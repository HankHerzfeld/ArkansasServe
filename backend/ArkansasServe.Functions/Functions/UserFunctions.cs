using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

using User = ArkansasServe.Functions.Models.User;

namespace ArkansasServe.Functions.Functions;

public class UserFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<UserFunctions> logger)
{
	[Function("GetOrCreateCurrentUser")]
	public async Task<HttpResponseData> GetMe(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "users/me")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var user = await cosmos.GetUserByExternalIdAsync(ctx.UserId, ctx.TenantId);
		if (user == null)
		{
			user = new User
			{
				ExternalId = ctx.UserId,
				TenantId = ctx.TenantId,
				Role = ctx.Role,
				DisplayName = ctx.DisplayName,
				Email = ctx.Email
			};
			user = await cosmos.UpsertUserAsync(user);
		}

		return await HttpHelper.OkJson(req, user);
	}

	[Function("UpdateCurrentUser")]
	public async Task<HttpResponseData> UpdateMe(
		[HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "users/me")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<User>(req);
		if (body == null) return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Invalid request body");

		var user = await cosmos.GetUserByExternalIdAsync(ctx.UserId, ctx.TenantId);
		if (user == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "User not found");

		user.DisplayName = body.DisplayName;
		user.Phone = body.Phone;
		user.Grade = body.Grade;

		var updated = await cosmos.UpsertUserAsync(user);
		return await HttpHelper.OkJson(req, updated);
	}
}
