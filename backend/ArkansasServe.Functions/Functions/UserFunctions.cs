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
			"GetOrCreateCurrentUser invoked. AdminLevel={AdminLevel}; HasTenant={HasTenant}",
			ctx.AdminLevel,
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
				var adminLevel = isArkansasServeAdmin ? "SuperAdmin" : ctx.AdminLevel;

				// Seed the structured name from token claims (falling back to a split
				// of the single "name" claim). PersonType stays null so the first-login
				// wizard asks the person which they are, then completes intake (#22-#24).
				var (seedFirst, seedLast) = SplitName(ctx.GivenName, ctx.FamilyName, ctx.DisplayName);
				user = new User
				{
					ExternalId = ctx.UserId,
					TenantId = tenantId,
					OrganizationId = tenantId,
					AdminLevel = adminLevel,
					FirstName = seedFirst,
					LastName = seedLast,
					DisplayName = User.ComposeName(seedFirst, seedLast, ctx.DisplayName),
					Email = ctx.Email
				};
				user.ProfileComplete = IntakeValidation.IsComplete(user);
				user = await cosmos.UpsertUserWithPartitionFallbackAsync(user);

				logger.LogInformation(
					"Bootstrap user profile created. UserDocumentId={UserDocumentId}; Tenant={TenantId}",
					user.Id,
					user.TenantId);
			}
			else if (ctx.Email.EndsWith(ArkansasServeEmailDomain, StringComparison.OrdinalIgnoreCase)
				&& !string.Equals(user.AdminLevel, "SuperAdmin", StringComparison.OrdinalIgnoreCase))
			{
				user.AdminLevel = "SuperAdmin";
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

			// Per-org: self-editing can be locked. Org admins can always self-edit.
			// EXCEPTION: a person may always complete their own required intake on
			// first login (a locked, still-incomplete profile can't be a dead end).
			var tenant = await cosmos.GetTenantAsync(tenantId);
			if (tenant is { AllowProfileSelfEdit: false }
				&& user.ProfileComplete
				&& !AdminLevels.AtLeast(ctx.AdminLevel, AdminLevels.OrganizationAdmin)
				&& !AdminLevels.AtLeast(user.AdminLevel, AdminLevels.OrganizationAdmin))
				return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Profile editing is disabled for your organization");

			// Structured name (#24). Accept First/Last when provided; keep DisplayName
			// in sync. Fall back to a raw DisplayName edit for legacy callers.
			if (body.FirstName != null || body.LastName != null)
			{
				user.FirstName = body.FirstName?.Trim();
				user.LastName = body.LastName?.Trim();
				user.DisplayName = User.ComposeName(user.FirstName, user.LastName, body.DisplayName);
			}
			else if (!string.IsNullOrWhiteSpace(body.DisplayName))
			{
				user.DisplayName = body.DisplayName.Trim();
			}

			if (PersonTypes.IsValid(body.PersonType)) user.PersonType = body.PersonType;

			user.Phone = body.Phone;
			user.Grade = body.Grade;

			// Type-specific intake fields (#23). Background-check fields are
			// admin-managed and intentionally NOT accepted from self-service here.
			user.DateOfBirth = body.DateOfBirth;
			user.GuardianName = body.GuardianName;
			user.GuardianEmail = body.GuardianEmail;
			user.GuardianPhone = body.GuardianPhone;
			user.GuardianConsent = body.GuardianConsent;
			user.Affiliation = body.Affiliation;
			user.EmergencyContactName = body.EmergencyContactName;
			user.EmergencyContactPhone = body.EmergencyContactPhone;

			user.ProfileComplete = IntakeValidation.IsComplete(user);

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

	// Best-effort structured name from token claims: prefer given_name/family_name,
	// else split the single "name" claim on the last space (first-token = first name).
	private static (string? First, string? Last) SplitName(string? given, string? family, string? full)
	{
		if (!string.IsNullOrWhiteSpace(given) || !string.IsNullOrWhiteSpace(family))
			return (given?.Trim(), family?.Trim());

		var name = full?.Trim();
		if (string.IsNullOrWhiteSpace(name)) return (null, null);

		var idx = name.LastIndexOf(' ');
		return idx <= 0
			? (name, null)
			: (name[..idx].Trim(), name[(idx + 1)..].Trim());
	}
}
