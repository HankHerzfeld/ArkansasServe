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
