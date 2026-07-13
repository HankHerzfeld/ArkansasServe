using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using User = ArkansasServe.Functions.Models.User;

namespace ArkansasServe.Functions.Functions;

public class AdminFunctions(CosmosService cosmos, BlobService blob, AuthConfig authConfig, ILogger<AdminFunctions> logger)
{
	private const string RootTenantId = "arkansas-serve-root";

	[Function("GetTenants")]
	public async Task<HttpResponseData> GetTenants(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/tenants")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		if (await IsGlobalSuperAsync(ctx))
			return await HttpHelper.OkJson(req, await cosmos.GetAllTenantsAsync());

		// A non-super sees the tenants where they hold an OrganizationAdmin+ membership.
		var memberships = await cosmos.GetMembershipsByExternalIdAsync(ctx.UserId);
		var orgIds = memberships
			.Where(m => AdminLevels.AtLeast(m.AdminLevel, AdminLevels.OrganizationAdmin))
			.Select(m => m.OrganizationId ?? m.TenantId)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		var tenants = (await cosmos.GetAllTenantsAsync())
			.Where(t => orgIds.Contains(t.Id))
			.ToList();
		return await HttpHelper.OkJson(req, tenants);
	}

	[Function("GetTenant")]
	public async Task<HttpResponseData> GetTenant(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/tenants/{id}")] HttpRequestData req,
		string id)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		// A global super may read any tenant; otherwise the caller must hold an
		// OrganizationAdmin+ membership in it (same visibility rule as the tenant list).
		if (!await IsGlobalSuperAsync(ctx) && !await CanManageTenantAsync(ctx, id))
			return await Forbid(req);

		var tenant = await cosmos.GetTenantAsync(id);
		if (tenant == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Tenant not found");
		return await HttpHelper.OkJson(req, tenant);
	}

