using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

public class EventFunctions(CosmosService cosmos, BlobService blob, AuthConfig authConfig, ILogger<EventFunctions> logger)
{
	[Function("GetEvents")]
	public async Task<HttpResponseData> GetEvents(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "events")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
		var schoolId = query["schoolId"];

		var effectiveSchoolId = ctx.IsStudent ? ctx.TenantId : schoolId;

		try
		{
			var events = await cosmos.GetUpcomingEventsCompatAsync(effectiveSchoolId);
			return await HttpHelper.OkJson(req, events);
		}
		catch (CosmosException ex)
		{
			logger.LogError(ex, "Cosmos error while loading events for user {UserId}", ctx.UserId);
			return await HttpHelper.Error(req, HttpStatusCode.InternalServerError, "Unable to load events");
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Unexpected error while loading events for user {UserId}", ctx.UserId);
			return await HttpHelper.Error(req, HttpStatusCode.InternalServerError, "Unable to load events");
		}
	}

	[Function("GetEventById")]
	public async Task<HttpResponseData> GetEvent(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "events/{id}")] HttpRequestData req,
		string id)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
		var orgId = query["organizationId"] ?? string.Empty;
		var evt = await cosmos.GetEventAsync(id, orgId);
		if (evt == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Event not found");
		return await HttpHelper.OkJson(req, evt);
	}

	[Function("CreateEvent")]
	public async Task<HttpResponseData> CreateEvent(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "OrgStaff", "PlatformAdmin");
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<Event>(req);
		if (body == null) return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Invalid request body");
		if (string.IsNullOrWhiteSpace(body.Title) || body.StartDateTime == default)
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Title and StartDateTime are required");

		body.OrganizationId = ctx.IsPlatformAdmin ? body.OrganizationId : ctx.TenantId;
		body.CreatedByUserId = ctx.UserId;
		body.Status = "Open";
		body.CurrentSlots = 0;

		var created = await cosmos.CreateEventAsync(body);
		return await HttpHelper.CreatedJson(req, created);
	}

	[Function("UpdateEvent")]
	public async Task<HttpResponseData> UpdateEvent(
		[HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "events/{id}")] HttpRequestData req,
		string id)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "OrgStaff", "PlatformAdmin");
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<Event>(req);
		if (body == null) return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Invalid request body");

		var existing = await cosmos.GetEventAsync(id, body.OrganizationId);
		if (existing == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Event not found");

		if (ctx.IsOrgStaff && existing.OrganizationId != ctx.TenantId)
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Cannot edit another organization's event");

		existing.Title = body.Title;
		existing.Description = body.Description;
		existing.Location = body.Location;
		existing.StartDateTime = body.StartDateTime;
		existing.EndDateTime = body.EndDateTime;
		existing.MaxSlots = body.MaxSlots;
		existing.HoursValue = body.HoursValue;
		existing.EligibleSchoolIds = body.EligibleSchoolIds;
		existing.PhotoUrl = body.PhotoUrl;
		existing.Category = body.Category;
		existing.GroupId = body.GroupId;
		existing.Status = body.Status;

		var updated = await cosmos.UpdateEventAsync(existing);
		return await HttpHelper.OkJson(req, updated);
	}

	[Function("GetEventRegistrations")]
	public async Task<HttpResponseData> GetRegistrations(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "events/{id}/registrations")] HttpRequestData req,
		string id)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "OrgStaff", "SchoolAdmin", "PlatformAdmin");
		if (ctx == null) return authError!;

		var registrations = await cosmos.GetRegistrationsByEventAsync(id);
		return await HttpHelper.OkJson(req, registrations);
	}

	[Function("GetUploadToken")]
	public async Task<HttpResponseData> GetUploadToken(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events/upload-token")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "OrgStaff", "PlatformAdmin");
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<UploadTokenRequest>(req);
		if (body == null) return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "fileName is required");

		var blobName = BlobService.GenerateBlobName("events", body.FileName);
		var sasUrl = blob.GenerateUploadSasToken("event-photos", blobName);
		var finalUrl = blob.GetPublicBlobUrl("event-photos", blobName);

		return await HttpHelper.OkJson(req, new { sasUrl, finalUrl, blobName });
	}

	[Function("GetOrgEvents")]
	public async Task<HttpResponseData> GetOrgEvents(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "org/events")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "OrgStaff", "PlatformAdmin");
		if (ctx == null) return authError!;

		var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

		// A PlatformAdmin may target any org via ?organizationId=; everyone else is
		// pinned to their own org.
		var requestedOrg = query["organizationId"];
		var orgId = ctx.IsPlatformAdmin && !string.IsNullOrWhiteSpace(requestedOrg)
			? requestedOrg
			: ctx.TenantId;

		var events = await cosmos.GetEventsByOrgAsync(orgId);

		// Narrow to the caller's granular scope. Enforcement is opt-in: an admin
		// with no assigned events/groups sees the whole org (nothing configured
		// yet); assigned EventAdmins/GroupAdmins are restricted to theirs.
		var actor = await cosmos.GetUserByExternalIdAsync(ctx.UserId, ctx.TenantId);
		var adminLevel = actor?.AdminLevel ?? string.Empty;
		var requestedGroup = query["groupId"];

		if (string.Equals(adminLevel, "EventAdmin", StringComparison.OrdinalIgnoreCase) && actor!.EventAdminEventIds.Count > 0)
		{
			var allowed = new HashSet<string>(actor.EventAdminEventIds);
			events = events.Where(e => allowed.Contains(e.Id)).ToList();
		}
		else if (string.Equals(adminLevel, "GroupAdmin", StringComparison.OrdinalIgnoreCase) && actor!.GroupIds.Count > 0)
		{
			// A GroupAdmin only ever sees their own groups; an out-of-scope
			// ?groupId is ignored rather than honored.
			var allowed = !string.IsNullOrWhiteSpace(requestedGroup) && actor.GroupIds.Contains(requestedGroup)
				? new HashSet<string> { requestedGroup }
				: new HashSet<string>(actor.GroupIds);
			events = events.Where(e => e.GroupId != null && allowed.Contains(e.GroupId)).ToList();
		}
		else if (!string.IsNullOrWhiteSpace(requestedGroup))
		{
			// OrganizationAdmin / SuperAdmin narrowing to a group via the switcher.
			events = events.Where(e => e.GroupId == requestedGroup).ToList();
		}

		return await HttpHelper.OkJson(req, events);
	}

	private sealed record UploadTokenRequest(string FileName);
}
