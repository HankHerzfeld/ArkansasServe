using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using User = ArkansasServe.Functions.Models.User;

namespace ArkansasServe.Functions.Functions;

// SuperAdmin role matrix: see every person's org/group/role assignments and
// assign/unassign freely. Assign/unassign are also usable by an OrganizationAdmin
// within their own org (subject to the same rank rules as UpdateUserAccess).
public class MatrixFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<MatrixFunctions> logger)
{
	private const int DefaultPageSize = 50;
	private const int MaxPageSize = 100;

	// One page of the role matrix, flattened to one row per membership document.
	// Filter server-side with ?organizationId= (scopes to that org's partition)
	// and/or ?search= (name/email), and page with ?continuationToken=&pageSize=.
	// Returns { items, continuationToken } — token is null on the last page.
	[Function("GetRoleMatrix")]
	public async Task<HttpResponseData> GetRoleMatrix(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/matrix")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await cosmos.IsGlobalSuperAsync(ctx.UserId, ctx.Role)) return await Forbid(req);

		var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
		var organizationId = query["organizationId"];
		var search = query["search"];
		var continuationToken = query["continuationToken"];
		var pageSize = ParsePageSize(query["pageSize"]);

		var (users, nextToken) = await cosmos.QueryMembershipsPageAsync(organizationId, search, pageSize, continuationToken);

		var tenantNames = (await cosmos.GetAllTenantsAsync())
			.ToDictionary(t => t.Id, t => t.Name, StringComparer.OrdinalIgnoreCase);

		var items = users
			.Select(u =>
			{
				var orgId = string.IsNullOrWhiteSpace(u.OrganizationId) ? u.TenantId : u.OrganizationId!;
				return new
				{
					userId = u.Id,
					externalId = u.ExternalId,
					email = u.Email,
					displayName = string.IsNullOrWhiteSpace(u.DisplayName) ? u.Email : u.DisplayName,
					organizationId = orgId,
					organizationName = tenantNames.GetValueOrDefault(orgId, orgId),
					adminLevel = u.AdminLevel,
					groupIds = u.GroupIds,
					isManaged = u.IsManaged,
				};
			})
			.ToList();

		return await HttpHelper.OkJson(req, new { items, continuationToken = nextToken });
	}

	private static int ParsePageSize(string? raw)
	{
		if (int.TryParse(raw, out var n) && n > 0)
			return Math.Min(n, MaxPageSize);
		return DefaultPageSize;
	}

	[Function("UpsertMembership")]
	public async Task<HttpResponseData> UpsertMembership(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/backend/memberships")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<UpsertMembershipRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.OrganizationId) || string.IsNullOrWhiteSpace(body.AdminLevel) || string.IsNullOrWhiteSpace(body.Email))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "organizationId, adminLevel and email are required");

		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.Role, body.OrganizationId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.OrganizationAdmin))
			return await Forbid(req);

		var actorRank = AdminLevels.RankOf(actor.AdminLevel);
		var isSuper = actorRank >= AdminLevels.RankOf(AdminLevels.SuperAdmin);
		if (!isSuper && actorRank <= AdminLevels.RankOf(body.AdminLevel))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Cannot assign a level at or above your own");

		var email = body.Email.Trim().ToLowerInvariant();
		var membership = string.IsNullOrWhiteSpace(body.ExternalId)
			? null
			: await cosmos.GetUserByExternalIdAsync(body.ExternalId, body.OrganizationId);
		membership ??= await cosmos.GetMembershipByEmailAsync(email, body.OrganizationId);

		if (membership != null)
		{
			if (!isSuper && actorRank <= AdminLevels.RankOf(membership.AdminLevel))
				return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Cannot modify a user at or above your own level");

			membership.AdminLevel = body.AdminLevel;
			membership.Role = AdminLevels.ToLegacyRole(body.AdminLevel);
			membership.GroupIds = body.GroupIds ?? [];
			var updated = await cosmos.UpsertUserAsync(membership);
			return await HttpHelper.OkJson(req, updated);
		}

		var created = new User
		{
			ExternalId = body.ExternalId ?? string.Empty,
			TenantId = body.OrganizationId,
			OrganizationId = body.OrganizationId,
			Email = email,
			DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? email : body.DisplayName.Trim(),
			AdminLevel = body.AdminLevel,
			Role = AdminLevels.ToLegacyRole(body.AdminLevel),
			GroupIds = body.GroupIds ?? [],
			Status = "active",
			IsManaged = string.IsNullOrWhiteSpace(body.ExternalId),
			ManagedByUserId = string.IsNullOrWhiteSpace(body.ExternalId) ? ctx.UserId : null,
		};
		var result = await cosmos.CreateManagedVolunteerAsync(created);
		return await HttpHelper.CreatedJson(req, result);
	}

	[Function("DeleteMembership")]
	public async Task<HttpResponseData> DeleteMembership(
		[HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "manage/backend/memberships/{id}")] HttpRequestData req,
		string id)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var tenantId = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["tenantId"];
		if (string.IsNullOrWhiteSpace(tenantId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "tenantId is required");

		var target = await cosmos.GetUserByIdAsync(id, tenantId);
		if (target == null)
			return await HttpHelper.OkJson(req, new { removed = true });

		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.Role, tenantId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.OrganizationAdmin))
			return await Forbid(req);

		var actorRank = AdminLevels.RankOf(actor.AdminLevel);
		if (actorRank < AdminLevels.RankOf(AdminLevels.SuperAdmin) && actorRank <= AdminLevels.RankOf(target.AdminLevel))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Cannot remove a user at or above your own level");

		await cosmos.DeleteUserWithFallbackAsync(id, tenantId);
		return await HttpHelper.OkJson(req, new { removed = true });
	}

	private static Task<HttpResponseData> Forbid(HttpRequestData req)
		=> HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

	private sealed record UpsertMembershipRequest(
		string Email,
		string OrganizationId,
		string AdminLevel,
		string? ExternalId,
		string? DisplayName,
		List<string>? GroupIds);
}
