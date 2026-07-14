using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using User = ArkansasServe.Functions.Models.User;

namespace ArkansasServe.Functions.Functions;

public class MembershipFunctions(CosmosService cosmos, BlobService blob, AuthConfig authConfig, ILogger<MembershipFunctions> logger)
{
	// The platform/root tenant is never a joinable organization.
	private const string RootTenantId = "arkansas-serve-root";

	// Every organization the current person belongs to (one membership per org),
	// enriched with the org name and its groups so the client can build the
	// multi-org and group switchers.
	[Function("GetMyMemberships")]
	public async Task<HttpResponseData> GetMyMemberships(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/me/memberships")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var memberships = await cosmos.GetMembershipsByExternalIdAsync(ctx.UserId);

		var result = new List<object>();
		foreach (var m in memberships)
		{
			var orgId = string.IsNullOrWhiteSpace(m.OrganizationId) ? m.TenantId : m.OrganizationId!;
			var tenant = string.IsNullOrWhiteSpace(orgId) ? null : await cosmos.GetTenantAsync(orgId);
			result.Add(new
			{
				organizationId = orgId,
				organizationName = tenant?.Name ?? orgId,
				status = tenant?.Status,
				rbacEnabled = tenant?.RbacEnabled,
				allowGroupAdminAddVolunteers = tenant?.AllowGroupAdminAddVolunteers,
				allowProfileSelfEdit = tenant?.AllowProfileSelfEdit,
				adminLevel = m.AdminLevel,
				groupIds = m.GroupIds,
				groups = tenant?.Groups ?? new List<TenantGroup>(),
			});
		}

		return await HttpHelper.OkJson(req, result);
	}

	// Public organization directory: every active, joinable org, each flagged with
	// whether the current person already belongs to it. Any signed-in user may
	// browse; this is the volunteer self-service counterpart to the admin-gated
	// GET /manage/tenants listing.
	[Function("GetOrgDirectory")]
	public async Task<HttpResponseData> GetOrgDirectory(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/orgs")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var memberships = await cosmos.GetMembershipsByExternalIdAsync(ctx.UserId);
		var memberOrgIds = memberships
			.Select(m => string.IsNullOrWhiteSpace(m.OrganizationId) ? m.TenantId : m.OrganizationId!)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		var orgs = (await cosmos.GetAllTenantsAsync())
			.Where(t => string.Equals(t.Status, "active", StringComparison.OrdinalIgnoreCase)
				&& !string.Equals(t.Id, RootTenantId, StringComparison.OrdinalIgnoreCase))
			.Select(t => new
			{
				id = t.Id,
				name = t.Name,
				type = t.Type,
				logoUrl = blob.ResolveDisplayUrl("org-logos", t.LogoBlobName, t.LogoUrl),
				alreadyMember = memberOrgIds.Contains(t.Id),
			})
			.ToList();

		return await HttpHelper.OkJson(req, orgs);
	}

	// Public profile for one organization: branding + public contact info, whether
	// the caller already belongs, and the org's upcoming open events. Any signed-in
	// user may view. Empty fields are returned as-is for the client to omit.
	[Function("GetOrgProfile")]
	public async Task<HttpResponseData> GetOrgProfile(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/orgs/{id}")] HttpRequestData req,
		string id)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		if (string.IsNullOrWhiteSpace(id) || string.Equals(id, RootTenantId, StringComparison.OrdinalIgnoreCase))
			return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Organization not found");

		var tenant = await cosmos.GetTenantAsync(id);
		if (tenant == null || !string.Equals(tenant.Status, "active", StringComparison.OrdinalIgnoreCase))
			return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Organization not found");

		var memberships = await cosmos.GetMembershipsByExternalIdAsync(ctx.UserId);
		var alreadyMember = memberships.Any(m =>
			string.Equals(string.IsNullOrWhiteSpace(m.OrganizationId) ? m.TenantId : m.OrganizationId, id, StringComparison.OrdinalIgnoreCase));

		var now = DateTime.UtcNow;
		var upcomingEvents = (await cosmos.GetEventsByOrgAsync(id))
			.Where(e => e.StartDateTime >= now && string.Equals(e.Status, "Open", StringComparison.OrdinalIgnoreCase))
			.OrderBy(e => e.StartDateTime)
			.Take(20)
			.Select(e => new
			{
				id = e.Id,
				title = e.Title,
				category = e.Category,
				startDateTime = e.StartDateTime,
				location = e.Location,
				hoursValue = e.HoursValue,
				organizationId = e.OrganizationId,
			})
			.ToList();

