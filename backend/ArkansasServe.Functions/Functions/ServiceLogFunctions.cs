using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

public class ServiceLogFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<ServiceLogFunctions> logger)
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

	private async Task TryCreatePendingApprovalAsync(ServiceLog log)
	{
		try
		{
			await cosmos.CreatePendingApprovalAsync(new PendingApproval
			{
				SchoolId = log.SchoolId,
				ServiceLogId = log.Id,
				StudentId = log.StudentId,
				StudentName = log.StudentName,
				OrganizationName = log.OrganizationName,
				EventTitle = log.EventTitle,
				HoursLogged = log.HoursLogged,
				ServiceDate = log.ServiceDate
			});
		}
		catch (CosmosException ex)
		{
			logger.LogError(ex, "Cosmos DB error while creating pending approval for service log {ServiceLogId}", log.Id);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Unexpected error while creating pending approval for service log {ServiceLogId}", log.Id);
		}
	}

	private async Task TryReviewSideEffectsAsync(ServiceLog log)
	{
		try
		{
			await cosmos.DeletePendingApprovalByLogIdAsync(log.Id, log.SchoolId);

			var message = log.Status == "Approved"
				? $"Your {log.HoursLogged}h of service at {log.OrganizationName} ({log.EventTitle}) have been approved."
				: $"Your service log for {log.EventTitle} was not approved. Note: {log.ReviewNote ?? "No note provided."}";

			await cosmos.CreateNotificationAsync(new Notification
			{
				UserId = log.StudentId,
				Type = log.Status == "Approved" ? "HoursApproved" : "HoursRejected",
				Message = message,
				RelatedId = log.Id
			});
		}
		catch (CosmosException ex)
		{
			logger.LogError(ex, "Cosmos DB error while processing review side effects for service log {ServiceLogId}", log.Id);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Unexpected error while processing review side effects for service log {ServiceLogId}", log.Id);
		}
	}

	private sealed record ReviewRequest(string StudentId, string Status, string? ReviewNote);
}
