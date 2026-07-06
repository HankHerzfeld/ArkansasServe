using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

public class NotificationFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<NotificationFunctions> logger)
{
	// Any authenticated user may read their own notifications — no role filter.
	// Notifications are partitioned by userId, so the caller only ever sees theirs.
	[Function("GetMyNotifications")]
	public async Task<HttpResponseData> GetMine(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "notifications")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var notifications = await cosmos.GetNotificationsForUserAsync(ctx.UserId);
		var ordered = notifications.OrderByDescending(n => n.CreatedAt).ToList();
		return await HttpHelper.OkJson(req, ordered);
	}

	[Function("MarkNotificationRead")]
	public async Task<HttpResponseData> MarkRead(
		[HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "notifications/{id}")] HttpRequestData req,
		string id)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		// userId (the partition key) is the caller's, so this can only ever mark
		// a notification the caller owns; anyone else's id resolves to NotFound.
		var updated = await cosmos.MarkNotificationReadAsync(id, ctx.UserId);
		if (updated == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Notification not found");

		return await HttpHelper.OkJson(req, updated);
	}
}
