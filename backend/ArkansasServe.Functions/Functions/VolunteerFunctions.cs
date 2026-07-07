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
	[Function("GetVolunteers")]
	public async Task<HttpResponseData> GetVolunteers(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/volunteers")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
		var orgId = string.IsNullOrWhiteSpace(query["organizationId"]) ? ctx.TenantId : query["organizationId"]!;

		// Per-org: the caller must be a GroupAdmin+ in the target org.
		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.Role, orgId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.GroupAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var groupId = query["groupId"];
		var volunteers = await cosmos.GetVolunteersByTenantAsync(orgId, groupId);

		// A GroupAdmin only sees volunteers in the groups they administer.
		if (AdminLevels.RankOf(actor.AdminLevel) == AdminLevels.RankOf(AdminLevels.GroupAdmin))
		{
			var own = new HashSet<string>(actor.GroupIds ?? []);
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

		var body = await HttpHelper.ReadBody<CreateVolunteerRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.DisplayName) || string.IsNullOrWhiteSpace(body.Email))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "displayName and email are required");

		var orgId = string.IsNullOrWhiteSpace(body.OrganizationId) ? ctx.TenantId : body.OrganizationId!;

		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.Role, orgId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.GroupAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var email = body.Email.Trim().ToLowerInvariant();
		var groupIds = body.GroupIds ?? [];

		// GroupAdmins may only add within their own groups, and only org-wide (no
		// group) when the tenant setting allows it.
		if (AdminLevels.RankOf(actor.AdminLevel) == AdminLevels.RankOf(AdminLevels.GroupAdmin))
		{
			var own = new HashSet<string>(actor.GroupIds ?? []);
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
			AdminLevel = AdminLevels.Student,
			GroupIds = groupIds,
			Status = "active",
			IsManaged = true,
			ManagedByUserId = ctx.UserId,
		};

		var created = await cosmos.CreateManagedVolunteerAsync(volunteer);
		return await HttpHelper.CreatedJson(req, created);
	}

	private sealed record CreateVolunteerRequest(string DisplayName, string Email, string? OrganizationId, List<string>? GroupIds);
}
