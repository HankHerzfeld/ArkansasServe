using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

public class ApprovalFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<ApprovalFunctions> logger)
{
	[Function("GetPendingApprovals")]
	public async Task<HttpResponseData> GetApprovals(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "approvals")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
		var schoolId = query["schoolId"];

		// A platform admin with no school selected sees every school's queue.
		if (ctx.IsSuperAdmin && string.IsNullOrEmpty(schoolId))
		{
			var approvals = await cosmos.GetAllPendingApprovalsAsync();
			return await HttpHelper.OkJson(req, approvals);
		}

		// Per-org: the caller must be an OrganizationAdmin+ in the target school.
		var orgId = string.IsNullOrWhiteSpace(schoolId) ? ctx.TenantId : schoolId;
		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, orgId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.OrganizationAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "You do not have approval access in this organization");

		var result = await cosmos.GetPendingApprovalsBySchoolAsync(orgId);
		return await HttpHelper.OkJson(req, result);
	}
}
