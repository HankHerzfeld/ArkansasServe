using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
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
			await FillOrganizationNamesAsync(events);
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

		// Attach the viewer's own active registration so the detail page can render a
		// "you're signed up" state + Cancel, instead of always offering Sign Up (which
		// then 409s on the already-registered guard). Merged onto the response only —
		// never persisted. Uses camelCase to match the API's serialization.
		var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
		var payload = JsonSerializer.SerializeToNode(evt, opts)!.AsObject();
		try
		{
			var regs = await cosmos.GetRegistrationsByEventAsync(id);
			var self = await cosmos.GetUserByExternalIdAsync(ctx.UserId, ctx.TenantId);
			var mine = regs.FirstOrDefault(r =>
				r.BelongsTo(ctx.UserId, self?.Id)
				&& !string.Equals(r.Status, "Cancelled", StringComparison.OrdinalIgnoreCase));
			payload["myRegistration"] = mine == null ? null : JsonSerializer.SerializeToNode(mine, opts);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Could not attach viewer registration for event {EventId}", id);
		}
		return await HttpHelper.OkJson(req, payload);
	}

	[Function("DeleteEvent")]
	public async Task<HttpResponseData> DeleteEvent(
		[HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "events/{id}")] HttpRequestData req,
		string id)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var orgId = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["organizationId"] ?? string.Empty;
		var evt = await cosmos.GetEventAsync(id, orgId);
		if (evt == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Event not found");

		// Per-org: deleting an event is destructive, so require OrganizationAdmin+ in the
		// event's own org (a global super clears this everywhere). Authorize against the
		// event's authoritative OrganizationId, not the query string.
		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, evt.OrganizationId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.OrganizationAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "You cannot delete events in this organization");

		var registrationsRemoved = await cosmos.DeleteEventCascadeAsync(id, evt.OrganizationId);
		logger.LogInformation("Deleted event {EventId} in org {OrgId} ({Count} registrations removed) by {UserId}",
			id, evt.OrganizationId, registrationsRemoved, ctx.UserId);
		return await HttpHelper.OkJson(req, new { deleted = true, eventId = id, registrationsRemoved });
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

		// Denormalize the org name, as service logs already do (ServiceLogFunctions). It was
		// never set here, so every event created through the app carried
		// organizationName:"" — only crawled events had one (CrawlerService sets it). The
		// events page then advertised "Search by name or organization" while the
		// organization half could never match anything.
		if (string.IsNullOrWhiteSpace(body.OrganizationName))
		{
			var org = await cosmos.GetTenantAsync(orgId);
			if (org != null) body.OrganizationName = org.Name;
		}

		// One-off event: unchanged.
		if (body.Recurrence == null)
		{
			body.SeriesId = null;
			var created = await cosmos.CreateEventAsync(body);
			return await HttpHelper.CreatedJson(req, created);
		}

		// ── Recurring series ────────────────────────────────────────────────────
		var (starts, ruleError) = RecurrenceExpander.Expand(body.StartDateTime, body.Recurrence);
		if (ruleError != null) return await HttpHelper.Error(req, HttpStatusCode.BadRequest, ruleError);

		var seriesId = Guid.NewGuid().ToString();
		var duration = body.EndDateTime - body.StartDateTime;
		// Shifts carry ABSOLUTE datetimes, so each occurrence's shifts have to move with it.
		// Offsetting from the template's own start keeps each shift at the same point within
		// the day (a 1:08pm shift on day 1 is 1:08pm on day 8), which is the only reading that
		// survives the series being generated on the local calendar.
		var shiftOffsets = body.Shifts.Select(s => new
		{
			Template = s,
			StartOffset = s.StartDateTime.HasValue ? s.StartDateTime.Value - body.StartDateTime : (TimeSpan?)null,
			EndOffset = s.EndDateTime.HasValue ? s.EndDateTime.Value - body.StartDateTime : (TimeSpan?)null,
		}).ToList();

		var occurrences = new List<Event>();
		foreach (var start in starts!)
		{
			occurrences.Add(new Event
			{
				OrganizationId = body.OrganizationId,
				OrganizationName = body.OrganizationName,
				Title = body.Title,
				Description = body.Description,
				Location = body.Location,
				StartDateTime = start,
				EndDateTime = start + duration,
				SeriesId = seriesId,
				Recurrence = body.Recurrence,
				MaxSlots = body.MaxSlots,
				// Every occurrence starts empty — capacity is per occurrence, never shared.
				CurrentSlots = 0,
				Shifts = shiftOffsets.Select(s => new EventShift
				{
					// Fresh id per occurrence: a shift is a distinct thing on each date, and
					// reusing one id across twelve dates would make "shift 3" ambiguous the
					// moment anything reports across a series.
					Id = Guid.NewGuid().ToString(),
					Label = s.Template.Label,
					StartDateTime = s.StartOffset.HasValue ? start + s.StartOffset.Value : null,
					EndDateTime = s.EndOffset.HasValue ? start + s.EndOffset.Value : null,
					Capacity = s.Template.Capacity,
					Filled = 0,
				}).ToList(),
				SignupQuestions = body.SignupQuestions,
				HoursValue = body.HoursValue,
				Status = "Open",
				EligibleSchoolIds = body.EligibleSchoolIds,
				PhotoBlobName = body.PhotoBlobName,
				PhotoUrl = body.PhotoUrl,
				Category = body.Category,
				Tags = body.Tags,
				Requirements = body.Requirements,
				ExternalUrl = body.ExternalUrl,
				ContactName = body.ContactName,
				ContactEmail = body.ContactEmail,
				ContactPhone = body.ContactPhone,
				ContactUrl = body.ContactUrl,
				GroupId = body.GroupId,
				Visibility = body.Visibility,
				CreatedByUserId = ctx.UserId,
			});
		}

		var saved = new List<Event>();
		try
		{
			foreach (var occ in occurrences) saved.Add(await cosmos.CreateEventAsync(occ));
		}
		catch (Exception ex)
		{
			// A half-written series is worse than none: the admin sees "created" and only
			// finds the gaps weeks later. Remove what landed and report the failure. Nothing
			// can have been registered yet — these ids are seconds old and unpublished.
			logger.LogError(ex, "[Recurrence] Failed writing series {SeriesId}; removing {Count} occurrence(s) already created", seriesId, saved.Count);
			foreach (var s in saved)
			{
				try { await cosmos.DeleteEventCascadeAsync(s.Id, s.OrganizationId); }
				catch (Exception cleanupEx) { logger.LogError(cleanupEx, "[Recurrence] Could not remove partial occurrence {EventId}", s.Id); }
			}
			return await HttpHelper.Error(req, HttpStatusCode.InternalServerError, "Could not create the series. No events were created.");
		}

		logger.LogInformation("[Recurrence] Created series {SeriesId} with {Count} occurrence(s) in org {OrgId}", seriesId, saved.Count, orgId);
		return await HttpHelper.CreatedJson(req, new
		{
			seriesId,
			created = saved.Count,
			occurrences = saved,
		});
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
	// Fill in organizationName for events that don't carry one, so existing records resolve
	// without a data migration (the same approach SignEventPhoto takes for photos).
	//
	// CreateEvent never set it, so every app-created event has organizationName:"" — only
	// crawled events had one. That left the events page offering "Search by name or
	// organization" when the organization half could never match. CreateEvent now
	// denormalizes it for new events; this covers everything already stored.
	//
	// Tenants are looked up once per DISTINCT org, not once per event: a page of events is
	// typically a handful of orgs, and this runs on every events load.
	private async Task FillOrganizationNamesAsync(List<Event> events)
	{
		var missing = events
			.Where(e => string.IsNullOrWhiteSpace(e.OrganizationName) && !string.IsNullOrWhiteSpace(e.OrganizationId))
			.ToList();
		if (missing.Count == 0) return;

		var names = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var orgId in missing.Select(e => e.OrganizationId!).Distinct(StringComparer.Ordinal))
		{
			try
			{
				var org = await cosmos.GetTenantAsync(orgId);
				if (org != null && !string.IsNullOrWhiteSpace(org.Name)) names[orgId] = org.Name;
			}
			catch (Exception ex)
			{
				// Best-effort: a name we can't resolve must never break event loading.
				logger.LogWarning(ex, "Could not resolve organization name for {OrgId} while listing events", orgId);
			}
		}

		foreach (var e in missing)
		{
			if (names.TryGetValue(e.OrganizationId!, out var name)) e.OrganizationName = name;
		}
	}

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
