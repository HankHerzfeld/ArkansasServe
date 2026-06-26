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
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "SchoolAdmin", "PlatformAdmin");
		if (ctx == null) return authError!;

		var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
		var schoolId = query["schoolId"];

		if (ctx.IsPlatformAdmin && string.IsNullOrEmpty(schoolId))
		{
			var approvals = await cosmos.GetAllPendingApprovalsAsync();
			return await HttpHelper.OkJson(req, approvals);
		}

		var effectiveSchoolId = ctx.IsPlatformAdmin ? schoolId! : ctx.TenantId;
		var result = await cosmos.GetPendingApprovalsBySchoolAsync(effectiveSchoolId);
		return await HttpHelper.OkJson(req, result);
	}
}
