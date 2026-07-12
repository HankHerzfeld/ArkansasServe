using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

// SuperAdmin remote access — Phase F #26, Phase 1: DEMO USERS ONLY, read-only.
// Start/stop create and end an ImpersonationSession; AuthMiddleware resolves the
// effective (target) context from it per request. See the design doc.
public class ImpersonationFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<ImpersonationFunctions> logger)
{
	// Phase 1 hard limits.
	private const string Phase1Mode = "read-only";
	private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);

	[Function("StartImpersonation")]
	public async Task<HttpResponseData> Start(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/impersonation")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		// No nested impersonation, and only a real global super may start one.
		if (ctx.IsImpersonating)
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Cannot start impersonation while already impersonating");
		if (!await IsGlobalSuperAsync(ctx))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "SuperAdmin only");

		var body = await HttpHelper.ReadBody<StartRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.TargetUserId)
			|| string.IsNullOrWhiteSpace(body.TargetTenantId) || string.IsNullOrWhiteSpace(body.Reason))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "targetUserId, targetTenantId and reason are required");

		var target = await cosmos.GetUserByIdAsync(body.TargetUserId, body.TargetTenantId);
		if (target == null)
			return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Target user not found");

		// Phase 1 gate: demo users only. Real-user impersonation is a later phase.
		if (!target.IsDemoUser)
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Phase 1 allows impersonating demo users only");

		var effectiveLevel = !string.IsNullOrWhiteSpace(target.DemoUserType) ? target.DemoUserType! : target.AdminLevel;
		var now = DateTime.UtcNow;
		var session = new ImpersonationSession
		{
			AdminUserId = ctx.UserId,
			AdminName = ctx.DisplayName,
			AdminEmail = ctx.Email,
			TargetUserId = target.Id,
			TargetActingId = string.IsNullOrWhiteSpace(target.ExternalId) ? target.Id : target.ExternalId,
			TargetTenantId = target.TenantId,
			TargetName = target.DisplayName,
			TargetEmail = target.Email,
			TargetAdminLevel = effectiveLevel,
			TargetIsDemo = true,
			Reason = body.Reason.Trim(),
			Mode = Phase1Mode,
			StartedAt = now,
			ExpiresAt = now.Add(SessionLifetime),
		};

		var created = await cosmos.CreateImpersonationSessionAsync(session);

		// Fail-closed audit: if we can't record the start, the session must not stand.
		try
		{
			await cosmos.AppendAuditEventAsync(new AuditEvent
			{
				AdminUserId = ctx.UserId,
				SessionId = created.Id,
				TargetUserId = target.Id,
				Action = "impersonation.start",
				Detail = $"{ctx.Email} → {target.DisplayName} ({effectiveLevel}, demo); reason: {session.Reason}",
			});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Audit write failed for impersonation start; rolling back session {SessionId}", created.Id);
			await cosmos.DeleteImpersonationSessionAsync(created.Id, ctx.UserId);
			return await HttpHelper.Error(req, HttpStatusCode.InternalServerError, "Could not start impersonation (audit unavailable)");
		}

		return await HttpHelper.CreatedJson(req, new
		{
			sessionId = created.Id,
			mode = created.Mode,
			expiresAt = created.ExpiresAt,
			target = new { id = target.Id, name = target.DisplayName, email = target.Email, adminLevel = effectiveLevel },
		});
	}

	[Function("StopImpersonation")]
	public async Task<HttpResponseData> Stop(
		[HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "manage/impersonation/{sid}")] HttpRequestData req,
		string sid)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		// The caller may be mid-session (header still set) or a super ending their own.
		var adminId = ctx.IsImpersonating ? ctx.RealUserId : ctx.UserId;
		var ended = await cosmos.EndImpersonationSessionAsync(sid, adminId);
		if (ended == null)
			return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Session not found");

		await cosmos.AppendAuditEventAsync(new AuditEvent
		{
			AdminUserId = adminId,
			SessionId = ended.Id,
			TargetUserId = ended.TargetUserId,
			Action = "impersonation.stop",
			Detail = $"ended session for {ended.TargetName}",
		});

		return await HttpHelper.OkJson(req, new { ended = true, sessionId = ended.Id });
	}

	[Function("ListImpersonationSessions")]
	public async Task<HttpResponseData> List(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/impersonation")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (ctx.IsImpersonating || !await IsGlobalSuperAsync(ctx))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "SuperAdmin only");

		var sessions = await cosmos.GetImpersonationSessionsByAdminAsync(ctx.UserId);
		return await HttpHelper.OkJson(req, sessions);
	}

	private Task<bool> IsGlobalSuperAsync(UserContext ctx) =>
		cosmos.IsGlobalSuperAsync(ctx.UserId, ctx.AdminLevel);

	private sealed record StartRequest(string TargetUserId, string TargetTenantId, string Reason);
}
