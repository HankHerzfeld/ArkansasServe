using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

/// <summary>
/// Day-of check-in (#14). The admin running an event marks registrants present on the day,
/// by shift or by user. This slice is the online core — roster read and an idempotent
/// check-in toggle. Walk-ins, blockCheckIn tag enforcement, the QR entry point, and the
/// offline/PWA layer (#15) build on top of it.
///
/// Authorization mirrors CancelRegistration (Finding 9): acting on an event on the day is
/// EventAdmin+ IN THE EVENT'S OWN ORG — whoever runs the event clears no-shows and checks
/// people in. Deliberately lower than the destructive delete/void (OrganizationAdmin+), and
/// resolved per-org via ResolveActorInOrgAsync so a membership-based admin (who carries no
/// admin claim on their token) is not wrongly refused.
/// </summary>
public class CheckInFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<CheckInFunctions> logger)
{
	/// <summary>
	/// GET /api/events/{eventId}/roster?organizationId={org}
	/// The check-in view: the event's live (non-cancelled) registrations with their check-in
	/// state, plus the shift definitions so the client can group "by shift".
	/// </summary>
	[Function("GetEventRoster")]
	public async Task<HttpResponseData> GetRoster(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "events/{eventId}/roster")] HttpRequestData req,
		string eventId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var orgId = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["organizationId"] ?? string.Empty;
		if (string.IsNullOrWhiteSpace(orgId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "organizationId is required");

		var evt = await cosmos.GetEventAsync(eventId, orgId);
		if (evt == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Event not found");

		var authFail = await RequireEventAdmin(req, ctx, evt);
		if (authFail != null) return authFail;

		// Cancelled rows are not part of the day-of roster.
		var regs = (await cosmos.GetRegistrationsByEventAsync(eventId))
			.Where(r => !string.Equals(r.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
			.OrderBy(r => r.StudentName, StringComparer.OrdinalIgnoreCase)
			.Select(r => new
			{
				id = r.Id,
				memberId = r.MemberId,
				studentName = r.StudentName,
				schoolId = r.SchoolId,
				shiftId = r.ShiftId,
				status = r.Status,
				checkedInAt = r.CheckedInAt,
				// A registrant from another org can't (yet) be gated on the event org's tags —
				// same-org-only enforcement is the locked decision until cross-org tag state
				// lands. Surface it so the UI can label them, not to drive logic here.
				crossOrg = !string.Equals(r.SchoolId, evt.OrganizationId, StringComparison.OrdinalIgnoreCase),
			})
			.ToList();

		return await HttpHelper.OkJson(req, new
		{
			eventId = evt.Id,
			title = evt.Title,
			organizationId = evt.OrganizationId,
			startDateTime = evt.StartDateTime,
			shifts = evt.Shifts.Select(s => new { s.Id, s.Label, s.Capacity, s.Filled, s.StartDateTime, s.EndDateTime }),
			registrations = regs,
			checkedInCount = regs.Count(r => r.checkedInAt != null),
			totalCount = regs.Count,
		});
	}

	/// <summary>
	/// POST /api/events/{eventId}/checkin  body: { organizationId, registrationId, checkedIn }
	///
	/// One idempotent toggle rather than separate check-in / undo endpoints: the caller sends
	/// the DESIRED state (checkedIn true/false), so re-sending the same state is a safe no-op —
	/// which matters on a flaky venue connection where a request may be retried, and is exactly
	/// what the offline/PWA sync layer (#15) will replay. `checkedIn` defaults to true.
	/// </summary>
	[Function("SetRegistrationCheckIn")]
	public async Task<HttpResponseData> SetCheckIn(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events/{eventId}/checkin")] HttpRequestData req,
		string eventId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<CheckInRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.RegistrationId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "registrationId is required");
		if (string.IsNullOrWhiteSpace(body.OrganizationId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "organizationId is required");

		var evt = await cosmos.GetEventAsync(eventId, body.OrganizationId);
		if (evt == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Event not found");

		var authFail = await RequireEventAdmin(req, ctx, evt);
		if (authFail != null) return authFail;

		var reg = await cosmos.GetRegistrationAsync(body.RegistrationId, eventId);
		if (reg == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Registration not found");
		if (string.Equals(reg.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
			return await HttpHelper.Error(req, HttpStatusCode.Conflict, "That registration was cancelled");

		var wantCheckedIn = body.CheckedIn ?? true;
		var isCheckedIn = reg.CheckedInAt != null;

		// Idempotent: already in the desired state → return it unchanged, no write.
		if (wantCheckedIn != isCheckedIn)
		{
			reg.CheckedInAt = wantCheckedIn ? DateTime.UtcNow : null;
			await cosmos.UpdateRegistrationAsync(reg);
			logger.LogInformation("[CheckIn] {RegId} on event {EventId} set checkedIn={State} by {Actor}",
				reg.Id, eventId, wantCheckedIn, ctx.UserId);
		}

		return await HttpHelper.OkJson(req, new
		{
			id = reg.Id,
			studentName = reg.StudentName,
			shiftId = reg.ShiftId,
			checkedInAt = reg.CheckedInAt,
		});
	}

	/// <summary>
	/// EventAdmin+ in the event's OWN org, resolved per-org. Returns a 403 response to return
	/// to the caller, or null when authorized (a global super always resolves).
	/// </summary>
	private async Task<HttpResponseData?> RequireEventAdmin(HttpRequestData req, UserContext ctx, Event evt)
	{
		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, evt.OrganizationId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.EventAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "EventAdmin or higher is required in this event's organization");
		return null;
	}

	private sealed record CheckInRequest(string? OrganizationId, string? RegistrationId, bool? CheckedIn);
}