		return await HttpHelper.OkJson(req, new
		{
			id = tenant.Id,
			name = tenant.Name,
			type = tenant.Type,
			description = tenant.Description,
			mission = tenant.Mission,
			website = tenant.Website,
			logoUrl = blob.ResolveDisplayUrl("org-logos", tenant.LogoBlobName, tenant.LogoUrl),
			contactEmail = tenant.ContactEmail,
			contactPhone = tenant.ContactPhone,
			address = tenant.Address,
			alreadyMember,
			upcomingEvents,
		});
	}

	// Self-service join: the current person adds themselves to an org as a Student.
	// Idempotent if already a member; adopts a matching admin-created managed
	// volunteer (email is unique per org) instead of creating a duplicate.
	[Function("JoinOrg")]
	public async Task<HttpResponseData> JoinOrg(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/me/memberships")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<JoinOrgRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.OrganizationId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "organizationId is required");

		var orgId = body.OrganizationId.Trim();
		if (string.Equals(orgId, RootTenantId, StringComparison.OrdinalIgnoreCase))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Cannot join this organization");

		var tenant = await cosmos.GetTenantAsync(orgId);
		if (tenant == null || !string.Equals(tenant.Status, "active", StringComparison.OrdinalIgnoreCase))
			return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Organization not found");

		// Already a signed-in member → idempotent success.
		var existing = await cosmos.GetUserByExternalIdAsync(ctx.UserId, orgId);
		if (existing != null)
			return await HttpHelper.OkJson(req, existing);

		// A global super already has access to every org via ResolveActorInOrgAsync.
		// Creating a self-joined Student membership here would only pollute the data and
		// cap them at Student in that org's authz — the exact stray-membership problem.
		// Return their effective (super) actor without persisting anything.
		if (await cosmos.IsGlobalSuperAsync(ctx.UserId, ctx.AdminLevel))
		{
			// Non-null for a confirmed global super (returns the membership or a synthetic super).
			var superActor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, orgId);
			return await HttpHelper.OkJson(req, superActor!);
		}

		var email = ctx.Email?.Trim().ToLowerInvariant();
		if (!string.IsNullOrWhiteSpace(email))
		{
			var byEmail = await cosmos.GetMembershipByEmailAsync(email, orgId);
			if (byEmail is { IsManaged: true })
			{
				// Adopt the admin-created managed record on self-join.
				byEmail.ExternalId = ctx.UserId;
				byEmail.IsManaged = false;
				byEmail.ManagedByUserId = null;
				if (string.IsNullOrWhiteSpace(byEmail.DisplayName)) byEmail.DisplayName = ctx.DisplayName;
				var adopted = await cosmos.UpsertUserWithPartitionFallbackAsync(byEmail);
				await TryMigrateAdoptedLogsAsync(adopted.Id, ctx.UserId);
				return await HttpHelper.OkJson(req, adopted);
			}
			if (byEmail != null)
				return await HttpHelper.Error(req, HttpStatusCode.Conflict, "A member with this email already exists in this organization");
		}

		var membership = new User
		{
			ExternalId = ctx.UserId,
			TenantId = orgId,
			OrganizationId = orgId,
			Email = email ?? string.Empty,
			DisplayName = ctx.DisplayName,
			AdminLevel = AdminLevels.Student,
			Status = "active",
			SelfJoined = true,
		};
		var created = await cosmos.CreateManagedVolunteerAsync(membership);
		return await HttpHelper.CreatedJson(req, created);
	}

	// Self-service leave: drop a membership the person added themselves. Only a
	// self-joined Student membership may be removed here; elevated memberships must
	// be removed by an admin through the role matrix.
	//
	// Finding 6 — BY DESIGN, decided 2026-07-14. An ADOPTED membership (adoption leaves
	// selfJoined:false) is refused here on purpose: it was built by an org — e.g. a school
	// adding a student to its roster — so the person may not remove themselves from it, and
	// Leave hard-deletes the doc. They must ask an admin, who removes it via the role
	// matrix. Only a membership the person opted into themselves is theirs to drop.
	// Do not "fix" the SelfJoined test away; the 403 below is the intended answer.
	[Function("LeaveOrg")]
	public async Task<HttpResponseData> LeaveOrg(
		[HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "manage/me/memberships/{orgId}")] HttpRequestData req,
		string orgId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		if (string.IsNullOrWhiteSpace(orgId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "orgId is required");

		var membership = await cosmos.GetUserByExternalIdAsync(ctx.UserId, orgId);
		if (membership == null)
			return await HttpHelper.OkJson(req, new { removed = true });

		// Split messages: the old single "cannot be removed here" gave no reason, which is
		// the UX half of Finding 6 — the refusal is correct, but the person couldn't tell
		// why or what to do next.
		if (!membership.SelfJoined)
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden,
				"This organization added you to their roster, so you can't remove yourself. Ask one of their administrators to remove you.");

		if (!string.Equals(membership.AdminLevel, AdminLevels.Student, StringComparison.OrdinalIgnoreCase))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden,
				"Roles above volunteer must be removed by an administrator.");

		await cosmos.DeleteUserWithFallbackAsync(membership.Id, membership.TenantId);
		return await HttpHelper.OkJson(req, new { removed = true });
	}

	// Move an adopted managed volunteer's service logs from the old studentId
	// (their doc Id) into the externalId partition. Best-effort: a failure here
	// must never break the join — the logs can be migrated on a later adoption.
	private async Task TryMigrateAdoptedLogsAsync(string oldStudentId, string externalId)
	{
		try
		{
			await cosmos.MigrateServiceLogsStudentIdAsync(oldStudentId, externalId);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to migrate service logs from {Old} to {New} on adoption", oldStudentId, externalId);
		}
	}

	private sealed record JoinOrgRequest(string OrganizationId);
}
