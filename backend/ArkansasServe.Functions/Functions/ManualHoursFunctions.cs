using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

public class ManualHoursFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<ManualHoursFunctions> logger)
{
	// Suggest existing events (in the org + public events from other orgs) that
	// match a title, so an admin reuses a shared event instead of duplicating it.
	[Function("MatchEvents")]
	public async Task<HttpResponseData> MatchEvents(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/events/match")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
		var orgId = string.IsNullOrWhiteSpace(query["organizationId"]) ? ctx.TenantId : query["organizationId"]!;
		var q = (query["q"] ?? string.Empty).Trim();

		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.Role, orgId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.EventAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var orgEvents = await cosmos.GetEventsByOrgAsync(orgId);
		var publicEvents = (await cosmos.GetPublicEventsAsync()).Where(e => e.OrganizationId != orgId);
		var all = orgEvents.Concat(publicEvents);

		var matches = string.IsNullOrWhiteSpace(q)
			? all
			: all.Where(e => (e.Title ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase)
						  || (e.Description ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase));

		var result = matches
			.OrderByDescending(e => e.StartDateTime)
			.Take(10)
			.Select(e => new
			{
				id = e.Id,
				title = e.Title,
				organizationId = e.OrganizationId,
				organizationName = e.OrganizationName,
				visibility = e.Visibility,
				startDateTime = e.StartDateTime,
			})
			.ToList();
		return await HttpHelper.OkJson(req, result);
	}

	// Log service for many volunteers at once — auto-approved (the admin is
	// vouching), optionally creating a shared event. One save credits everyone.
	[Function("BulkLogService")]
	public async Task<HttpResponseData> BulkLogService(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/servicelogs/bulk")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<BulkLogRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.OrganizationId) || body.VolunteerIds is not { Count: > 0 } || body.HoursLogged <= 0)
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "organizationId, volunteerIds and hoursLogged > 0 are required");

		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.Role, body.OrganizationId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.EventAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var tenant = await cosmos.GetTenantAsync(body.OrganizationId);
		var orgName = tenant?.Name ?? body.OrganizationId;

		// Resolve or create the event the hours reference.
		var eventId = body.EventId ?? string.Empty;
		var eventTitle = body.EventTitle ?? "Off-site service";
		if (string.IsNullOrWhiteSpace(body.EventId) && !string.IsNullOrWhiteSpace(body.NewEventTitle))
		{
			var newEvent = new Event
			{
				OrganizationId = body.OrganizationId,
				OrganizationName = orgName,
				Title = body.NewEventTitle,
				Description = body.NewEventDescription,
				Location = body.NewEventLocation ?? string.Empty,
				StartDateTime = body.ServiceDate,
				EndDateTime = body.ServiceDate,
				HoursValue = body.HoursLogged,
				Status = "Completed",
				Visibility = string.Equals(body.Visibility, "public", StringComparison.OrdinalIgnoreCase) ? "public" : "org",
				CreatedByUserId = ctx.UserId,
			};
			var createdEvent = await cosmos.CreateEventAsync(newEvent);
			eventId = createdEvent.Id;
			eventTitle = createdEvent.Title;
		}

		// Logs belong to the volunteers' org (for reporting), even if the event is
		// a shared public one owned elsewhere.
		var created = new List<ServiceLog>();
		foreach (var volunteerId in body.VolunteerIds)
		{
			var vol = await cosmos.GetUserByIdAsync(volunteerId, body.OrganizationId);
			if (vol == null) continue;

			var log = new ServiceLog
			{
				StudentId = string.IsNullOrWhiteSpace(vol.ExternalId) ? vol.Id : vol.ExternalId,
				StudentName = vol.DisplayName,
				SchoolId = body.OrganizationId,
				EventId = eventId,
				EventTitle = eventTitle,
				OrganizationId = body.OrganizationId,
				OrganizationName = orgName,
				HoursLogged = body.HoursLogged,
				ServiceDate = body.ServiceDate,
				Status = "Approved",
				SubmittedByUserId = ctx.UserId,
				ReviewedByUserId = ctx.UserId,
				ReviewedAt = DateTime.UtcNow,
				ReviewNote = body.Note,
			};
			created.Add(await cosmos.CreateServiceLogAsync(log));
		}