	[Function("CreateTenant")]
	public async Task<HttpResponseData> CreateTenant(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/tenants")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await IsGlobalSuperAsync(ctx)) return await Forbid(req);

		var body = await HttpHelper.ReadBody<Tenant>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Type))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "name and type are required");

		var created = await cosmos.CreateTenantAsync(body);
		return await HttpHelper.CreatedJson(req, created);
	}

	// Destructive: removes the tenant and cascades to its events + memberships.
	// Guarded three ways — SuperAdmin only, the root tenant can never be deleted, and
	// the caller must echo the exact tenant name via ?confirmName= (the UI's type-to-
	// confirm), so a stray call can't wipe an org. The deletion is audit-logged.
	[Function("DeleteTenant")]
	public async Task<HttpResponseData> DeleteTenant(
		[HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "manage/tenants/{id}")] HttpRequestData req,
		string id)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await IsGlobalSuperAsync(ctx)) return await Forbid(req);

		if (string.Equals(id, RootTenantId, StringComparison.OrdinalIgnoreCase))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "The root tenant cannot be deleted");

		var tenant = await cosmos.GetTenantAsync(id);
		if (tenant == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Tenant not found");

		var confirmName = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["confirmName"];
		if (!string.Equals(confirmName?.Trim(), tenant.Name?.Trim(), StringComparison.OrdinalIgnoreCase))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Confirmation name does not match the organization name");

		var (events, members) = await cosmos.DeleteTenantCascadeAsync(id);

		try
		{
			await cosmos.AppendAuditEventAsync(new AuditEvent
			{
				AdminUserId = ctx.UserId,
				TargetUserId = id,
				Action = "tenant.delete",
				Detail = $"{ctx.Email} deleted tenant '{tenant.Name}' ({id}); removed {events} event(s), {members} membership(s)",
			});
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Tenant {TenantId} deleted but the audit write failed", id);
		}

		return await HttpHelper.OkJson(req, new { deleted = true, tenantId = id, events, members });
	}

	[Function("GetAdminBackendContext")]
	public async Task<HttpResponseData> GetAdminBackendContext(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/backend/context")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var explicitOrg = QueryValue(req, "organizationId");
		var orgId = OrgFromQuery(req, ctx);
		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, orgId);

		// Multi-org: the Admin Backend bootstraps by calling this with NO org, so orgId
		// defaults to the caller's home/token org. A person who is an admin only in a
		// DIFFERENT org would resolve as a non-admin there and the page would 403/bounce.
		// When no org was explicitly requested, fall back to their strongest admin
		// membership so the page loads; the scope switcher then picks which org to manage.
		if (string.IsNullOrWhiteSpace(explicitOrg)
			&& (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.EventAdmin)))
		{
			var best = (await cosmos.GetMembershipsByExternalIdAsync(ctx.UserId))
				.Where(m => AdminLevels.AtLeast(m.AdminLevel, AdminLevels.EventAdmin))
				.OrderByDescending(m => AdminLevels.RankOf(m.AdminLevel))
				.FirstOrDefault();
			if (best != null) { orgId = best.OrganizationId ?? best.TenantId; actor = best; }
		}

		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.EventAdmin))
			return await Forbid(req);

		var tenant = string.IsNullOrWhiteSpace(orgId) ? null : await cosmos.GetTenantAsync(orgId);
		return await HttpHelper.OkJson(req, new
		{
			user = actor,
			canManageDemoUsers = await IsGlobalSuperAsync(ctx),
			tenant,
			// Signed display URL for the current logo (org-logos is private) so the edit form
			// can preview an uploaded logo. Null when there is no logo.
			logoDisplayUrl = tenant == null ? null : blob.ResolveDisplayUrl("org-logos", tenant.LogoBlobName, tenant.LogoUrl),
		});
	}

	[Function("GetAdminScopedUsers")]
	public async Task<HttpResponseData> GetAdminScopedUsers(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/backend/users")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var orgId = QueryValue(req, "tenantId") ?? OrgFromQuery(req, ctx);
		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, orgId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.EventAdmin))
			return await Forbid(req);

		if (string.IsNullOrWhiteSpace(orgId))
			return await HttpHelper.OkJson(req, Array.Empty<User>());

		var users = await cosmos.GetUsersForAdminScopeAsync(orgId);
		return await HttpHelper.OkJson(req, users);
	}

	[Function("UpdateUserAccess")]
	public async Task<HttpResponseData> UpdateUserAccess(
		[HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "manage/backend/users/{id}/access")] HttpRequestData req,
		string id)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<UpdateUserAccessRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.TenantId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "tenantId is required");

		var target = await cosmos.GetUserByIdAsync(id, body.TenantId);
		if (target == null)
			return await HttpHelper.Error(req, HttpStatusCode.NotFound, "User not found");

		// Authorize by the actor's membership IN THE TARGET USER'S ORG.
		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, target.TenantId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.OrganizationAdmin))
			return await Forbid(req);

		var actorRank = AdminLevels.RankOf(actor.AdminLevel);
		var isSuper = actorRank >= AdminLevels.RankOf(AdminLevels.SuperAdmin);
		if (!isSuper)
		{
			// A non-super may only assign a level below their own, and may not
			// modify a user who already holds a level at or above their own.
			if (actorRank <= AdminLevels.RankOf(body.AdminLevel))
				return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Cannot assign a level at or above your own");
			if (actorRank <= AdminLevels.RankOf(target.AdminLevel))
				return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Cannot modify a user at or above your own level");
		}

		target.AdminLevel = body.AdminLevel;
		target.OrganizationId = body.OrganizationId ?? target.OrganizationId ?? target.TenantId;
		target.GroupIds = body.GroupIds ?? [];
		target.EventAdminEventIds = body.EventAdminEventIds ?? [];

		var updated = await cosmos.UpsertUserAsync(target);
		return await HttpHelper.OkJson(req, updated);
	}

	[Function("GetTenantGroups")]
	public async Task<HttpResponseData> GetTenantGroups(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/backend/tenants/{tenantId}/groups")] HttpRequestData req,
		string tenantId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await CanManageTenantAsync(ctx, tenantId)) return await Forbid(req);

		var tenant = await cosmos.GetTenantAsync(tenantId);
		if (tenant == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Tenant not found");

		return await HttpHelper.OkJson(req, tenant.Groups);
	}

	[Function("CreateTenantGroup")]
	public async Task<HttpResponseData> CreateTenantGroup(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/backend/tenants/{tenantId}/groups")] HttpRequestData req,
		string tenantId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await CanManageTenantAsync(ctx, tenantId)) return await Forbid(req);

		var body = await HttpHelper.ReadBody<CreateGroupRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.Name))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "name is required");

		var tenant = await cosmos.GetTenantAsync(tenantId);
		if (tenant == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Tenant not found");

		tenant.Groups.Add(new TenantGroup
		{
			Name = body.Name,
			Status = string.IsNullOrWhiteSpace(body.Status) ? "active" : body.Status,
			OrganizationId = tenantId,
		});

		var updated = await cosmos.UpdateTenantAsync(tenant);
		return await HttpHelper.OkJson(req, updated.Groups);
	}

	[Function("GetOrgLogoUploadToken")]
	public async Task<HttpResponseData> GetOrgLogoUploadToken(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/backend/tenants/{tenantId}/logo-upload-token")] HttpRequestData req,
		string tenantId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await CanManageTenantAsync(ctx, tenantId)) return await Forbid(req);

		var body = await HttpHelper.ReadBody<LogoUploadTokenRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.FileName))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "fileName is required");

		// Store under the org's id so logos are grouped per tenant. org-logos is private;
		// the readable URL is a short-lived SAS minted at display time (see ResolveDisplayUrl).
		var blobName = BlobService.GenerateBlobName($"logos/{tenantId}", body.FileName);
		var sasUrl = blob.GenerateUploadSasToken("org-logos", blobName);
		return await HttpHelper.OkJson(req, new { sasUrl, blobName });
	}

	[Function("UpdateTenantSettings")]
	public async Task<HttpResponseData> UpdateTenantSettings(
		[HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "manage/backend/tenants/{tenantId}")] HttpRequestData req,
		string tenantId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await CanManageTenantAsync(ctx, tenantId)) return await Forbid(req);

		var body = await HttpHelper.ReadBody<UpdateTenantRequest>(req);
		if (body == null) return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Invalid request body");

		var tenant = await cosmos.GetTenantAsync(tenantId);
		if (tenant == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Tenant not found");

		if (!string.IsNullOrWhiteSpace(body.Name)) tenant.Name = body.Name;
		if (!string.IsNullOrWhiteSpace(body.Status)) tenant.Status = body.Status;
		if (body.RbacEnabled.HasValue) tenant.RbacEnabled = body.RbacEnabled.Value;
		if (body.AllowGroupAdminAddVolunteers.HasValue) tenant.AllowGroupAdminAddVolunteers = body.AllowGroupAdminAddVolunteers.Value;
		if (body.AllowProfileSelfEdit.HasValue) tenant.AllowProfileSelfEdit = body.AllowProfileSelfEdit.Value;
		// Public profile fields — non-null means "set" (empty string clears).
		if (body.Description != null) tenant.Description = body.Description;
		if (body.Mission != null) tenant.Mission = body.Mission;
		if (body.Website != null) tenant.Website = body.Website;
		if (body.ContactEmail != null) tenant.ContactEmail = body.ContactEmail;
		if (body.ContactPhone != null) tenant.ContactPhone = body.ContactPhone;
		if (body.Address != null) tenant.Address = body.Address;
		if (body.LogoUrl != null) tenant.LogoUrl = body.LogoUrl;
		if (body.LogoBlobName != null) tenant.LogoBlobName = body.LogoBlobName.Length == 0 ? null : body.LogoBlobName;

		var updated = await cosmos.UpdateTenantAsync(tenant);
		return await HttpHelper.OkJson(req, updated);
	}

	[Function("GetDemoUsers")]
	public async Task<HttpResponseData> GetDemoUsers(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/backend/demo-users")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await IsGlobalSuperAsync(ctx)) return await Forbid(req);

		var orgId = OrgFromQuery(req, ctx);
		if (string.IsNullOrWhiteSpace(orgId))
			return await HttpHelper.OkJson(req, Array.Empty<User>());

		var demoUsers = await cosmos.GetDemoUsersByTenantAsync(orgId);
		return await HttpHelper.OkJson(req, demoUsers);
	}

	[Function("ResetDemoUsers")]
	public async Task<HttpResponseData> ResetDemoUsers(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/backend/demo-users/reset")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await IsGlobalSuperAsync(ctx)) return await Forbid(req);

		var orgId = OrgFromQuery(req, ctx);
		if (string.IsNullOrWhiteSpace(orgId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Tenant scope not found");

		await cosmos.DeleteDemoUsersByTenantAsync(orgId);
		var created = await cosmos.UpsertDemoUsersAsync(orgId, BuildDefaultDemoUsers(orgId));
		return await HttpHelper.OkJson(req, created);
	}

	[Function("GetDbContainers")]
	public async Task<HttpResponseData> GetDbContainers(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/db/containers")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await IsGlobalSuperAsync(ctx)) return await Forbid(req);

		return await HttpHelper.OkJson(req, cosmos.QueryableContainers);
	}

	// Manual, read-only database query for SuperAdmins. Cosmos' SQL API cannot
	// mutate data, and the container is allow-listed server-side, so this is a
	// read-only inspection tool by construction.
	[Function("RunDbQuery")]
	public async Task<HttpResponseData> RunDbQuery(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/db/query")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await IsGlobalSuperAsync(ctx)) return await Forbid(req);

		var body = await HttpHelper.ReadBody<DbQueryRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.Container) || string.IsNullOrWhiteSpace(body.Query))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "container and query are required");

		if (!body.Query.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Only SELECT queries are allowed");

		try
		{
			var rows = await cosmos.RunReadQueryAsync(body.Container, body.Query, body.MaxItems ?? 50);
			return await HttpHelper.OkJson(req, new { container = body.Container, count = rows.Count, rows });
		}
		catch (ArgumentException ex)
		{
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, ex.Message);
		}
		catch (CosmosException ex)
		{
			logger.LogWarning(ex, "DB console query failed for container {Container}", body.Container);
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, $"Query error: {ex.ResponseBody ?? ex.Message}");
		}
	}

	// ── Authorization helpers ───────────────────────────────────────────────

	// A person is a platform super if their token level is SuperAdmin or any of
	// their memberships is SuperAdmin (e.g. granted via the role matrix).
	private Task<bool> IsGlobalSuperAsync(UserContext ctx) =>
		cosmos.IsGlobalSuperAsync(ctx.UserId, ctx.AdminLevel);

	// True when the caller may manage the given tenant: a super, or an
	// OrganizationAdmin+ membership in that specific tenant.
	private async Task<bool> CanManageTenantAsync(UserContext ctx, string tenantId)
	{
		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, tenantId);
		return actor != null && AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.OrganizationAdmin);
	}

	// The org the request targets: ?organizationId=, else the caller's token org.
	private static string OrgFromQuery(HttpRequestData req, UserContext ctx)
	{
		var requested = QueryValue(req, "organizationId");
		if (!string.IsNullOrWhiteSpace(requested)) return requested;
		return string.IsNullOrWhiteSpace(ctx.TenantId) ? RootTenantId : ctx.TenantId;
	}

	private static string? QueryValue(HttpRequestData req, string key)
	{
		var value = System.Web.HttpUtility.ParseQueryString(req.Url.Query)[key];
		return string.IsNullOrWhiteSpace(value) ? null : value;
	}

	private static Task<HttpResponseData> Forbid(HttpRequestData req)
		=> HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

	private static List<User> BuildDefaultDemoUsers(string tenantId)
	{
		var levels = new[] { AdminLevels.SuperAdmin, AdminLevels.OrganizationAdmin, AdminLevels.GroupAdmin, AdminLevels.EventAdmin, AdminLevels.Student };
		var users = new List<User>();
		foreach (var level in levels)
		{
			for (var i = 1; i <= 2; i++)
			{
				users.Add(new User
				{
					Id = $"demo-{level.ToLowerInvariant()}-{i}",
					ExternalId = $"demo-{level.ToLowerInvariant()}-{i}",
					TenantId = tenantId,
					OrganizationId = tenantId,
					AdminLevel = level,
					DisplayName = $"Demo {level} {i}",
					Email = $"demo.{level.ToLowerInvariant()}.{i}@arkansasserve.local",
					IsDemoUser = true,
					DemoUserType = level,
				});
			}
		}

		return users;
	}

	private sealed record UpdateUserAccessRequest(
		string TenantId,
		string AdminLevel,
		string? OrganizationId,
		List<string>? GroupIds,
		List<string>? EventAdminEventIds);

	private sealed record CreateGroupRequest(string Name, string? Status);

	private sealed record DbQueryRequest(string Container, string Query, int? MaxItems);

	private sealed record LogoUploadTokenRequest(string FileName);

	private sealed record UpdateTenantRequest(
		string? Name, string? Status, bool? RbacEnabled, bool? AllowGroupAdminAddVolunteers, bool? AllowProfileSelfEdit,
		string? Description, string? Mission, string? Website,
		string? ContactEmail, string? ContactPhone, string? Address, string? LogoUrl, string? LogoBlobName);
}
