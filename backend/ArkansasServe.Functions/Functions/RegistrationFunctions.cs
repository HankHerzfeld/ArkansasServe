using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

public class RegistrationFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<RegistrationFunctions> logger)
{
	[Function("CreateRegistration")]
	public async Task<HttpResponseData> Register(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "registrations")] HttpRequestData req)
	{
		// Any authenticated user may register themselves; ownership is enforced below.
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<RegistrationRequest>(req);
		if (body == null || string.IsNullOrEmpty(body.EventId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "eventId is required");

		var evt = await cosmos.GetEventAsync(body.EventId, body.OrganizationId ?? string.Empty);
		if (evt == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Event not found");
		if (evt.Status != "Open") return await HttpHelper.Error(req, HttpStatusCode.Conflict, "Event is not open for registration");
		if (evt.MaxSlots > 0 && evt.CurrentSlots >= evt.MaxSlots)
			return await HttpHelper.Error(req, HttpStatusCode.Conflict, "Event is full");

		if (await cosmos.IsAlreadyRegisteredAsync(body.EventId, ctx.UserId))
			return await HttpHelper.Error(req, HttpStatusCode.Conflict, "Already registered for this event");

		var reg = new EventRegistration
		{
			EventId = body.EventId,
			UserId = ctx.UserId,
			StudentName = ctx.DisplayName,
			SchoolId = ctx.TenantId,
			Status = "Registered"
		};

		var created = await cosmos.CreateRegistrationAsync(reg);

		const int maxRetries = 5;
		var slotUpdateSucceeded = false;
		for (var attempt = 0; attempt < maxRetries; attempt++)
		{
			var (freshEvt, etag) = await cosmos.GetEventWithETagAsync(body.EventId, body.OrganizationId ?? string.Empty);
			if (freshEvt == null) break;

			if (freshEvt.MaxSlots > 0 && freshEvt.CurrentSlots >= freshEvt.MaxSlots)
			{
				reg.Status = "Cancelled";
				await cosmos.UpdateRegistrationAsync(reg);
				return await HttpHelper.Error(req, HttpStatusCode.Conflict, "Event is full");
			}

			freshEvt.CurrentSlots++;
			if (freshEvt.MaxSlots > 0 && freshEvt.CurrentSlots >= freshEvt.MaxSlots) freshEvt.Status = "Full";

			try
			{
				await cosmos.UpdateEventAsync(freshEvt, etag);
				slotUpdateSucceeded = true;
				break;
			}
			catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
			{
				if (attempt == maxRetries - 1)
					logger.LogWarning("Failed to update slot count for event {EventId} after {Retries} retries due to concurrent modifications", body.EventId, maxRetries);
			}
		}

		if (!slotUpdateSucceeded)
		{
			reg.Status = "Cancelled";
			await cosmos.UpdateRegistrationAsync(reg);
			return await HttpHelper.Error(req, HttpStatusCode.ServiceUnavailable, "Unable to complete registration due to concurrent modifications. Please try again.");
		}

		return await HttpHelper.CreatedJson(req, created);
	}

	[Function("CancelRegistration")]
	public async Task<HttpResponseData> CancelRegistration(
		[HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "registrations/{id}")] HttpRequestData req,
		string id)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
		var eventId = query["eventId"] ?? string.Empty;

		var reg = await cosmos.GetRegistrationAsync(id, eventId);
		if (reg == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Registration not found");

		// A non-admin may only cancel their own registration.
		if (ctx.IsStudentLevel && reg.UserId != ctx.UserId)
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Cannot cancel another user's registration");

		if (reg.Status == "Cancelled")
			return await HttpHelper.Error(req, HttpStatusCode.Conflict, "Registration is already cancelled");

		reg.Status = "Cancelled";
		await cosmos.UpdateRegistrationAsync(reg);

		const int maxRetries = 5;
		for (var attempt = 0; attempt < maxRetries; attempt++)
		{
			var (freshEvt, etag) = await cosmos.GetEventWithETagAsync(reg.EventId, reg.SchoolId);
			if (freshEvt == null) break;

			if (freshEvt.CurrentSlots > 0) freshEvt.CurrentSlots--;
			if (freshEvt.Status == "Full" && freshEvt.CurrentSlots < freshEvt.MaxSlots) freshEvt.Status = "Open";

			try
			{
				await cosmos.UpdateEventAsync(freshEvt, etag);
				break;
			}
			catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
			{
				if (attempt == maxRetries - 1)
					logger.LogWarning("Failed to decrement slot count for event {EventId} after {Retries} retries", reg.EventId, maxRetries);
			}
		}

		return req.CreateResponse(HttpStatusCode.NoContent);
	}

	private sealed record RegistrationRequest(string EventId, string? OrganizationId);
}
