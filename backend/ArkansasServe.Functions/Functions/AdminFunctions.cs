using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

public class AdminFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<AdminFunctions> logger)
{
	[Function("GetTenants")]
	public async Task<HttpResponseData> GetTenants(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin/tenants")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "PlatformAdmin");
		if (ctx == null) return authError!;

		var tenants = await cosmos.GetAllTenantsAsync();
		return await HttpHelper.OkJson(req, tenants);
	}

	[Function("CreateTenant")]
	public async Task<HttpResponseData> CreateTenant(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "admin/tenants")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "PlatformAdmin");
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<Tenant>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Type))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "name and type are required");

		var created = await cosmos.CreateTenantAsync(body);
		return await HttpHelper.CreatedJson(req, created);
	}
}
