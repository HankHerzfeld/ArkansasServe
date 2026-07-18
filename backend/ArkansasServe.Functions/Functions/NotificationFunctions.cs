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

	// The shell's notification pane: the caller's own notifications PLUS role-scoped
	// admin items (pending approvals where they're OrganizationAdmin+, recent
	// self-joins where they're GroupAdmin+), each with a place to jump and act.
	// Admin items are scoped to the orgs the caller actually holds a role in.
	[Function("GetNotificationPane")]
	public async Task<HttpResponseData> GetPane(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "notifications/pane")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var personal = (await cosmos.GetNotificationsForUserAsync(ctx.UserId))
			.OrderByDescending(n => n.CreatedAt)
			.Select(n => new { id = n.Id, type = n.Type, message = n.Message, isRead = n.IsRead, relatedId = n.RelatedId, createdAt = n.CreatedAt })
			.ToList();
		var unread = personal.Count(n => !n.isRead);

		var memberships = await cosmos.GetMembershipsByExternalIdAsync(ctx.UserId);
		var since = DateTime.UtcNow.AddDays(-14);
		var admin = new List<object>();
		var tenantNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		async Task<string> OrgName(string orgId)
		{
			if (tenantNames.TryGetValue(orgId, out var cached)) return cached;
			var t = await cosmos.GetTenantAsync(orgId);
			var name = t?.Name ?? orgId;
			tenantNames[orgId] = name;
			return name;
		}

		foreach (var m in memberships)
		{
			var orgId = string.IsNullOrWhiteSpace(m.OrganizationId) ? m.TenantId : m.OrganizationId!;
			if (string.IsNullOrWhiteSpace(orgId)) continue;

			if (AdminLevels.AtLeast(m.AdminLevel, AdminLevels.OrganizationAdmin))
			{
				var pending = await cosmos.GetPendingApprovalsBySchoolAsync(orgId);
				if (pending.Count > 0)
				{
					var name = await OrgName(orgId);
					admin.Add(new
					{
						kind = "approvals",
						orgId,
						orgName = name,
						count = pending.Count,
						message = $"{pending.Count} hour request{(pending.Count == 1 ? "" : "s")} pending approval in {name}",
						href = "/admin-portal.html",
					});
				}
			}

			if (AdminLevels.AtLeast(m.AdminLevel, AdminLevels.GroupAdmin))
			{
				var joins = await cosmos.GetRecentSelfJoinsAsync(orgId, since);
				foreach (var u in joins.Where(j => j.ExternalId != ctx.UserId))
				{
					var name = await OrgName(orgId);
					var who = string.IsNullOrWhiteSpace(u.DisplayName) ? u.Email : u.DisplayName;
					admin.Add(new
					{
						kind = "selfjoin",
						orgId,
						orgName = name,
						userId = u.Id,
						who,
						message = $"{who} joined {name}",
						createdAt = u.CreatedAt,
						href = "/admin-backend.html",
					});
				}
			}
		}

		return await HttpHelper.OkJson(req, new { personal, unread, admin, actionCount = admin.Count });
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

	// DELETE /api/notifications/{id} — remove one of the caller's own notifications. The userId
	// partition key is the caller's, so anyone else's id resolves to NotFound.
	[Function("DeleteNotification")]
	public async Task<HttpResponseData> Delete(
		[HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "notifications/{id}")] HttpRequestData req,
		string id)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var deleted = await cosmos.DeleteNotificationAsync(id, ctx.UserId);
		if (!deleted) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Notification not found");

		return await HttpHelper.OkJson(req, new { deleted = true, id });
	}

	// DELETE /api/notifications — clear ALL of the caller's own notifications.
	[Function("ClearNotifications")]
	public async Task<HttpResponseData> Clear(
		[HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "notifications")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var cleared = await cosmos.DeleteAllNotificationsForUserAsync(ctx.UserId);
		logger.LogInformation("[Notifications] {User} cleared {Count} notification(s)", ctx.UserId, cleared);
		return await HttpHelper.OkJson(req, new { cleared });
	}
}