		return await HttpHelper.OkJson(req, new { count = created.Count, eventId, logs = created });
	}

	// Bulk import from a spreadsheet: each row links a volunteer by email (creating
	// a managed record if new) and logs approved hours, reusing/creating events by
	// title. Returns a per-row result so the client can report failures.
	[Function("ImportServiceLogs")]
	public async Task<HttpResponseData> ImportServiceLogs(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/servicelogs/import")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<ImportRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.OrganizationId) || body.Rows is not { Count: > 0 })
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "organizationId and rows are required");

		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.Role, body.OrganizationId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.EventAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

		var tenant = await cosmos.GetTenantAsync(body.OrganizationId);
		var orgName = tenant?.Name ?? body.OrganizationId;

		// Reuse events by title so repeated rows share one event.
		var existingEvents = (await cosmos.GetEventsByOrgAsync(body.OrganizationId))
			.GroupBy(e => (e.Title ?? string.Empty).Trim().ToLowerInvariant())
			.ToDictionary(g => g.Key, g => g.First());
		var eventCache = new Dictionary<string, (string Id, string Title)>(StringComparer.OrdinalIgnoreCase);

		var results = new List<object>();
		var imported = 0;
		for (var i = 0; i < body.Rows.Count; i++)
		{
			var row = body.Rows[i];
			var rowNum = i + 1;
			try
			{
				var email = (row.Email ?? string.Empty).Trim().ToLowerInvariant();
				if (string.IsNullOrWhiteSpace(email)) throw new InvalidOperationException("Email is required");
				if (row.Hours <= 0) throw new InvalidOperationException("Hours must be greater than 0");
				if (!DateTime.TryParse(row.Date, out var date)) throw new InvalidOperationException("Invalid date");

				var vol = await cosmos.GetMembershipByEmailAsync(email, body.OrganizationId)
					?? await cosmos.CreateManagedVolunteerAsync(new User
					{
						ExternalId = string.Empty,
						TenantId = body.OrganizationId,
						OrganizationId = body.OrganizationId,
						Email = email,
						DisplayName = string.IsNullOrWhiteSpace(row.Name) ? email : row.Name.Trim(),
						Role = "Student",
						AdminLevel = AdminLevels.Student,
						Status = "active",
						IsManaged = true,
						ManagedByUserId = ctx.UserId,
					});

				var eventId = string.Empty;
				var eventTitle = "Off-site service";
				var evtTitle = (row.Event ?? string.Empty).Trim();
				if (!string.IsNullOrWhiteSpace(evtTitle))
				{
					var key = evtTitle.ToLowerInvariant();
					if (eventCache.TryGetValue(key, out var cached)) { eventId = cached.Id; eventTitle = cached.Title; }
					else if (existingEvents.TryGetValue(key, out var ex)) { eventId = ex.Id; eventTitle = ex.Title; eventCache[key] = (ex.Id, ex.Title); }
					else
					{
						var newEvt = await cosmos.CreateEventAsync(new Event
						{
							OrganizationId = body.OrganizationId,
							OrganizationName = orgName,
							Title = evtTitle,
							StartDateTime = date,
							EndDateTime = date,
							HoursValue = row.Hours,
							Status = "Completed",
							Visibility = "org",
							CreatedByUserId = ctx.UserId,
						});
						eventId = newEvt.Id;
						eventTitle = newEvt.Title;
						eventCache[key] = (newEvt.Id, newEvt.Title);
					}
				}

				await cosmos.CreateServiceLogAsync(new ServiceLog
				{
					StudentId = string.IsNullOrWhiteSpace(vol.ExternalId) ? vol.Id : vol.ExternalId,
					StudentName = vol.DisplayName,
					SchoolId = body.OrganizationId,
					EventId = eventId,
					EventTitle = eventTitle,
					OrganizationId = body.OrganizationId,
					OrganizationName = orgName,
					HoursLogged = row.Hours,
					ServiceDate = date,
					Status = "Approved",
					SubmittedByUserId = ctx.UserId,
					ReviewedByUserId = ctx.UserId,
					ReviewedAt = DateTime.UtcNow,
				});

				imported++;
				results.Add(new { row = rowNum, email, ok = true });
			}
			catch (Exception ex)
			{
				results.Add(new { row = rowNum, email = row.Email, ok = false, message = ex.Message });
			}
		}

		return await HttpHelper.OkJson(req, new { imported, failed = body.Rows.Count - imported, results });
	}

	private sealed record ImportRequest(string OrganizationId, List<ImportRow> Rows);

	private sealed record ImportRow(string? Email, string? Name, double Hours, string? Date, string? Event);

	private sealed record BulkLogRequest(
		string OrganizationId,
		List<string> VolunteerIds,
		double HoursLogged,
		DateTime ServiceDate,
		string? Note,
		string? EventId,
		string? EventTitle,
		string? NewEventTitle,
		string? NewEventDescription,
		string? NewEventLocation,
		string? Visibility);
}
