using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using User = ArkansasServe.Functions.Models.User;

namespace ArkansasServe.Functions.Functions;

public class AdminFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<AdminFunctions> logger)
{
	private const string ArkansasServeEmailDomain = "@arkansasserve.com";

	private static readonly IReadOnlyDictionary<string, int> AdminRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
	{
		["Student"] = 0,
		["EventAdmin"] = 1,
		["GroupAdmin"] = 2,
		["OrganizationAdmin"] = 3,
		["SuperAdmin"] = 4,
	};

	[Function("GetTenants")]
	public async Task<HttpResponseData> GetTenants(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin/tenants")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var actor = await GetOrCreateActorAsync(ctx);
		if (!HasAdminAccess(actor))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var tenants = IsSuperAdmin(actor)
			? await cosmos.GetAllTenantsAsync()
			: [.. (await cosmos.GetAllTenantsAsync()).Where(t => string.Equals(t.Id, actor.OrganizationId ?? actor.TenantId, StringComparison.OrdinalIgnoreCase))];
		return await HttpHelper.OkJson(req, tenants);
	}

	[Function("CreateTenant")]
	public async Task<HttpResponseData> CreateTenant(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "admin/tenants")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var actor = await GetOrCreateActorAsync(ctx);
		if (!IsSuperAdmin(actor))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var body = await HttpHelper.ReadBody<Tenant>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Type))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "name and type are required");

		var created = await cosmos.CreateTenantAsync(body);
		return await HttpHelper.CreatedJson(req, created);
	}

	[Function("GetAdminBackendContext")]
	public async Task<HttpResponseData> GetAdminBackendContext(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin/backend/context")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var actor = await GetOrCreateActorAsync(ctx);
		if (!HasAdminAccess(actor))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var tenantId = actor.OrganizationId ?? actor.TenantId;
		var tenant = string.IsNullOrWhiteSpace(tenantId) ? null : await cosmos.GetTenantAsync(tenantId);
		return await HttpHelper.OkJson(req, new
		{
			user = actor,
			canManageDemoUsers = IsSuperAdmin(actor),
			tenant,
		});
	}

	[Function("GetAdminScopedUsers")]
	public async Task<HttpResponseData> GetAdminScopedUsers(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin/backend/users")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var actor = await GetOrCreateActorAsync(ctx);
		if (!HasAdminAccess(actor))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var tenantId = actor.OrganizationId ?? actor.TenantId;
		if (string.IsNullOrWhiteSpace(tenantId))
			return await HttpHelper.OkJson(req, Array.Empty<User>());

		var users = await cosmos.GetUsersForAdminScopeAsync(tenantId);
		return await HttpHelper.OkJson(req, users);
	}

	[Function("UpdateUserAccess")]
	public async Task<HttpResponseData> UpdateUserAccess(
		[HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "admin/backend/users/{id}/access")] HttpRequestData req,
		string id)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var actor = await GetOrCreateActorAsync(ctx);
		if (!HasAdminAccess(actor))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var body = await HttpHelper.ReadBody<UpdateUserAccessRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.TenantId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "tenantId is required");

		var target = await cosmos.GetUserByIdAsync(id, body.TenantId);
		if (target == null)
			return await HttpHelper.Error(req, HttpStatusCode.NotFound, "User not found");

		if (!CanManageTenant(actor, target.TenantId))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		if (!CanAssignLevel(actor, body.AdminLevel))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Cannot assign this level");

		target.AdminLevel = body.AdminLevel;
		target.Role = MapAdminLevelToLegacyRole(body.AdminLevel);
		target.OrganizationId = body.OrganizationId ?? target.OrganizationId ?? target.TenantId;
		target.GroupIds = body.GroupIds ?? [];
		target.EventAdminEventIds = body.EventAdminEventIds ?? [];

		var updated = await cosmos.UpsertUserAsync(target);
		return await HttpHelper.OkJson(req, updated);
	}

	[Function("GetTenantGroups")]
	public async Task<HttpResponseData> GetTenantGroups(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin/backend/tenants/{tenantId}/groups")] HttpRequestData req,
		string tenantId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var actor = await GetOrCreateActorAsync(ctx);
		if (!CanManageTenant(actor, tenantId))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var tenant = await cosmos.GetTenantAsync(tenantId);
		if (tenant == null)
			return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Tenant not found");

		return await HttpHelper.OkJson(req, tenant.Groups);
	}

	[Function("CreateTenantGroup")]
	public async Task<HttpResponseData> CreateTenantGroup(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "admin/backend/tenants/{tenantId}/groups")] HttpRequestData req,
		string tenantId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var actor = await GetOrCreateActorAsync(ctx);
		if (!CanManageTenant(actor, tenantId) || !CanAssignLevel(actor, "GroupAdmin"))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var body = await HttpHelper.ReadBody<CreateGroupRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.Name))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "name is required");

		var tenant = await cosmos.GetTenantAsync(tenantId);
		if (tenant == null)
			return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Tenant not found");

		tenant.Groups.Add(new TenantGroup
		{
			Name = body.Name,
			Status = string.IsNullOrWhiteSpace(body.Status) ? "active" : body.Status,
			OrganizationId = tenantId,
		});

		var updated = await cosmos.UpdateTenantAsync(tenant);
		return await HttpHelper.OkJson(req, updated.Groups);
	}

	[Function("UpdateTenantSettings")]
	public async Task<HttpResponseData> UpdateTenantSettings(
		[HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "admin/backend/tenants/{tenantId}")] HttpRequestData req,
		string tenantId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var actor = await GetOrCreateActorAsync(ctx);
		if (!CanManageTenant(actor, tenantId))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var body = await HttpHelper.ReadBody<UpdateTenantRequest>(req);
		if (body == null)
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Invalid request body");

		var tenant = await cosmos.GetTenantAsync(tenantId);
		if (tenant == null)
			return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Tenant not found");

		if (!string.IsNullOrWhiteSpace(body.Name)) tenant.Name = body.Name;
		if (!string.IsNullOrWhiteSpace(body.Status)) tenant.Status = body.Status;
		if (body.RbacEnabled.HasValue) tenant.RbacEnabled = body.RbacEnabled.Value;

		var updated = await cosmos.UpdateTenantAsync(tenant);
		return await HttpHelper.OkJson(req, updated);
	}

	[Function("GetDemoUsers")]
	public async Task<HttpResponseData> GetDemoUsers(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin/backend/demo-users")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var actor = await GetOrCreateActorAsync(ctx);
		if (!IsSuperAdmin(actor))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var tenantId = actor.OrganizationId ?? actor.TenantId;
		if (string.IsNullOrWhiteSpace(tenantId))
			return await HttpHelper.OkJson(req, Array.Empty<User>());

		var demoUsers = await cosmos.GetDemoUsersByTenantAsync(tenantId);
		return await HttpHelper.OkJson(req, demoUsers);
	}

	[Function("ResetDemoUsers")]
	public async Task<HttpResponseData> ResetDemoUsers(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "admin/backend/demo-users/reset")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var actor = await GetOrCreateActorAsync(ctx);
		if (!IsSuperAdmin(actor))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var tenantId = actor.OrganizationId ?? actor.TenantId;
		if (string.IsNullOrWhiteSpace(tenantId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Tenant scope not found");

		await cosmos.DeleteDemoUsersByTenantAsync(tenantId);

		var demoUsers = BuildDefaultDemoUsers(tenantId);
		var created = await cosmos.UpsertDemoUsersAsync(tenantId, demoUsers);
		return await HttpHelper.OkJson(req, created);
	}

	private async Task<User> GetOrCreateActorAsync(UserContext ctx)
	{
		var tenantId = ResolveActorTenantId(ctx);
		var actor = await cosmos.GetUserByExternalIdAsync(ctx.UserId, tenantId);
		if (actor != null)
		{
			if (string.IsNullOrWhiteSpace(actor.AdminLevel))
			{
				actor.AdminLevel = MapLegacyRoleToAdminLevel(ctx.Role);
				actor.Role = string.IsNullOrWhiteSpace(actor.Role) ? ctx.Role : actor.Role;
				actor.TenantId = string.IsNullOrWhiteSpace(actor.TenantId) ? tenantId : actor.TenantId;
				actor.OrganizationId ??= actor.TenantId;
				actor = await cosmos.UpsertUserWithPartitionFallbackAsync(actor);
			}

			return actor;
		}

		var adminLevel = MapLegacyRoleToAdminLevel(ctx.Role);
		var created = new User
		{
			ExternalId = ctx.UserId,
			TenantId = tenantId,
			OrganizationId = tenantId,
			Role = string.IsNullOrWhiteSpace(ctx.Role) ? MapAdminLevelToLegacyRole(adminLevel) : ctx.Role,
			AdminLevel = adminLevel,
			DisplayName = string.IsNullOrWhiteSpace(ctx.DisplayName) ? "User" : ctx.DisplayName,
			Email = ctx.Email,
			Status = "active",
		};

		return await cosmos.UpsertUserWithPartitionFallbackAsync(created);
	}

	private static string ResolveActorTenantId(UserContext ctx)
	{
		if (!string.IsNullOrWhiteSpace(ctx.TenantId)) return ctx.TenantId;
		return ctx.Email.EndsWith(ArkansasServeEmailDomain, StringComparison.OrdinalIgnoreCase)
			? "arkansas-serve-root"
			: "unknown-tenant";
	}

	private static bool HasAdminAccess(User user) => GetRank(user.AdminLevel) > 0;

	private static bool IsSuperAdmin(User user) => string.Equals(user.AdminLevel, "SuperAdmin", StringComparison.OrdinalIgnoreCase);

	private static bool CanManageTenant(User actor, string tenantId)
	{
		if (IsSuperAdmin(actor)) return true;
		var actorTenant = actor.OrganizationId ?? actor.TenantId;
		return string.Equals(actorTenant, tenantId, StringComparison.OrdinalIgnoreCase)
			&& GetRank(actor.AdminLevel) >= GetRank("OrganizationAdmin");
	}

	private static bool CanAssignLevel(User actor, string level)
	{
		return GetRank(actor.AdminLevel) > GetRank(level);
	}

	private static int GetRank(string level)
	{
		if (AdminRank.TryGetValue(level, out var rank)) return rank;
		return 0;
	}

	private static string MapLegacyRoleToAdminLevel(string role)
	{
		if (string.Equals(role, "PlatformAdmin", StringComparison.OrdinalIgnoreCase)) return "SuperAdmin";
		if (string.Equals(role, "SchoolAdmin", StringComparison.OrdinalIgnoreCase)) return "OrganizationAdmin";
		if (string.Equals(role, "OrgStaff", StringComparison.OrdinalIgnoreCase)) return "EventAdmin";
		return "Student";
	}

	private static string MapAdminLevelToLegacyRole(string adminLevel)
	{
		if (string.Equals(adminLevel, "SuperAdmin", StringComparison.OrdinalIgnoreCase)) return "PlatformAdmin";
		if (string.Equals(adminLevel, "OrganizationAdmin", StringComparison.OrdinalIgnoreCase)) return "SchoolAdmin";
		if (string.Equals(adminLevel, "GroupAdmin", StringComparison.OrdinalIgnoreCase)) return "OrgStaff";
		if (string.Equals(adminLevel, "EventAdmin", StringComparison.OrdinalIgnoreCase)) return "OrgStaff";
		return "Student";
	}

	private static List<User> BuildDefaultDemoUsers(string tenantId)
	{
		var levels = new[] { "SuperAdmin", "OrganizationAdmin", "GroupAdmin", "EventAdmin", "Student" };
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
					Role = MapAdminLevelToLegacyRole(level),
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

	private sealed record UpdateTenantRequest(string? Name, string? Status, bool? RbacEnabled);
}
