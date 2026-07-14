using System.Net;
using System.Text.Json;
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
						return await HttpHelper.OkJson(req, await WithEffectiveAdminLevelAsync(adopted, ctx.UserId));
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
			return await HttpHelper.OkJson(req, await WithEffectiveAdminLevelAsync(user, ctx.UserId));
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

		// Read the raw body so we can do a PARTIAL update: only fields actually present
		// in the request are touched. Binding to a full User and assigning every field
		// would let a basic edit (which omits intake fields) wipe guardian data/consent.
		string raw;
		using (var reader = new StreamReader(req.Body))
			raw = await reader.ReadToEndAsync();
		if (string.IsNullOrWhiteSpace(raw))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Invalid request body");

		JsonDocument doc;
		try { doc = JsonDocument.Parse(raw); }
		catch (JsonException) { return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Invalid request body"); }

		using (doc)
		{
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
				return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Invalid request body");

			bool Has(string n) => root.TryGetProperty(n, out _);
			string? GetStr(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
			bool GetBool(string n, bool dflt) => root.TryGetProperty(n, out var v) && (v.ValueKind is JsonValueKind.True or JsonValueKind.False) ? v.GetBoolean() : dflt;

			try
			{
				var user = await cosmos.GetUserByExternalIdAsync(ctx.UserId, tenantId);
				if (user == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "User not found");

				// First-login intake completion is the ONE self-edit a locked org must
				// allow. Detect it by the intake wizard's payload (a valid personType on a
				// still-incomplete profile) — NOT merely by ProfileComplete==false, which is
				// also true for legacy profiles that predate intake. A plain name/phone edit
				// carries no personType, so the org lock still applies to it.
				var isIntakeSubmission = !user.ProfileComplete && Has("personType") && PersonTypes.IsValid(GetStr("personType"));

				var tenant = await cosmos.GetTenantAsync(tenantId);
				if (tenant is { AllowProfileSelfEdit: false }
					&& !isIntakeSubmission
					&& !AdminLevels.AtLeast(ctx.AdminLevel, AdminLevels.OrganizationAdmin)
					&& !AdminLevels.AtLeast(user.AdminLevel, AdminLevels.OrganizationAdmin))
					return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Profile editing is disabled for your organization");

				// Structured name (#24). Keep DisplayName in sync, but never blank an
				// existing name if the request supplies empty first/last.
				var hasFirst = Has("firstName");
				var hasLast = Has("lastName");
				if (hasFirst || hasLast)
				{
					if (hasFirst) user.FirstName = GetStr("firstName")?.Trim();
					if (hasLast) user.LastName = GetStr("lastName")?.Trim();
					var composed = User.ComposeName(user.FirstName, user.LastName, Has("displayName") ? GetStr("displayName") : user.DisplayName);
					if (!string.IsNullOrWhiteSpace(composed)) user.DisplayName = composed;
				}
				else if (Has("displayName"))
				{
					var dn = GetStr("displayName")?.Trim();
					if (!string.IsNullOrWhiteSpace(dn)) user.DisplayName = dn;
				}

				if (Has("personType") && PersonTypes.IsValid(GetStr("personType"))) user.PersonType = GetStr("personType");

				if (Has("phone")) user.Phone = GetStr("phone");
				if (Has("grade")) user.Grade = GetStr("grade");

				// Type-specific intake fields (#23), applied only when present. Background-
				// check fields are admin-managed and intentionally NOT accepted here.
				if (Has("dateOfBirth")) user.DateOfBirth = GetStr("dateOfBirth");
				if (Has("guardianName")) user.GuardianName = GetStr("guardianName");
				if (Has("guardianEmail")) user.GuardianEmail = GetStr("guardianEmail");
				if (Has("guardianPhone")) user.GuardianPhone = GetStr("guardianPhone");
				if (Has("guardianConsent")) user.GuardianConsent = GetBool("guardianConsent", user.GuardianConsent);
				if (Has("affiliation")) user.Affiliation = GetStr("affiliation");
				if (Has("emergencyContactName")) user.EmergencyContactName = GetStr("emergencyContactName");
				if (Has("emergencyContactPhone")) user.EmergencyContactPhone = GetStr("emergencyContactPhone");

				user.ProfileComplete = IntakeValidation.IsComplete(user);

				var updated = await cosmos.UpsertUserWithPartitionFallbackAsync(user);

				// A person's name belongs to the person, not the org. This write only touches
				// their HOME-org doc, so without this the same person keeps rendering under a
				// stale name in every other org they belong to. Best-effort: the profile save
				// has already succeeded and must not be failed by a sync problem.
				try
				{
					var synced = await cosmos.SyncNameAcrossMembershipsAsync(
						ctx.UserId, updated.FirstName, updated.LastName, updated.DisplayName, updated.TenantId);
					if (synced > 0)
						logger.LogInformation("Synced display name to {Count} other membership(s) for {UserId}", synced, ctx.UserId);
				}
				catch (Exception ex)
				{
					logger.LogError(ex, "Failed to sync display name across memberships for {UserId}", ctx.UserId);
				}

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

	// A person holds one User doc per org, each with its own AdminLevel, but the
	// /users/me response carries a single adminLevel that the client uses to gate tab
	// visibility and route guards. Report the STRONGEST level held across all
	// memberships — otherwise someone who is an admin only in a non-home org resolves
	// to their home-org level and is locked out of every admin surface (per-org
	// authorization is still enforced server-side by the actual per-org doc). The
	// mutation is response-only: it is never upserted back to the home-org doc.
	private async Task<User> WithEffectiveAdminLevelAsync(User user, string externalId)
	{
		try
		{
			var memberships = await cosmos.GetMembershipsByExternalIdAsync(externalId);
			var bestLevel = user.AdminLevel;
			var bestRank = AdminLevels.RankOf(bestLevel);
			foreach (var m in memberships)
			{
				var rank = AdminLevels.RankOf(m.AdminLevel);
				if (rank > bestRank) { bestRank = rank; bestLevel = m.AdminLevel; }
			}
			user.AdminLevel = bestLevel;
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Could not compute effective admin level for {ExternalId}; returning home-org level.", externalId);
		}
		return user;
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
