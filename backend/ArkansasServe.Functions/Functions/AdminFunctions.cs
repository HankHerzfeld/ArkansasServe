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

public class AdminFunctions(CosmosService cosmos, BlobService blob, CategoryService categories, AuthConfig authConfig, ILogger<AdminFunctions> logger)
{
	// A proposed service-category label is a short noun phrase; this only stops an accidental
	// over-long string from becoming a proposal (#10②).
	private const int MaxCategoryLabelLength = 60;

	// Demo organizations (#26) — parents for the demo personas. See BuildDemoOrganizations.
	private const string DemoOrgAlphaId = "demo-org-alpha";
	private const string DemoOrgBetaId = "demo-org-beta";

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

		// Normalise the casing at the only point a type is ever set. This endpoint is
		// model-bound (ReadBody<Tenant>), so without this a caller sending "organization"
		// re-introduces the split casing that already divided live data.
		body.Type = OrgTypes.Normalize(body.Type);

		// #10②: an unknown service category is no longer rejected — it is stored and recorded as
		// a pending proposal (below, after creation). Only an over-long label is refused.
		body.ServiceCategory = string.IsNullOrWhiteSpace(body.ServiceCategory) ? null : body.ServiceCategory.Trim();
		if (body.ServiceCategory?.Length > MaxCategoryLabelLength)
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, $"That service-category label is too long (max {MaxCategoryLabelLength} characters).");

		// Same rule as UpdateTenant: only a community organization carries these. Being
		// model-bound means anything in the body lands on the document unless it is cleared
		// here, so a school could otherwise arrive with a service category that no form shows
		// and nobody can clear.
		if (!OrgTypes.IsOrganization(body.Type))
		{
			body.ServiceCategory = null;
			body.FaithBased = false;
		}

		var created = await cosmos.CreateTenantAsync(body);
		await categories.RecordProposalIfNewAsync(created.ServiceCategory, created.Id, created.Name, CategoryProposalSources.Org);
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

		// ⚠️ THE LAST REMAINING SPECIAL CASE FOR THIS TENANT, and the reason changed on
		// 2026-07-21. It is no longer "root is not a real organization" — Arkansas Serve is now
		// an ordinary browsable org that happens to live in this partition. It is undeletable
		// because THE CATEGORY VOCABULARY IS STORED ON THIS DOCUMENT (#10②: no new Cosmos
		// container was available, so the singleton lives here). Deleting it would silently
		// destroy every approved category and alias platform-wide.
		//
		// The other historic guards (hidden from the directory, org page 404, join refused) are
		// gone. Do not restore them; do not remove this one.
		if (string.Equals(id, TenantIds.Root, StringComparison.OrdinalIgnoreCase))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest,
				"Arkansas Serve cannot be deleted — it stores the platform's shared category vocabulary.");

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

		// #13: replace the oversight assignments when supplied (null = leave untouched). The
		// OrgAdmin sets WHO oversees; each admin's own notification prefs are preserved across a
		// membership edit (they are the assigned admin's to change, via SetMyAssignmentPrefs).
		if (body.AssignedAdmins != null)
		{
			var priorByAdmin = target.AssignedAdmins.ToDictionary(a => a.AdminId, a => a, StringComparer.OrdinalIgnoreCase);
			var rebuilt = new List<UserAssignment>();
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var a in body.AssignedAdmins)
			{
				var adminId = a?.AdminId?.Trim();
				if (string.IsNullOrWhiteSpace(adminId) || string.Equals(adminId, id, StringComparison.OrdinalIgnoreCase)) continue;
				if (!seen.Add(adminId)) continue;

				var adminUser = await cosmos.GetUserByIdAsync(adminId, target.TenantId);
				if (adminUser == null || !AdminLevels.AtLeast(adminUser.AdminLevel, AdminLevels.EventAdmin))
					return await HttpHelper.Error(req, HttpStatusCode.BadRequest,
						"Every assigned admin must be an EventAdmin or higher in this organization.");

				rebuilt.Add(priorByAdmin.TryGetValue(adminId, out var prev) ? prev : new UserAssignment { AdminId = adminId });
			}
			target.AssignedAdmins = rebuilt;
		}

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

	// ── Per-org user tags / credentials (#11) ─────────────────────────────────
	// Definitions only; a person's state against them is set via VolunteerFunctions.
	// Mirrors the Groups endpoints above — same route shape, same CanManageTenantAsync gate,
	// same "stored on the Tenant" model. These are a handful per org, always read with the
	// org, never queried across orgs.

	[Function("GetTenantUserTags")]
	public async Task<HttpResponseData> GetTenantUserTags(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/backend/tenants/{tenantId}/user-tags")] HttpRequestData req,
		string tenantId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await CanManageTenantAsync(ctx, tenantId)) return await Forbid(req);

		var tenant = await cosmos.GetTenantAsync(tenantId);
		if (tenant == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Tenant not found");

		return await HttpHelper.OkJson(req, tenant.UserTags);
	}

	[Function("CreateTenantUserTag")]
	public async Task<HttpResponseData> CreateTenantUserTag(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/backend/tenants/{tenantId}/user-tags")] HttpRequestData req,
		string tenantId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await CanManageTenantAsync(ctx, tenantId)) return await Forbid(req);

		var body = await HttpHelper.ReadBody<UserTagRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.Label))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "label is required");

		var enforcement = string.IsNullOrWhiteSpace(body.Enforcement) ? TagEnforcement.Advisory : body.Enforcement;
		if (!TagEnforcement.IsValid(enforcement))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest,
				$"\"{enforcement}\" is not a supported enforcement. Use advisory or blockRegistration.");
		if (body.ExpiresAfterDays is <= 0)
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "expiresAfterDays must be a positive number of days, or omitted for a credential that never expires");

		var tenant = await cosmos.GetTenantAsync(tenantId);
		if (tenant == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Tenant not found");

		// A duplicate label is not a hard error anywhere, but two tags called "Waiver signed"
		// are indistinguishable to the admin choosing between them — which defeats the point.
		if (tenant.UserTags.Any(t => string.Equals(t.Label, body.Label.Trim(), StringComparison.OrdinalIgnoreCase)
			&& string.Equals(t.Status, "active", StringComparison.OrdinalIgnoreCase)))
			return await HttpHelper.Error(req, HttpStatusCode.Conflict, $"This organization already has a tag called \"{body.Label.Trim()}\"");

		tenant.UserTags.Add(new TenantUserTag
		{
			Label = body.Label.Trim(),
			Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim(),
			Enforcement = enforcement,
			ExpiresAfterDays = body.ExpiresAfterDays,
			Status = "active",
		});

		var updated = await cosmos.UpdateTenantAsync(tenant);
		logger.LogInformation("[UserTags] Created tag \"{Label}\" ({Enforcement}) in org {OrgId} by {UserId}", body.Label, enforcement, tenantId, ctx.UserId);
		return await HttpHelper.OkJson(req, updated.UserTags);
	}

	[Function("UpdateTenantUserTag")]
	public async Task<HttpResponseData> UpdateTenantUserTag(
		[HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "manage/backend/tenants/{tenantId}/user-tags/{tagId}")] HttpRequestData req,
		string tenantId, string tagId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await CanManageTenantAsync(ctx, tenantId)) return await Forbid(req);

		var body = await HttpHelper.ReadBody<UserTagRequest>(req);
		if (body == null) return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Invalid request body");

		var tenant = await cosmos.GetTenantAsync(tenantId);
		if (tenant == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Tenant not found");

		var tag = tenant.UserTags.FirstOrDefault(t => t.Id == tagId);
		if (tag == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Tag not found");

		if (!string.IsNullOrWhiteSpace(body.Label)) tag.Label = body.Label.Trim();
		if (body.Description != null) tag.Description = body.Description.Length == 0 ? null : body.Description.Trim();
		if (!string.IsNullOrWhiteSpace(body.Enforcement))
		{
			if (!TagEnforcement.IsValid(body.Enforcement))
				return await HttpHelper.Error(req, HttpStatusCode.BadRequest, $"\"{body.Enforcement}\" is not a supported enforcement");
			tag.Enforcement = body.Enforcement;
		}
		// Changing the expiry policy does NOT retouch anyone's existing ExpiresAt: that was
		// stamped from the policy in force when they completed it. Shortening a waiver from
		// two years to one should not retroactively expire people who are compliant under the
		// rule they were told about.
		if (body.ExpiresAfterDays.HasValue)
		{
			if (body.ExpiresAfterDays.Value <= 0)
				return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "expiresAfterDays must be positive");
			tag.ExpiresAfterDays = body.ExpiresAfterDays;
		}
		// Archiving stops a tag being offered but keeps existing people's history readable —
		// deleting it would silently strip the record that someone signed a waiver.
		if (!string.IsNullOrWhiteSpace(body.Status)) tag.Status = body.Status;

		var updated = await cosmos.UpdateTenantAsync(tenant);
		return await HttpHelper.OkJson(req, updated.UserTags);
	}

	// ── School/JDC event-approval policy (#12) ──────────────────────────────────
	// Whether a student's logged hours auto-count or need review, by org and/or category.
	// OrganizationAdmin+ in the school (or a global super) manages it, like the tag endpoints.

	[Function("GetTenantApprovalPolicy")]
	public async Task<HttpResponseData> GetTenantApprovalPolicy(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/backend/tenants/{tenantId}/approval-policy")] HttpRequestData req,
		string tenantId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await CanManageTenantAsync(ctx, tenantId)) return await Forbid(req);

		var tenant = await cosmos.GetTenantAsync(tenantId);
		if (tenant == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Tenant not found");

		// An unconfigured school reads back the defaults, so the editor always has a shape to bind.
		return await HttpHelper.OkJson(req, tenant.ApprovalPolicy ?? new ApprovalPolicy());
	}

	[Function("SetTenantApprovalPolicy")]
	public async Task<HttpResponseData> SetTenantApprovalPolicy(
		[HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "manage/backend/tenants/{tenantId}/approval-policy")] HttpRequestData req,
		string tenantId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await CanManageTenantAsync(ctx, tenantId)) return await Forbid(req);

		var body = await HttpHelper.ReadBody<ApprovalPolicy>(req);
		if (body == null) return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "A policy body is required");

		// Validate + normalize the whole policy before storing: an invalid value must fail the
		// request, never land as a silent typo that changes who gets auto-approved.
		var def = string.IsNullOrWhiteSpace(body.Default) ? ApprovalPolicies.ApprovalRequired : body.Default.Trim();
		if (!ApprovalPolicies.IsValid(def))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, $"\"{def}\" is not a valid default. Use approvalRequired or preapproved.");

		var byOrg = new Dictionary<string, string>();
		foreach (var kv in body.ByOrg ?? [])
		{
			if (string.IsNullOrWhiteSpace(kv.Key)) continue;
			if (!ApprovalPolicies.IsValid(kv.Value))
				return await HttpHelper.Error(req, HttpStatusCode.BadRequest, $"\"{kv.Value}\" is not a valid policy for org {kv.Key}.");
			byOrg[kv.Key.Trim()] = kv.Value;
		}

		// Category keys must be REAL categories (canonical or approved-new, #10②) — never a
		// pending label, which would gate on something no one can pick yet.
		var effectiveCategories = (await categories.GetEffectiveAsync()).Effective;
		var byCategory = new Dictionary<string, string>();
		foreach (var kv in body.ByCategory ?? [])
		{
			if (string.IsNullOrWhiteSpace(kv.Key)) continue;
			if (!effectiveCategories.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
				return await HttpHelper.Error(req, HttpStatusCode.BadRequest, $"\"{kv.Key}\" is not a service category.");
			if (!ApprovalPolicies.IsValid(kv.Value))
				return await HttpHelper.Error(req, HttpStatusCode.BadRequest, $"\"{kv.Value}\" is not a valid policy for category {kv.Key}.");
			byCategory[kv.Key] = kv.Value;
		}

		var tenant = await cosmos.GetTenantAsync(tenantId);
		if (tenant == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Tenant not found");

		tenant.ApprovalPolicy = new ApprovalPolicy { Default = def, ByOrg = byOrg, ByCategory = byCategory };
		var updated = await cosmos.UpdateTenantAsync(tenant);
		logger.LogInformation("[ApprovalPolicy] {Actor} set policy for org {OrgId}: default={Default}, {OrgRules} org rule(s), {CatRules} category rule(s)",
			ctx.UserId, tenantId, def, byOrg.Count, byCategory.Count);
		return await HttpHelper.OkJson(req, updated.ApprovalPolicy);
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
		if (body.AllowSelfJoin.HasValue) tenant.AllowSelfJoin = body.AllowSelfJoin.Value;
		// #20. Nullable so an omitted field never silently flips a consent policy: a partial
		// tenant update must not be able to switch guardian enforcement on or off by accident.
		if (body.RequireGuardianConsent.HasValue) tenant.RequireGuardianConsent = body.RequireGuardianConsent.Value;

		// ── Taxonomy ────────────────────────────────────────────────────────────
		// Normalise on write so the stored casing converges on the canonical value, even
		// though every read goes through OrgTypes.IsOrganization and does not depend on it.
		if (!string.IsNullOrWhiteSpace(body.Type)) tenant.Type = OrgTypes.Normalize(body.Type);

		if (body.FaithBased.HasValue) tenant.FaithBased = body.FaithBased.Value;

		if (body.ServiceCategory != null)
		{
			var proposed = body.ServiceCategory.Trim();
			if (proposed.Length > MaxCategoryLabelLength)
				return await HttpHelper.Error(req, HttpStatusCode.BadRequest, $"That service-category label is too long (max {MaxCategoryLabelLength} characters).");

			// Only a community organization has a service category. Silently keeping one on a
			// school would leave an invisible value that the form never shows and nobody can
			// clear, which then surfaces in a filter months later.
			tenant.ServiceCategory = OrgTypes.IsOrganization(tenant.Type)
				? (proposed.Length == 0 ? null : proposed)
				: null;

			// #10②: an unknown label is recorded as a pending proposal for SuperAdmin review
			// rather than rejected. Known values / empties are no-ops.
			await categories.RecordProposalIfNewAsync(tenant.ServiceCategory, tenantId, tenant.Name, CategoryProposalSources.Org);
		}
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

		// Demo fixtures are global (they live in the demo orgs + the root org), so they are
		// listed regardless of the caller's current org scope.
		var demoUsers = await cosmos.GetAllDemoUsersAsync();
		return await HttpHelper.OkJson(req, demoUsers);
	}

	[Function("ResetDemoUsers")]
	public async Task<HttpResponseData> ResetDemoUsers(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/backend/demo-users/reset")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await IsGlobalSuperAsync(ctx)) return await Forbid(req);

		// Global fixture rebuild: assert the demo orgs exist, then recreate every demo
		// persona across them. Not scoped to the caller's current org.
		foreach (var org in BuildDemoOrganizations())
			await cosmos.UpsertTenantAsync(org);

		await cosmos.DeleteAllDemoUsersAsync();
		var created = await cosmos.UpsertDemoUsersAsync(BuildDefaultDemoUsers());
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

	// Canonicalise the CASING of every tenant's `type` (School / JDC / Organization). Legacy rows
	// carry mixed casing — the platform-admin dropdown once wrote lowercase "organization" while
	// seeded orgs were capitalised — and `arkansas-serve-root` still holds the lowercase form.
	// Nothing branches on the casing (OrgTypes.IsOrganization compares case-insensitively), so this
	// is a tidy-up, not a correctness fix; the create/update paths already normalise, so this only
	// catches rows written before that. Idempotent, and touches ONLY casing of known types (an
	// unknown value folds to itself and is skipped).
	//
	// DRY-RUN BY DEFAULT: reports what it would change and writes nothing unless ?apply=true. A
	// data-normalisation tool should let you see the diff before it commits it.
	[Function("NormalizeTenantTypes")]
	public async Task<HttpResponseData> NormalizeTenantTypes(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/maintenance/normalize-tenant-types")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await IsGlobalSuperAsync(ctx)) return await Forbid(req);

		var apply = string.Equals(QueryValue(req, "apply"), "true", StringComparison.OrdinalIgnoreCase);

		var tenants = await cosmos.GetAllTenantsAsync();
		var changes = new List<object>();
		var applied = 0;
		foreach (var t in tenants)
		{
			var canonical = OrgTypes.Normalize(t.Type);
			// Skip empty/null types and rows already canonical. An unknown value folds to itself,
			// so it compares equal here and is left untouched.
			if (string.IsNullOrEmpty(canonical) || string.Equals(canonical, t.Type, StringComparison.Ordinal))
				continue;

			changes.Add(new { id = t.Id, name = t.Name, from = t.Type, to = canonical });
			if (apply)
			{
				t.Type = canonical;
				await cosmos.UpdateTenantAsync(t);
				applied++;
			}
		}

		logger.LogInformation(
			"[Maintenance] normalize-tenant-types by {Actor}: {Candidates} candidate(s), apply={Apply}, applied={Applied}",
			ctx.UserId, changes.Count, apply, applied);

		return await HttpHelper.OkJson(req, new
		{
			dryRun = !apply,
			scanned = tenants.Count,
			candidates = changes.Count,
			applied,
			changes,
		});
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
		return string.IsNullOrWhiteSpace(ctx.TenantId) ? TenantIds.Root : ctx.TenantId;
	}

	private static string? QueryValue(HttpRequestData req, string key)
	{
		var value = System.Web.HttpUtility.ParseQueryString(req.Url.Query)[key];
		return string.IsNullOrWhiteSpace(value) ? null : value;
	}

	private static Task<HttpResponseData> Forbid(HttpRequestData req)
		=> HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

	// The demo organizations that parent the demo personas. Alpha is the "home" org;
	// Beta exists so a secondary-org admin is reproducible — that shape (admin in a
	// non-home org) is what Findings 2/7/9 are about, and a single-org fixture set
	// cannot express it. SuperAdmin personas stay on the Arkansas Serve root org.
	private static List<Tenant> BuildDemoOrganizations() =>
	[
		new Tenant
		{
			Id = DemoOrgAlphaId,
			Type = "Organization",
			Name = "Demo Community Organization (Alpha)",
			Description = "Seeded demo organization — home org for the demo personas. Safe to reset.",
			ContactEmail = "demo.alpha@arkansasserve.local",
			Status = "active",
		},
		new Tenant
		{
			Id = DemoOrgBetaId,
			Type = "Organization",
			Name = "Demo Partner Organization (Beta)",
			Description = "Seeded demo organization — secondary org for the cross-org demo persona. Safe to reset.",
			ContactEmail = "demo.beta@arkansasserve.local",
			Status = "active",
		},
	];

	// Demo personas.
	//
	// A person is one User doc PER ORG, keyed by a shared externalId — so the cross-org
	// persona below is two docs with the SAME externalId. That is what makes
	// ResolveActorInOrgAsync see both memberships and lets a single "Act as" session
	// exercise secondary-org admin behaviour.
	private static List<User> BuildDefaultDemoUsers()
	{
		var users = new List<User>();

		static User Demo(string id, string tenantId, string level, string name, string? externalId = null) => new()
		{
			Id = id,
			// Shared externalId ⇒ same person across orgs; defaults to the doc id.
			ExternalId = externalId ?? id,
			TenantId = tenantId,
			OrganizationId = tenantId,
			AdminLevel = level,
			DemoUserType = level,
			DisplayName = name,
			Email = $"{id}@arkansasserve.local",
			IsDemoUser = true,
		};

		// SuperAdmin — stays on the Arkansas Serve host org.
		for (var i = 1; i <= 2; i++)
			users.Add(Demo($"demo-superadmin-{i}", TenantIds.Root, AdminLevels.SuperAdmin, $"Demo SuperAdmin {i}"));

		// Org / Group / Event admins — parented to the Alpha demo org.
		foreach (var level in new[] { AdminLevels.OrganizationAdmin, AdminLevels.GroupAdmin, AdminLevels.EventAdmin })
			for (var i = 1; i <= 2; i++)
				users.Add(Demo($"demo-{level.ToLowerInvariant()}-{i}", DemoOrgAlphaId, level, $"Demo {level} {i}"));

		// Students — parented to Alpha. The two differ only in SelfJoined, which is the
		// A/B for Finding 6: a self-joined membership may Leave, an adopted one is refused.
		var selfJoined = Demo("demo-student-1", DemoOrgAlphaId, AdminLevels.Student, "Demo Student 1 (self-joined)");
		selfJoined.SelfJoined = true;
		users.Add(selfJoined);

		var adopted = Demo("demo-student-2", DemoOrgAlphaId, AdminLevels.Student, "Demo Student 2 (adopted)");
		adopted.SelfJoined = false;
		users.Add(adopted);

		// Cross-org persona: ONE person (shared externalId), volunteer in their home org
		// (Alpha) and OrganizationAdmin in a secondary org (Beta). Reproduces Findings 2/7/9.
		const string crossOrgExternalId = "demo-crossorg-1";
		users.Add(Demo("demo-crossorg-1-alpha", DemoOrgAlphaId, AdminLevels.Student,
			"Demo Cross-Org 1 (volunteer in Alpha)", crossOrgExternalId));
		users.Add(Demo("demo-crossorg-1-beta", DemoOrgBetaId, AdminLevels.OrganizationAdmin,
			"Demo Cross-Org 1 (admin in Beta)", crossOrgExternalId));

		return users;
	}

	private sealed record UpdateUserAccessRequest(
		string TenantId,
		string AdminLevel,
		string? OrganizationId,
		List<string>? GroupIds,
		List<string>? EventAdminEventIds,
		// #13: the admins overseeing this volunteer. When present, REPLACES the list (like
		// GroupIds). Omit (null) to leave assignments untouched — a role edit shouldn't wipe them.
		List<UserAssignment>? AssignedAdmins);

	private sealed record CreateGroupRequest(string Name, string? Status);

	private sealed record UserTagRequest(
		string? Label, string? Description, string? Enforcement, int? ExpiresAfterDays, string? Status);

	private sealed record DbQueryRequest(string Container, string Query, int? MaxItems);

	private sealed record LogoUploadTokenRequest(string FileName);

	private sealed record UpdateTenantRequest(
		string? Name, string? Status, bool? RbacEnabled, bool? AllowGroupAdminAddVolunteers, bool? AllowProfileSelfEdit,
		bool? AllowSelfJoin, bool? RequireGuardianConsent, string? Type, string? ServiceCategory, bool? FaithBased,
		string? Description, string? Mission, string? Website,
		string? ContactEmail, string? ContactPhone, string? Address, string? LogoUrl, string? LogoBlobName);
}
