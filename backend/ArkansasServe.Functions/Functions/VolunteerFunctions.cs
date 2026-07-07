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
	private static readonly IReadOnlyDictionary<string, int> Rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
	{
		["Student"] = 0,
		["EventAdmin"] = 1,
		["GroupAdmin"] = 2,
		["OrganizationAdmin"] = 3,
		["SuperAdmin"] = 4,
	};

	private static int RankOf(string? level) => level != null && Rank.TryGetValue(level, out var r) ? r : 0;

	private static string MapRole(string? role) => role switch
	{
		"PlatformAdmin" => "SuperAdmin",
		"SchoolAdmin" => "OrganizationAdmin",
		"OrgStaff" => "EventAdmin",
		_ => "Student",
	};

	[Function("GetVolunteers")]
	public async Task<HttpResponseData> GetVolunteers(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/volunteers")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var actor = await cosmos.GetUserByExternalIdAsync(ctx.UserId, ctx.TenantId);
		var level = actor?.AdminLevel ?? MapRole(ctx.Role);
		if (RankOf(level) < RankOf("GroupAdmin"))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
		var orgId = ResolveOrg(actor, ctx, level, query["organizationId"]);
		var groupId = query["groupId"];

		var volunteers = await cosmos.GetVolunteersByTenantAsync(orgId, groupId);

		// A GroupAdmin only sees volunteers in the groups they administer.
		if (RankOf(level) == RankOf("GroupAdmin"))
		{
			var own = new HashSet<string>(actor?.GroupIds ?? []);
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

		var actor = await cosmos.GetUserByExternalIdAsync(ctx.UserId, ctx.TenantId);
		var level = actor?.AdminLevel ?? MapRole(ctx.Role);
		if (RankOf(level) < RankOf("GroupAdmin"))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var body = await HttpHelper.ReadBody<CreateVolunteerRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.DisplayName) || string.IsNullOrWhiteSpace(body.Email))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "displayName and email are required");

		var orgId = ResolveOrg(actor, ctx, level, body.OrganizationId);
		var email = body.Email.Trim().ToLowerInvariant();
		var groupIds = body.GroupIds ?? [];

		// GroupAdmins may only add within their own groups, and only org-wide (no
		// group) when the tenant setting allows it.
		if (RankOf(level) == RankOf("GroupAdmin"))
		{
			var own = new HashSet<string>(actor?.GroupIds ?? []);
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
			DisplayName = body.DisplayName.Trim(),
			Role = "Student",
			AdminLevel = "Student",
			GroupIds = groupIds,
			Status = "active",
			IsManaged = true,
			ManagedByUserId = ctx.UserId,
		};

		var created = await cosmos.CreateManagedVolunteerAsync(volunteer);
		return await HttpHelper.CreatedJson(req, created);
	}

	// A SuperAdmin may target any org; everyone else is pinned to their own.
	private static string ResolveOrg(User? actor, UserContext ctx, string? level, string? requestedOrg)
	{
		if (RankOf(level) >= RankOf("SuperAdmin") && !string.IsNullOrWhiteSpace(requestedOrg))
			return requestedOrg;
		return actor?.OrganizationId ?? actor?.TenantId ?? ctx.TenantId;
	}

	private sealed record CreateVolunteerRequest(string DisplayName, string Email, string? OrganizationId, List<string>? GroupIds);
}
