using System.Net;
using System.Security.Cryptography;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

/// <summary>
/// Day-of check-in (#14). Two audiences:
///   • the admin running the event gets a live overview (roster) and can check people in by
///     hand, add walk-ins, and mint the QR code;
///   • a registered student scans that QR at the venue and checks THEMSELVES in.
///
/// Admin actions authorize as EventAdmin+ IN THE EVENT'S OWN ORG (Finding 9: whoever runs the
/// event clears no-shows and checks people in), resolved per-org via ResolveActorInOrgAsync so
/// a membership-based admin — who carries no admin claim on their token — is not wrongly
/// refused. Self check-in authorizes on possession of the posted code plus being registered.
///
/// blockCheckIn tags are enforced on every transition to checked-in (self and admin alike),
/// same-org only, per the locked cross-org gating decision.
/// </summary>
public class CheckInFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<CheckInFunctions> logger)
{
	/// <summary>
	/// GET /api/events/{eventId}/roster?organizationId={org}
	/// The admin's live check-in overview: non-cancelled registrations with their check-in
	/// state, plus shift definitions so the client can group "by shift" and poll for "live".
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
				crossOrg = !string.Equals(r.SchoolId, evt.OrganizationId, StringComparison.OrdinalIgnoreCase),
			})
			.ToList();

		var codeActive = !string.IsNullOrWhiteSpace(evt.CheckInCode)
			&& evt.CheckInCodeExpiresAt.HasValue && evt.CheckInCodeExpiresAt.Value > DateTime.UtcNow;

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
			checkInCodeActive = codeActive,
			checkInCodeExpiresAt = codeActive ? evt.CheckInCodeExpiresAt : null,
		});
	}

	/// <summary>
	/// POST /api/events/{eventId}/checkin  body: { organizationId, registrationId, checkedIn }
	/// Admin marks one registrant present/absent. Idempotent toggle (send desired state), so a
	/// retried request on a flaky venue connection — or the #15 offline sync — is a safe no-op.
	/// A transition to checked-in enforces blockCheckIn tags.
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
		if (wantCheckedIn && reg.CheckedInAt == null)
		{
			var (blocked, reason) = await BlockedByTagsAsync(evt, reg);
			if (blocked) return await HttpHelper.Error(req, HttpStatusCode.Conflict, reason!);
		}

		if (wantCheckedIn != (reg.CheckedInAt != null))
		{
			reg.CheckedInAt = wantCheckedIn ? DateTime.UtcNow : null;
			await cosmos.UpdateRegistrationAsync(reg);
			logger.LogInformation("[CheckIn] {RegId} on event {EventId} set checkedIn={State} by {Actor}",
				reg.Id, eventId, wantCheckedIn, ctx.UserId);
		}

		return await HttpHelper.OkJson(req, new { id = reg.Id, studentName = reg.StudentName, shiftId = reg.ShiftId, checkedInAt = reg.CheckedInAt });
	}

	/// <summary>
	/// POST /api/events/{eventId}/checkin/qr  body: { organizationId }
	/// Admin mints (or rotates) the event's posted check-in code and gets back the self
	/// check-in URL to render as a QR. A new code invalidates the previous one.
	/// </summary>
	[Function("MintCheckInCode")]
	public async Task<HttpResponseData> MintCode(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events/{eventId}/checkin/qr")] HttpRequestData req,
		string eventId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<MintRequest>(req);
		var orgId = body?.OrganizationId;
		if (string.IsNullOrWhiteSpace(orgId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "organizationId is required");

		// Mint under optimistic concurrency so a double-tap doesn't race two codes onto the doc.
		const int maxRetries = 5;
		for (var attempt = 0; attempt < maxRetries; attempt++)
		{
			var (evt, etag) = await cosmos.GetEventWithETagAsync(eventId, orgId);
			if (evt == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Event not found");

			var authFail = await RequireEventAdmin(req, ctx, evt);
			if (authFail != null) return authFail;

			var code = NewCode();
			// Valid through the event's end plus a grace window, but never less than a few hours
			// from now — covers minting the code before an event that hasn't started and events
			// whose stored end time is already past.
			var expiresAt = (evt.EndDateTime > DateTime.UtcNow ? evt.EndDateTime : DateTime.UtcNow).AddHours(6);
			evt.CheckInCode = code;
			evt.CheckInCodeExpiresAt = expiresAt;

			try
			{
				await cosmos.UpdateEventAsync(evt, etag);
				var url = $"{PublicBaseUrl(req)}/checkin.html?e={Uri.EscapeDataString(eventId)}&o={Uri.EscapeDataString(orgId)}&c={Uri.EscapeDataString(code)}";
				logger.LogInformation("[CheckIn] code minted for event {EventId} by {Actor}, expires {ExpiresAt:o}", eventId, ctx.UserId, expiresAt);
				return await HttpHelper.OkJson(req, new { code, url, expiresAt });
			}
			catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
			{
				if (attempt == maxRetries - 1)
					return await HttpHelper.Error(req, HttpStatusCode.ServiceUnavailable, "The event is being updated. Please try again.");
			}
		}
		return await HttpHelper.Error(req, HttpStatusCode.ServiceUnavailable, "Could not mint a check-in code. Please try again.");
	}

	/// <summary>
	/// POST /api/events/{eventId}/checkin/self  body: { organizationId, code }
	/// A registered student, having scanned the posted QR, checks themselves in. Gated on a
	/// valid unexpired code (proves on-site presence) AND holding a live registration.
	/// blockCheckIn tags are enforced. Idempotent.
	/// </summary>
	[Function("SelfCheckIn")]
	public async Task<HttpResponseData> SelfCheckIn(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events/{eventId}/checkin/self")] HttpRequestData req,
		string eventId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<SelfCheckInRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.OrganizationId) || string.IsNullOrWhiteSpace(body.Code))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "organizationId and code are required");

		var evt = await cosmos.GetEventAsync(eventId, body.OrganizationId);
		if (evt == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Event not found");

		// Constant-time compare on a valid, unexpired code. Fail closed if no code is minted.
		var codeOk = !string.IsNullOrWhiteSpace(evt.CheckInCode)
			&& evt.CheckInCodeExpiresAt.HasValue && evt.CheckInCodeExpiresAt.Value > DateTime.UtcNow
			&& CryptographicOperations.FixedTimeEquals(
				System.Text.Encoding.UTF8.GetBytes(body.Code), System.Text.Encoding.UTF8.GetBytes(evt.CheckInCode!));
		if (!codeOk)
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "This check-in code is not valid or has expired. Ask an event admin for the current code.");

		// The caller may only check in THEIR OWN registration. Match on the canonical memberId
		// or legacy externalId, exactly like CancelRegistration's ownership check.
		var self = await cosmos.GetUserByExternalIdAsync(ctx.UserId, ctx.TenantId);
		var reg = (await cosmos.GetRegistrationsByEventAsync(eventId))
			.FirstOrDefault(r => !string.Equals(r.Status, "Cancelled", StringComparison.OrdinalIgnoreCase)
				&& r.BelongsTo(ctx.UserId, self?.Id));
		if (reg == null)
			return await HttpHelper.Error(req, HttpStatusCode.Conflict, "You're not registered for this event, so there's nothing to check in. Ask an event admin to add you as a walk-in.");

		if (reg.CheckedInAt == null)
		{
			var (blocked, reason) = await BlockedByTagsAsync(evt, reg);
			if (blocked) return await HttpHelper.Error(req, HttpStatusCode.Conflict, reason!);

			reg.CheckedInAt = DateTime.UtcNow;
			await cosmos.UpdateRegistrationAsync(reg);
			logger.LogInformation("[CheckIn] self check-in {RegId} on event {EventId}", reg.Id, eventId);
		}

		return await HttpHelper.OkJson(req, new { checkedInAt = reg.CheckedInAt, title = evt.Title, alreadyCheckedIn = reg.CheckedInAt != null });
	}

	/// <summary>
	/// POST /api/events/{eventId}/checkin/walkin  body: { organizationId, firstName, lastName, email?, shiftId? }
	/// Admin adds someone who showed up unregistered: creates a managed volunteer in the event's
	/// org, registers them (counters move, capacity is NOT enforced — they are physically here),
	/// and checks them in. blockCheckIn is not applied to a walk-in the admin is vouching for on
	/// the spot.
	/// </summary>
	[Function("AddWalkIn")]
	public async Task<HttpResponseData> AddWalkIn(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events/{eventId}/checkin/walkin")] HttpRequestData req,
		string eventId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<WalkInRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.OrganizationId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "organizationId is required");

		var displayName = Models.User.ComposeName(body.FirstName, body.LastName, body.DisplayName);
		if (string.IsNullOrWhiteSpace(displayName))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "a name (first/last or displayName) is required");

		var evt = await cosmos.GetEventAsync(eventId, body.OrganizationId);
		if (evt == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Event not found");

		var authFail = await RequireEventAdmin(req, ctx, evt);
		if (authFail != null) return authFail;

		var shiftId = evt.Shifts.Count > 0 ? body.ShiftId : null;
		if (evt.Shifts.Count > 0 && (string.IsNullOrWhiteSpace(shiftId) || evt.Shifts.All(s => s.Id != shiftId)))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Please choose a valid shift for this walk-in");

		// Find-or-create the walk-in as a managed volunteer in the event's org. Dedupe by email
		// when one is given (email is unique per org); a nameless-only walk-in is always new.
		var email = string.IsNullOrWhiteSpace(body.Email) ? null : body.Email.Trim().ToLowerInvariant();
		Models.User? member = email == null ? null : await cosmos.GetMembershipByEmailAsync(email, evt.OrganizationId);
		if (member == null)
		{
			member = new Models.User
			{
				ExternalId = string.Empty,
				TenantId = evt.OrganizationId,
				OrganizationId = evt.OrganizationId,
				Email = email ?? string.Empty,
				FirstName = body.FirstName?.Trim(),
				LastName = body.LastName?.Trim(),
				DisplayName = displayName,
				AdminLevel = AdminLevels.Student,
				Status = "active",
				IsManaged = true,
				ManagedByUserId = ctx.UserId,
			};
			member.ProfileComplete = IntakeValidation.IsComplete(member);
			member = await cosmos.CreateManagedVolunteerAsync(member);
		}

		// Already on the roster? Just make sure they're checked in rather than double-registering.
		var existing = (await cosmos.GetRegistrationsByEventAsync(eventId))
			.FirstOrDefault(r => !string.Equals(r.Status, "Cancelled", StringComparison.OrdinalIgnoreCase)
				&& r.BelongsTo(member.ExternalId, member.Id));
		if (existing != null)
		{
			if (existing.CheckedInAt == null) { existing.CheckedInAt = DateTime.UtcNow; await cosmos.UpdateRegistrationAsync(existing); }
			return await HttpHelper.OkJson(req, new { registrationId = existing.Id, memberId = member.Id, studentName = existing.StudentName, checkedInAt = existing.CheckedInAt, alreadyRegistered = true });
		}

		var reg = await cosmos.CreateRegistrationAsync(new EventRegistration
		{
			EventId = eventId,
			UserId = member.ExternalId,
			MemberId = member.Id,
			StudentName = displayName,
			SchoolId = evt.OrganizationId,
			OrganizationId = evt.OrganizationId,
			Status = "Registered",
			ShiftId = shiftId,
			CheckedInAt = DateTime.UtcNow,
		});

		// Move counters to reflect the extra body. Capacity is deliberately not enforced — a
		// walk-in is already present, so refusing the count would misreport who is on site.
		await BumpSlotsForWalkInAsync(eventId, evt.OrganizationId, shiftId);

		logger.LogInformation("[CheckIn] walk-in {MemberId} added + checked in to event {EventId} by {Actor}", member.Id, eventId, ctx.UserId);
		return await HttpHelper.CreatedJson(req, new { registrationId = reg.Id, memberId = member.Id, studentName = displayName, checkedInAt = reg.CheckedInAt, alreadyRegistered = false });
	}

	// ── helpers ───────────────────────────────────────────────────────────────

	/// <summary>
	/// EventAdmin+ in the event's OWN org. Returns a 403 response to hand back, or null when
	/// authorized (a global super always resolves).
	/// </summary>
	private async Task<HttpResponseData?> RequireEventAdmin(HttpRequestData req, UserContext ctx, Event evt)
	{
		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, evt.OrganizationId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.EventAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "EventAdmin or higher is required in this event's organization");
		return null;
	}

	/// <summary>
	/// Does the registrant lack a tag their event's org marks blockCheckIn? Same-org only: a
	/// cross-org registrant has no User doc in the event's org, so there is nothing to check and
	/// they are never blocked (the locked cross-org decision). Returns the human message naming
	/// every missing tag when blocked.
	/// </summary>
	private async Task<(bool blocked, string? reason)> BlockedByTagsAsync(Event evt, EventRegistration reg)
	{
		// Cross-org registrant → skip (nothing to evaluate).
		if (!string.Equals(reg.SchoolId, evt.OrganizationId, StringComparison.OrdinalIgnoreCase))
			return (false, null);

		var tenant = await cosmos.GetTenantAsync(evt.OrganizationId);
		var gating = tenant?.UserTags
			.Where(t => string.Equals(t.Enforcement, TagEnforcement.BlockCheckIn, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(t.Status, "active", StringComparison.OrdinalIgnoreCase))
			.ToList() ?? [];
		if (gating.Count == 0) return (false, null);

		var memberId = reg.MemberId;
		var member = string.IsNullOrWhiteSpace(memberId) ? null : await cosmos.GetUserByIdAsync(memberId, evt.OrganizationId);
		if (member == null) return (false, null); // no doc to evaluate → don't block on a lookup miss

		var now = DateTime.UtcNow;
		var missing = gating
			.Where(t => !member.Tags.Any(s => string.Equals(s.TagId, t.Id, StringComparison.OrdinalIgnoreCase) && s.IsCurrentAt(now)))
			.Select(t => t.Label)
			.ToList();
		if (missing.Count == 0) return (false, null);

		return (true, $"Cannot check in yet — still needed: {string.Join(", ", missing)}. An event admin can record it and try again.");
	}

	/// <summary>Increment overall and (optional) shift counters for an added walk-in, retrying on a lost race. Capacity not enforced.</summary>
	private async Task BumpSlotsForWalkInAsync(string eventId, string orgId, string? shiftId)
	{
		const int maxRetries = 5;
		for (var attempt = 0; attempt < maxRetries; attempt++)
		{
			var (fresh, etag) = await cosmos.GetEventWithETagAsync(eventId, orgId);
			if (fresh == null) return;

			fresh.CurrentSlots++;
			if (!string.IsNullOrWhiteSpace(shiftId))
			{
				var shift = fresh.Shifts.FirstOrDefault(s => s.Id == shiftId);
				if (shift != null) shift.Filled++;
			}
			if (fresh.MaxSlots > 0 && fresh.CurrentSlots >= fresh.MaxSlots) fresh.Status = "Full";

			try { await cosmos.UpdateEventAsync(fresh, etag); return; }
			catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
			{
				if (attempt == maxRetries - 1)
					logger.LogWarning("Failed to bump slot count for walk-in on event {EventId} after {Retries} retries", eventId, maxRetries);
			}
		}
	}

	/// <summary>A short, URL-safe, unguessable code (~26 base32 chars of entropy is overkill; 10 is plenty for a posted, expiring code).</summary>
	private static string NewCode()
	{
		const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"; // no 0/O/1/I/L — legible if typed by hand
		Span<byte> bytes = stackalloc byte[10];
		RandomNumberGenerator.Fill(bytes);
		return string.Create(bytes.Length, bytes.ToArray(), (span, b) =>
		{
			for (var i = 0; i < span.Length; i++) span[i] = alphabet[b[i] % alphabet.Length];
		});
	}

	/// <summary>The public origin to build the self check-in link from, honoring the proxy's forwarded host (Cloudflare/SWA).</summary>
	private static string PublicBaseUrl(HttpRequestData req)
	{
		string? Header(string n) => req.Headers.TryGetValues(n, out var v) ? v.FirstOrDefault() : null;
		var proto = Header("X-Forwarded-Proto") ?? "https";
		var host = Header("X-Forwarded-Host") ?? Header("Host") ?? req.Url.Host;
		return $"{proto}://{host}";
	}

	private sealed record CheckInRequest(string? OrganizationId, string? RegistrationId, bool? CheckedIn);
	private sealed record MintRequest(string? OrganizationId);
	private sealed record SelfCheckInRequest(string? OrganizationId, string? Code);
	private sealed record WalkInRequest(string? OrganizationId, string? FirstName, string? LastName, string? DisplayName, string? Email, string? ShiftId);
}
