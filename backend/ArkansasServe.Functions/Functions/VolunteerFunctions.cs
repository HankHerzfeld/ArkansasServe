using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using User = ArkansasServe.Functions.Models.User;

namespace ArkansasServe.Functions.Functions;

public class VolunteerFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<VolunteerFunctions> logger)
{
	[Function("GetVolunteers")]
	public async Task<HttpResponseData> GetVolunteers(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/volunteers")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
		var orgId = string.IsNullOrWhiteSpace(query["organizationId"]) ? ctx.TenantId : query["organizationId"]!;

		// Per-org: the caller must be a GroupAdmin+ in the target org.
		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, orgId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.GroupAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var groupId = query["groupId"];
		var volunteers = await cosmos.GetVolunteersByTenantAsync(orgId, groupId);

		// A GroupAdmin only sees volunteers in the groups they administer.
		if (AdminLevels.RankOf(actor.AdminLevel) == AdminLevels.RankOf(AdminLevels.GroupAdmin))
		{
			var own = new HashSet<string>(actor.GroupIds ?? []);
			volunteers = volunteers.Where(v => v.GroupIds.Any(own.Contains)).ToList();
		}

		return await HttpHelper.OkJson(req, volunteers);
	}

	[Function("CreateVolunteer")]
	public async Task<HttpResponseData> CreateVolunteer(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/volunteers")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<CreateVolunteerRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.Email))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "email is required");

		// Accept structured first/last (preferred) or a legacy single displayName.
		var displayName = User.ComposeName(body.FirstName, body.LastName, body.DisplayName);
		if (string.IsNullOrWhiteSpace(displayName))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "a name (first/last or displayName) is required");

		var orgId = string.IsNullOrWhiteSpace(body.OrganizationId) ? ctx.TenantId : body.OrganizationId!;

		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, orgId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.GroupAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var email = body.Email.Trim().ToLowerInvariant();
		var groupIds = body.GroupIds ?? [];

		// GroupAdmins may only add within their own groups, and only org-wide (no
		// group) when the tenant setting allows it.
		if (AdminLevels.RankOf(actor.AdminLevel) == AdminLevels.RankOf(AdminLevels.GroupAdmin))
		{
			var own = new HashSet<string>(actor.GroupIds ?? []);
			if (groupIds.Count == 0)
			{
				var tenant = await cosmos.GetTenantAsync(orgId);
				if (tenant is { AllowGroupAdminAddVolunteers: false })
					return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Group admins cannot add organization-wide volunteers here");
			}
			else if (!groupIds.All(own.Contains))
			{
				return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Cannot add a volunteer to a group you do not administer");
			}
		}

		// Email is unique per organization.
		var existing = await cosmos.GetMembershipByEmailAsync(email, orgId);
		if (existing != null)
			return await HttpHelper.Error(req, HttpStatusCode.Conflict, "A volunteer with this email already exists in this organization");

		var volunteer = new User
		{
			ExternalId = string.Empty,
			TenantId = orgId,
			OrganizationId = orgId,
			Email = email,
			FirstName = body.FirstName?.Trim(),
			LastName = body.LastName?.Trim(),
			DisplayName = displayName,
			// PersonType is a starting hint from the admin; the person confirms and
			// completes their own intake on first login (see IntakeValidation).
			PersonType = PersonTypes.IsValid(body.PersonType) ? body.PersonType : null,
			AdminLevel = AdminLevels.Member,
			GroupIds = groupIds,
			Status = "active",
			IsManaged = true,
			ManagedByUserId = ctx.UserId,
			// A volunteer a demo admin adds (impersonation is demo-only) is a demo user.
			IsDemoUser = ctx.IsImpersonating,
		};
		volunteer.ProfileComplete = IntakeValidation.IsComplete(volunteer);

