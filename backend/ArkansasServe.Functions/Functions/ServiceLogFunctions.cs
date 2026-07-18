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
	// Stamped as the reviewer on a log auto-approved by a school's #12 policy — a sentinel, not a
	// person, so the audit trail distinguishes policy auto-approval from a human review.
	private const string SystemPolicyReviewer = "system:school-policy";

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

		// Load the event once — its title denormalizes onto the log, and its category feeds the
		// school's approval policy below (#12).
		Event? evt = null;
		if (!string.IsNullOrWhiteSpace(body.EventId))
			evt = await cosmos.GetEventAsync(body.EventId, orgId);

		// Denormalize the event/org names so the student's dashboard and the report
		// can render them without a per-row lookup. The client only sends ids; trust
		// the server's copy of the title/name, not client-supplied strings.
		if (string.IsNullOrWhiteSpace(body.EventTitle) && evt != null)
			body.EventTitle = evt.Title;
		if (string.IsNullOrWhiteSpace(body.OrganizationName))
		{
			var org = await cosmos.GetTenantAsync(orgId);
			if (org != null) body.OrganizationName = org.Name;
		}

		// #12: the student's School/JDC may PREAPPROVE this org (and/or its category), in which
		// case the hours auto-count instead of queueing for review. Anything else — the default,
		// an unconfigured school, or a student with no school — keeps the Pending + queue path,
		// so behaviour is unchanged until a school opts in.
		if (!string.IsNullOrWhiteSpace(body.SchoolId))
		{
			var school = await cosmos.GetTenantAsync(body.SchoolId);
			if (school?.ApprovalPolicy != null
				&& school.ApprovalPolicy.Resolve(body.OrganizationId, evt?.Category) == ApprovalPolicies.Preapproved)
			{
				body.Status = "Approved";
				body.ReviewedByUserId = SystemPolicyReviewer;
				body.ReviewedAt = DateTime.UtcNow;
				body.ReviewNote = "Auto-approved: this organization is preapproved by the school.";
			}
		}

		var created = await cosmos.CreateServiceLogAsync(body);

		// A preapproved log is already Approved — mirror the review outcome (notify/email the
		// student; there is no pending pointer to remove). Everything else enters the queue.
		if (string.Equals(created.Status, "Approved", StringComparison.OrdinalIgnoreCase))
			await TryReviewSideEffectsAsync(created);
		else
			await TryCreatePendingApprovalAsync(created);

		// #13: tell the volunteer's assigned admins, each per their own prefs. Best-effort — a
		// lost notification must never fail the log.
		try { await NotifyAssignedAdminsAsync(created); }
		catch (Exception ex) { logger.LogWarning(ex, "Assigned-admin notification failed for service log {ServiceLogId}", created.Id); }

		return await HttpHelper.CreatedJson(req, created);
	}

	/// <summary>
	/// Notifies the admins overseeing this volunteer (#13), fanning out over their per-org
	/// assignments and honouring each admin's own notify-on-hours / notify-on-approval prefs.
	/// Assignments live on the volunteer's User doc IN THE EVENT'S ORG; an admin who never signs
	/// in (no externalId) is skipped since notifications are keyed by the recipient's login id.
	/// </summary>
	private async Task NotifyAssignedAdminsAsync(ServiceLog log)
	{
		var volunteer = await cosmos.GetUserByIdAsync(log.StudentId, log.OrganizationId)
			?? await cosmos.GetUserByExternalIdAsync(log.StudentId, log.OrganizationId);
		if (volunteer == null || volunteer.AssignedAdmins.Count == 0) return;

		var needsApproval = string.Equals(log.Status, "Pending", StringComparison.OrdinalIgnoreCase);
		foreach (var assignment in volunteer.AssignedAdmins)
		{
			var admin = await cosmos.GetUserByIdAsync(assignment.AdminId, log.OrganizationId);
			var recipient = admin?.ExternalId;
			if (string.IsNullOrWhiteSpace(recipient)) continue;

			if (assignment.NotifyOnHours)
				await cosmos.CreateNotificationAsync(new Notification
				{
					UserId = recipient,
					Type = "AssignedHoursLogged",
					Message = $"{log.StudentName} logged {log.HoursLogged}h at {log.OrganizationName} ({log.EventTitle}).",
					RelatedId = log.Id,
				});

			if (needsApproval && assignment.NotifyOnApproval)
				await cosmos.CreateNotificationAsync(new Notification
				{
					UserId = recipient,
					Type = "AssignedApprovalNeeded",
					Message = $"{log.StudentName}'s {log.HoursLogged}h need approval.",
					RelatedId = log.Id,
				});
		}
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

	[Function("DeleteServiceLog")]
	public async Task<HttpResponseData> DeleteLog(
		[HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "servicelogs/{id}")] HttpRequestData req,
		string id)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		// studentId is the ServiceLogs partition key; required to locate the log.
		var studentId = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["studentId"];
		if (string.IsNullOrWhiteSpace(studentId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "studentId is required");

		var log = await cosmos.GetServiceLogAsync(id, studentId);
		if (log == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Service log not found");

		// Per-org: voiding a log is admin-only — same rule as reviewing it (OrganizationAdmin+
		// in the log's school, or a global super).
		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, log.SchoolId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.OrganizationAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Cannot void logs for this school");

		await cosmos.DeleteServiceLogAsync(id, studentId);
		// Clear any lingering pending-approval pointer (harmless if the log was already reviewed).
		try { await cosmos.DeletePendingApprovalByLogIdAsync(id, log.SchoolId); }
		catch (Exception ex) { logger.LogWarning(ex, "Void: pending-approval cleanup failed for log {ServiceLogId}", id); }

		logger.LogInformation("Voided service log {ServiceLogId} (student {StudentId}, {Hours}h) by {UserId}",
			id, studentId, log.HoursLogged, ctx.UserId);
		return await HttpHelper.OkJson(req, new { deleted = true, serviceLogId = id });
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
