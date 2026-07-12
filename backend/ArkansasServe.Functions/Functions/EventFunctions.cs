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

		var effectiveSchoolId = ctx.IsStudentLevel ? ctx.TenantId : schoolId;

		try
		{
			var events = await cosmos.GetUpcomingEventsCompatAsync(effectiveSchoolId);
			foreach (var e in events) SignEventPhoto(e);
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
		SignEventPhoto(evt);
		return await HttpHelper.OkJson(req, evt);
	}

	[Function("CreateEvent")]
	public async Task<HttpResponseData> CreateEvent(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<Event>(req);
		if (body == null) return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Invalid request body");
		if (string.IsNullOrWhiteSpace(body.Title) || body.StartDateTime == default)
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Title and StartDateTime are required");

		// Per-org: authorize by the caller's membership IN THE TARGET ORG (or global
		// super), not their token level — a membership-based admin/super carries no
		// matching role on the token.
		var orgId = string.IsNullOrWhiteSpace(body.OrganizationId) ? ctx.TenantId : body.OrganizationId;
		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, orgId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.EventAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "You cannot create events in this organization");

		body.OrganizationId = orgId;
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
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<Event>(req);
		if (body == null) return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Invalid request body");

		var existing = await cosmos.GetEventAsync(id, body.OrganizationId);
		if (existing == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Event not found");

		// Per-org: must be EventAdmin+ in the event's own org (or global super).
		var editor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, existing.OrganizationId);
		if (editor == null || !AdminLevels.AtLeast(editor.AdminLevel, AdminLevels.EventAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Cannot edit this organization's event");

		existing.Title = body.Title;
		existing.Description = body.Description;
		existing.Location = body.Location;
		existing.StartDateTime = body.StartDateTime;
		existing.EndDateTime = body.EndDateTime;
		existing.MaxSlots = body.MaxSlots;
		existing.HoursValue = body.HoursValue;
		existing.EligibleSchoolIds = body.EligibleSchoolIds;
		existing.PhotoBlobName = body.PhotoBlobName;
		existing.PhotoUrl = body.PhotoUrl;
		existing.Category = body.Category;
		existing.Tags = body.Tags ?? [];
		existing.Requirements = body.Requirements;
		existing.ExternalUrl = body.ExternalUrl;
		existing.ContactName = body.ContactName;
		existing.ContactEmail = body.ContactEmail;
		existing.ContactPhone = body.ContactPhone;
		// Preserve each shift's filled count across edits (the admin form doesn't own it).
		var priorFilled = existing.Shifts.ToDictionary(s => s.Id, s => s.Filled);
		existing.Shifts = (body.Shifts ?? []).Select(s =>
		{
			if (priorFilled.TryGetValue(s.Id, out var f)) s.Filled = f;
			return s;
		}).ToList();
		existing.SignupQuestions = body.SignupQuestions ?? [];
		existing.GroupId = body.GroupId;
		if (!string.IsNullOrWhiteSpace(body.Visibility)) existing.Visibility = body.Visibility;
		existing.Status = body.Status;

		var updated = await cosmos.UpdateEventAsync(existing);
		return await HttpHelper.OkJson(req, updated);
	}

	[Function("GetEventRegistrations")]
	public async Task<HttpResponseData> GetRegistrations(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "events/{id}/registrations")] HttpRequestData req,
		string id)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		// Event registrations are addressed by event id (no org in the route), so authorize
		// by EventAdmin+ in any org (a global super's SuperAdmin membership clears this).
		if (!await cosmos.IsAtLeastInAnyOrgAsync(ctx.UserId, ctx.AdminLevel, AdminLevels.EventAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var registrations = await cosmos.GetRegistrationsByEventAsync(id);
		return await HttpHelper.OkJson(req, registrations);
	}

	[Function("GetUploadToken")]
	public async Task<HttpResponseData> GetUploadToken(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events/upload-token")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await cosmos.IsAtLeastInAnyOrgAsync(ctx.UserId, ctx.AdminLevel, AdminLevels.EventAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var body = await HttpHelper.ReadBody<UploadTokenRequest>(req);
		if (body == null) return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "fileName is required");

		var blobName = BlobService.GenerateBlobName("events", body.FileName);
		var sasUrl = blob.GenerateUploadSasToken("event-photos", blobName);

		// Return the stable blob NAME to persist on the event (not a URL). event-photos is
		// a private container, so the readable URL is a short-lived SAS minted at read time.
		return await HttpHelper.OkJson(req, new { sasUrl, blobName });
	}

	[Function("GetOrgEvents")]
	public async Task<HttpResponseData> GetOrgEvents(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "org/events")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
		var orgId = string.IsNullOrWhiteSpace(query["organizationId"]) ? ctx.TenantId : query["organizationId"]!;

		// Multi-org: authorize by the caller's membership IN THE TARGET ORG (their
		// role/groups there), not their token org. Requires EventAdmin+ in that org.
		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, orgId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.EventAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "You do not have event access in this organization");

		var events = await cosmos.GetEventsByOrgAsync(orgId);

		// Narrow to the caller's granular scope. Enforcement is opt-in: an admin
		// with no assigned events/groups sees the whole org (nothing configured
		// yet); assigned EventAdmins/GroupAdmins are restricted to theirs.
		var adminLevel = actor.AdminLevel;
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

		foreach (var e in events) SignEventPhoto(e);
		return await HttpHelper.OkJson(req, events);
	}

	// Replaces an internally-stored event photo with a short-lived read SAS URL, since the
	// event-photos container is private. Uses PhotoBlobName when set; otherwise falls back to
	// deriving the blob name from a legacy bare PhotoUrl that points at our own container (so
	// pre-existing events don't need a data migration). External URLs (crawled events) and any
	// signing failure leave the stored PhotoUrl untouched — a missing photo must never break
	// event loading.
	private void SignEventPhoto(Event evt)
	{
		try
		{
			var blobName = !string.IsNullOrWhiteSpace(evt.PhotoBlobName)
				? evt.PhotoBlobName
				: blob.TryGetOwnedBlobName("event-photos", evt.PhotoUrl);

			if (string.IsNullOrWhiteSpace(blobName)) return;

			evt.PhotoBlobName = blobName;
			evt.PhotoUrl = blob.GenerateReadSasToken("event-photos", blobName);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Could not sign photo for event {EventId}; returning stored PhotoUrl.", evt.Id);
		}
	}

	private sealed record UploadTokenRequest(string FileName);
}
