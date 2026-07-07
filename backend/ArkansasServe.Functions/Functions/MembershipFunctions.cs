using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

public class MembershipFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<MembershipFunctions> logger)
{
	// Every organization the current person belongs to (one membership per org),
	// enriched with the org name and its groups so the client can build the
	// multi-org and group switchers.
	[Function("GetMyMemberships")]
	public async Task<HttpResponseData> GetMyMemberships(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/me/memberships")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var memberships = await cosmos.GetMembershipsByExternalIdAsync(ctx.UserId);

		var result = new List<object>();
		foreach (var m in memberships)
		{
			var orgId = string.IsNullOrWhiteSpace(m.OrganizationId) ? m.TenantId : m.OrganizationId!;
			var tenant = string.IsNullOrWhiteSpace(orgId) ? null : await cosmos.GetTenantAsync(orgId);
			result.Add(new
			{
				organizationId = orgId,
				organizationName = tenant?.Name ?? orgId,
				adminLevel = m.AdminLevel,
				role = m.Role,
				groupIds = m.GroupIds,
				groups = tenant?.Groups ?? new List<TenantGroup>(),
			});
		}

		return await HttpHelper.OkJson(req, result);
	}
}