		var created = await cosmos.CreateManagedVolunteerAsync(volunteer);
		return await HttpHelper.CreatedJson(req, created);
	}

	// ── PUT /api/manage/volunteers/{memberId}/tags/{tagId} ────────────────────

	/// <summary>
	/// Sets one person's state against one of their org's credentials (#11).
	///
	/// Addressed by MemberId — the per-org User doc id — for the same reason group
	/// registration is: a managed volunteer has no account, and a tag has to be recordable
	/// against someone who has never signed in. That is most of the point.
	///
	/// GroupAdmin+ in the person's own org, matching the volunteer roster endpoints: whoever
	/// can add a volunteer can record that they signed the waiver. Deliberately NOT the
	/// person themselves — these are admin-attested facts, and a self-attested background
	/// check is not a background check.
	/// </summary>
	[Function("SetVolunteerTag")]
	public async Task<HttpResponseData> SetVolunteerTag(
		[HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "manage/volunteers/{memberId}/tags/{tagId}")] HttpRequestData req,
		string memberId, string tagId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
		var orgId = string.IsNullOrWhiteSpace(query["organizationId"]) ? ctx.TenantId : query["organizationId"]!;

		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, orgId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.GroupAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var body = await HttpHelper.ReadBody<SetTagRequest>(req);
		if (body == null || !TagStatuses.IsValid(body.Status))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "status must be None, Pending or Complete");

		var tenant = await cosmos.GetTenantAsync(orgId);
		if (tenant == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Organization not found");

		// The tag must be one THIS org defined. Without this an admin could pin another org's
		// tag id onto their own member, producing a credential that no page can render and no
		// gate can evaluate.
		var definition = tenant.UserTags.FirstOrDefault(t => t.Id == tagId);
		if (definition == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "This organization has no such tag");

		var member = await cosmos.GetUserByIdAsync(memberId, orgId);
		if (member == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "That person is not a member of this organization");

		var state = member.Tags.FirstOrDefault(t => t.TagId == tagId);
		if (state == null) { state = new UserTagState { TagId = tagId }; member.Tags.Add(state); }

		var wasComplete = string.Equals(state.Status, TagStatuses.Complete, StringComparison.OrdinalIgnoreCase);
		state.Status = body.Status;
		state.Note = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();

		if (string.Equals(body.Status, TagStatuses.Complete, StringComparison.OrdinalIgnoreCase))
		{
			// Completing it (re)stamps the dates. An explicit completedAt is honoured so an
			// admin can record a waiver signed on paper last week rather than today.
			state.CompletedAt = body.CompletedAt ?? DateTime.UtcNow;
			// Expiry is stamped from the policy in force NOW and then left alone. Changing the
			// org's expiry rule later must not retroactively expire people who were compliant
			// under the rule they were actually told about.
			state.ExpiresAt = body.ExpiresAt
				?? (definition.ExpiresAfterDays.HasValue
					? state.CompletedAt.Value.AddDays(definition.ExpiresAfterDays.Value)
					: null);
		}
		else
		{
			// Moving off Complete clears the dates: a completion date on a Pending credential
			// is a contradiction, and it is the kind that later reads as "they had it once".
			state.CompletedAt = null;
			state.ExpiresAt = null;
		}

		var saved = await cosmos.UpsertUserWithPartitionFallbackAsync(member);
		logger.LogInformation("[UserTags] {Actor} set \"{Label}\" to {Status} for member {MemberId} in org {OrgId} (was complete: {WasComplete})",
			ctx.UserId, definition.Label, state.Status, memberId, orgId, wasComplete);
		return await HttpHelper.OkJson(req, saved.Tags);
	}

	// ── GET /api/manage/me/tags?organizationId= ───────────────────────────────

	/// <summary>
	/// The caller's own credential requirements in one org: what the org asks for, where they
	/// stand, and whether they can satisfy it themselves right now (#19).
	///
	/// Exists so a refusal can become a PROMPT. Being told "still needed: Liability waiver. Ask an
	/// admin" is a dead end for the one credential the volunteer is actually able to provide; the
	/// page uses this to offer signing it on the spot instead.
	/// </summary>
	[Function("GetMyTags")]
	public async Task<HttpResponseData> GetMyTags(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/me/tags")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
		var orgId = string.IsNullOrWhiteSpace(query["organizationId"]) ? ctx.TenantId : query["organizationId"]!;

		var me = await cosmos.GetUserByExternalIdAsync(ctx.UserId, orgId);
		var tenant = await cosmos.GetTenantAsync(orgId);
		if (tenant == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Organization not found");

		var now = DateTime.UtcNow;
		var isMinor = me != null && IntakeValidation.IsMinor(me);

		var tags = tenant.UserTags
			.Where(t => string.Equals(t.Status, "active", StringComparison.OrdinalIgnoreCase))
			.Select(t =>
			{
				var state = me?.Tags.FirstOrDefault(s => string.Equals(s.TagId, t.Id, StringComparison.OrdinalIgnoreCase));
				var current = state?.IsCurrentAt(now) == true;
				return (object)new
				{
					id = t.Id,
					label = t.Label,
					description = t.Description,
					enforcement = t.Enforcement,
					evidence = t.Evidence,
					status = state?.Status ?? TagStatuses.None,
					completedAt = state?.CompletedAt,
					expiresAt = state?.ExpiresAt,
					current,
					// Everything the client needs to decide whether to offer a "sign it now" prompt,
					// rather than re-deriving the policy in JavaScript.
					canSelfAttest = !current && t.SelfAttestable && !isMinor
						&& string.Equals(t.Evidence, TagEvidence.Attestation, StringComparison.OrdinalIgnoreCase),
				};
			})
			.ToList();

		return await HttpHelper.OkJson(req, new { organizationId = orgId, isMinor, tags });
	}

	// ── POST /api/manage/me/tags/{tagId}/attest?organizationId= ───────────────

	/// <summary>
	/// The volunteer signs one of their org's credentials themselves (#19).
	///
	/// Narrow on purpose — three independent guards, because the general rule stays "tags are
	/// admin-attested": the tag must be opted in (<see cref="TenantUserTag.SelfAttestable"/>), it
	/// must be an attestation rather than a document (a file cannot be produced by ticking a box),
	/// and the signer must be an adult. A MINOR cannot sign for themselves; theirs is recorded by
	/// an admin, so refusing here strands nobody.
	/// </summary>
	[Function("AttestMyTag")]
	public async Task<HttpResponseData> AttestMyTag(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/me/tags/{tagId}/attest")] HttpRequestData req,
		string tagId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
		var orgId = string.IsNullOrWhiteSpace(query["organizationId"]) ? ctx.TenantId : query["organizationId"]!;

		var tenant = await cosmos.GetTenantAsync(orgId);
		if (tenant == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Organization not found");

		var definition = tenant.UserTags.FirstOrDefault(t => string.Equals(t.Id, tagId, StringComparison.OrdinalIgnoreCase));
		if (definition == null || !string.Equals(definition.Status, "active", StringComparison.OrdinalIgnoreCase))
			return await HttpHelper.Error(req, HttpStatusCode.NotFound, "This organization has no such credential");

		if (!definition.SelfAttestable)
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden,
				"This credential is recorded by an organization admin, not by you.");

		if (!string.Equals(definition.Evidence, TagEvidence.Attestation, StringComparison.OrdinalIgnoreCase))
			return await HttpHelper.Error(req, HttpStatusCode.Conflict,
				"This credential needs a document to be uploaded, so it can't be agreed to here.");

		var me = await cosmos.GetUserByExternalIdAsync(ctx.UserId, orgId);
		if (me == null)
			return await HttpHelper.Error(req, HttpStatusCode.NotFound, "You are not a member of this organization");

		if (IntakeValidation.IsMinor(me))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden,
				"A parent or guardian has to agree to this on your behalf. Ask an admin to record it.");

		var now = DateTime.UtcNow;
		var state = me.Tags.FirstOrDefault(t => string.Equals(t.TagId, tagId, StringComparison.OrdinalIgnoreCase));
		if (state == null) { state = new UserTagState { TagId = definition.Id }; me.Tags.Add(state); }

		state.Status = TagStatuses.Complete;
		state.CompletedAt = now;
		// Same rule as the admin path: stamped from the policy in force NOW and then left alone,
		// so changing the org's expiry later cannot retroactively expire someone.
		state.ExpiresAt = definition.ExpiresAfterDays.HasValue ? now.AddDays(definition.ExpiresAfterDays.Value) : null;
		state.Note = "Agreed to online by the volunteer";

		await cosmos.UpsertUserWithPartitionFallbackAsync(me);
		logger.LogInformation("[UserTags] {Actor} self-attested \"{Label}\" in org {OrgId}", ctx.UserId, definition.Label, orgId);

		return await HttpHelper.OkJson(req, new
		{
			tagId = definition.Id,
			label = definition.Label,
			status = state.Status,
			completedAt = state.CompletedAt,
			expiresAt = state.ExpiresAt,
		});
	}

	private sealed record CreateVolunteerRequest(
		string? DisplayName, string? FirstName, string? LastName, string? PersonType,
		string Email, string? OrganizationId, List<string>? GroupIds);

	private sealed record SetTagRequest(string? Status, string? Note, DateTime? CompletedAt, DateTime? ExpiresAt);
}
