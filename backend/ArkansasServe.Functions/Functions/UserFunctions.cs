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

public class UserFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<UserFunctions> logger)
{
	private const string ArkansasServeEmailDomain = "@arkansasserve.com";

	[Function("GetOrCreateCurrentUser")]
	public async Task<HttpResponseData> GetMe(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "users/me")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		var tenantId = ResolveTenantId(ctx);

		logger.LogInformation(
			"GetOrCreateCurrentUser invoked. Role={Role}; HasTenant={HasTenant}",
			ctx.Role,
			!string.IsNullOrWhiteSpace(ctx.TenantId));

		try
		{
			var user = await cosmos.GetUserByExternalIdAsync(ctx.UserId, tenantId);
			if (user == null)
			{
				// Auto-link: if an admin pre-created a managed volunteer with this
					// email in the tenant, adopt that record instead of making a new one.
					var managedEmail = ctx.Email?.Trim().ToLowerInvariant();
					var managed = string.IsNullOrWhiteSpace(managedEmail)
						? null
						: await cosmos.GetMembershipByEmailAsync(managedEmail, tenantId);
					if (managed is { IsManaged: true })
					{
						managed.ExternalId = ctx.UserId;
						managed.IsManaged = false;
						managed.ManagedByUserId = null;
						if (string.IsNullOrWhiteSpace(managed.DisplayName)) managed.DisplayName = ctx.DisplayName;
						var adopted = await cosmos.UpsertUserWithPartitionFallbackAsync(managed);
						await TryMigrateAdoptedLogsAsync(adopted.Id, ctx.UserId);
						logger.LogInformation("Adopted managed volunteer record on first sign-in for tenant {TenantId}.", tenantId);
						return await HttpHelper.OkJson(req, adopted);
					}

					logger.LogInformation("No user profile found for current principal; starting bootstrap create.");

				var isArkansasServeAdmin = ctx.Email.EndsWith(ArkansasServeEmailDomain, StringComparison.OrdinalIgnoreCase);
				var role = isArkansasServeAdmin ? "PlatformAdmin" : ctx.Role;
				var adminLevel = isArkansasServeAdmin ? "SuperAdmin" : MapLegacyRoleToAdminLevel(role);

				user = new User
				{
					ExternalId = ctx.UserId,
					TenantId = tenantId,
					OrganizationId = tenantId,
					Role = role,
					AdminLevel = adminLevel,
					DisplayName = ctx.DisplayName,
					Email = ctx.Email
				};
				user = await cosmos.UpsertUserWithPartitionFallbackAsync(user);

				logger.LogInformation(
					"Bootstrap user profile created. UserDocumentId={UserDocumentId}; Tenant={TenantId}",
					user.Id,
					user.TenantId);
			}
			else if (ctx.Email.EndsWith(ArkansasServeEmailDomain, StringComparison.OrdinalIgnoreCase)
				&& (!string.Equals(user.AdminLevel, "SuperAdmin", StringComparison.OrdinalIgnoreCase)
					|| !string.Equals(user.Role, "PlatformAdmin", StringComparison.OrdinalIgnoreCase)))
			{
				user.AdminLevel = "SuperAdmin";
				user.Role = "PlatformAdmin";
				user.OrganizationId ??= user.TenantId;
				user = await cosmos.UpsertUserWithPartitionFallbackAsync(user);

				logger.LogInformation("Applied ArkansasServe domain admin elevation for existing profile.");
			}

			logger.LogInformation("Returning current user profile payload.");
			return await HttpHelper.OkJson(req, user);
		}
		catch (CosmosException ex)
		{
			logger.LogError(ex, "Cosmos error while loading current user {UserId} in tenant {TenantId}", ctx.UserId, tenantId);
			return await HttpHelper.Error(req, HttpStatusCode.InternalServerError, "Unable to load user profile");
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Unexpected error while loading current user {UserId}", ctx.UserId);
			return await HttpHelper.Error(req, HttpStatusCode.InternalServerError, "Unable to load user profile");
		}
	}

	[Function("UpdateCurrentUser")]
	public async Task<HttpResponseData> UpdateMe(
		[HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "users/me")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		var tenantId = ResolveTenantId(ctx);

		var body = await HttpHelper.ReadBody<User>(req);
		if (body == null) return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Invalid request body");

		try
		{
			var user = await cosmos.GetUserByExternalIdAsync(ctx.UserId, tenantId);
			if (user == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "User not found");

			user.DisplayName = body.DisplayName;
			user.Phone = body.Phone;
			user.Grade = body.Grade;

			var updated = await cosmos.UpsertUserWithPartitionFallbackAsync(user);
			return await HttpHelper.OkJson(req, updated);
		}
		catch (CosmosException ex)
		{
			logger.LogError(ex, "Cosmos error while updating current user {UserId} in tenant {TenantId}", ctx.UserId, tenantId);
			return await HttpHelper.Error(req, HttpStatusCode.InternalServerError, "Unable to update user profile");
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Unexpected error while updating current user {UserId}", ctx.UserId);
			return await HttpHelper.Error(req, HttpStatusCode.InternalServerError, "Unable to update user profile");
		}
	}

	// Move an adopted managed volunteer's service logs from the old studentId
	// (their doc Id) into the externalId partition. Best-effort: a failure here
	// must never break sign-in — the logs can be migrated on a later adoption.
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

	private static string ResolveTenantId(UserContext ctx)
	{
		if (!string.IsNullOrWhiteSpace(ctx.TenantId)) return ctx.TenantId;
		return ctx.Email.EndsWith(ArkansasServeEmailDomain, StringComparison.OrdinalIgnoreCase)
			? "arkansas-serve-root"
			: "unknown-tenant";
	}

	private static string MapLegacyRoleToAdminLevel(string role)
	{
		if (string.Equals(role, "PlatformAdmin", StringComparison.OrdinalIgnoreCase)) return "SuperAdmin";
		if (string.Equals(role, "SchoolAdmin", StringComparison.OrdinalIgnoreCase)) return "OrganizationAdmin";
		if (string.Equals(role, "OrgStaff", StringComparison.OrdinalIgnoreCase)) return "EventAdmin";
		return "Student";
	}
}
