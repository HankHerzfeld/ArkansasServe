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
			AdminLevel = AdminLevels.Student,
			GroupIds = groupIds,
			Status = "active",
			IsManaged = true,
			ManagedByUserId = ctx.UserId,
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

	private sealed record CreateVolunteerRequest(
		string? DisplayName, string? FirstName, string? LastName, string? PersonType,
		string Email, string? OrganizationId, List<string>? GroupIds);

	private sealed record SetTagRequest(string? Status, string? Note, DateTime? CompletedAt, DateTime? ExpiresAt);
}
