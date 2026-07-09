using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

public class ServiceLogFunctions(CosmosService cosmos, EmailService email, AuthConfig authConfig, ILogger<ServiceLogFunctions> logger)
{
	[Function("CreateServiceLog")]
	public async Task<HttpResponseData> CreateLog(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "servicelogs")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<ServiceLog>(req);
		if (body == null || string.IsNullOrEmpty(body.StudentId) || body.HoursLogged <= 0)
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "studentId and hoursLogged > 0 are required");

		// Per-org: the caller must be an EventAdmin+ in the org the log is for.
		var orgId = string.IsNullOrWhiteSpace(body.OrganizationId) ? ctx.TenantId : body.OrganizationId;
		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, orgId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.EventAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "You cannot log service in this organization");

		body.OrganizationId = orgId;
		body.SubmittedByUserId = ctx.UserId;
		body.Status = "Pending";

		var created = await cosmos.CreateServiceLogAsync(body);
		await TryCreatePendingApprovalAsync(created);

		return await HttpHelper.CreatedJson(req, created);
	}

	[Function("ReviewServiceLog")]
	public async Task<HttpResponseData> ReviewLog(
		[HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "servicelogs/{id}")] HttpRequestData req,
		string id)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<ReviewRequest>(req);
		if (body == null || (body.Status != "Approved" && body.Status != "Rejected"))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "status must be 'Approved' or 'Rejected'");

		var log = await cosmos.GetServiceLogAsync(id, body.StudentId);
		if (log == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Service log not found");

		// Per-org: the reviewer must be an OrganizationAdmin+ in the log's school.
		var reviewer = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, log.SchoolId);
		if (reviewer == null || !AdminLevels.AtLeast(reviewer.AdminLevel, AdminLevels.OrganizationAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Cannot review logs for this school");

		log.Status = body.Status;
		log.ReviewNote = body.ReviewNote;
		log.ReviewedByUserId = ctx.UserId;
		log.ReviewedAt = DateTime.UtcNow;

		var updated = await cosmos.UpdateServiceLogAsync(log);
		await TryReviewSideEffectsAsync(updated);

		return await HttpHelper.OkJson(req, updated);
	}

	[Function("GetMyServiceLogs")]
	public async Task<HttpResponseData> GetMyLogs(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "students/me/servicelogs")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var logs = await cosmos.GetServiceLogsByStudentAsync(ctx.UserId);
		var total = logs.Where(l => l.Status == "Approved").Sum(l => l.HoursLogged);
		return await HttpHelper.OkJson(req, new { logs, totalApprovedHours = total });
	}

	[Function("GetServiceLog")]
	public async Task<HttpResponseData> GetLog(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "servicelogs/{id}")] HttpRequestData req,
		string id)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		// studentId is the ServiceLogs partition key. A student fetching their own log can omit
		// it (defaults to the caller); an admin passes ?studentId= (as the review flow does).
		var studentId = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["studentId"];
		if (string.IsNullOrWhiteSpace(studentId)) studentId = ctx.UserId;

		var log = await cosmos.GetServiceLogAsync(id, studentId);
		if (log == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Service log not found");

		// The owning student may read their own log; anyone else must be an OrganizationAdmin+
		// in the log's school (same rule as the review flow).
		if (!string.Equals(log.StudentId, ctx.UserId, StringComparison.Ordinal))
		{
			var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, log.SchoolId);
			if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.OrganizationAdmin))
				return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Cannot view logs for this school");
		}

		return await HttpHelper.OkJson(req, log);
	}

	// Create the pending-approval pointer for a freshly-submitted log. Uses a deterministic
	// pointer id so this is idempotent, and retries transient faults. A terminal failure is
	// recoverable: the admin queue self-heals via reconciliation on read (see CosmosService.Reconciliation).
	private async Task TryCreatePendingApprovalAsync(ServiceLog log)
	{
		try
		{
			await CosmosRetry.ExecuteAsync(
				() => cosmos.CreatePendingApprovalAsync(CosmosService.PendingApprovalFromLog(log)),
				logger, "CreatePendingApproval");
		}
		catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
		{
			// Pointer already exists (deterministic id) — submit is idempotent.
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to create pending approval for service log {ServiceLogId}; will be reconciled on queue load", log.Id);
		}
	}

	// Review side effects run independently so one failing does not skip the other. Both are
	// recoverable: a lost pending-pointer delete is reconciled on the admin queue read; the
	// student always sees the final status in their own log list even if the notification is lost.
	private async Task TryReviewSideEffectsAsync(ServiceLog log)
	{
		try
		{
			await CosmosRetry.ExecuteAsync(
				() => cosmos.DeletePendingApprovalByLogIdAsync(log.Id, log.SchoolId),
				logger, "DeletePendingApproval");
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to remove pending approval for service log {ServiceLogId}; will be reconciled on queue load", log.Id);
		}

		try
		{
			var message = log.Status == "Approved"
				? $"Your {log.HoursLogged}h of service at {log.OrganizationName} ({log.EventTitle}) have been approved."
				: $"Your service log for {log.EventTitle} was not approved. Note: {log.ReviewNote ?? "No note provided."}";

			await CosmosRetry.ExecuteAsync(
				() => cosmos.CreateNotificationAsync(new Notification
				{
					UserId = log.StudentId,
					Type = log.Status == "Approved" ? "HoursApproved" : "HoursRejected",
					Message = message,
					RelatedId = log.Id
				}),
				logger, "CreateNotification");
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to create review notification for service log {ServiceLogId}", log.Id);
		}

		// Email the student the same outcome. Best-effort and independent of the above; the
		// recipient lookup is skipped entirely unless ACS email is configured, so this adds no
		// cost when email is off.
		if (email.IsConfigured)
		{
			try
			{
				var student = await cosmos.GetUserByExternalIdAsync(log.StudentId, log.SchoolId);
				if (student != null && !string.IsNullOrWhiteSpace(student.Email))
				{
					var approved = log.Status == "Approved";
					var subject = approved ? "Your service hours were approved" : "Your service hours were not approved";
					var body = approved
						? $"Hi {student.DisplayName},\n\nYour {log.HoursLogged}h of service at {log.OrganizationName} ({log.EventTitle}) have been approved.\n\n— Arkansas Serve"
						: $"Hi {student.DisplayName},\n\nYour service log for {log.EventTitle} was not approved.\nNote: {log.ReviewNote ?? "No note provided."}\n\n— Arkansas Serve";
					await email.SendAsync(student.Email, subject, body);
				}
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Failed to send review email for service log {ServiceLogId}", log.Id);
			}
		}
	}

	private sealed record ReviewRequest(string StudentId, string Status, string? ReviewNote);
}
