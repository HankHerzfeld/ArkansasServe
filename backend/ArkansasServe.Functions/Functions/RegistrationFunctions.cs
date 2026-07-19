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

		// The canonical identity of this registration. Resolved from the caller's per-org User
		// doc rather than taken from the token, because a registrant need not have an account
		// at all (see EventRegistration.MemberId). Null only if this person somehow has no
		// User doc in their own tenant, in which case reads fall back to UserId as before.
		var self = await cosmos.GetUserByExternalIdAsync(ctx.UserId, ctx.TenantId);

		if (await cosmos.IsAlreadyRegisteredAsync(body.EventId, ctx.UserId, self?.Id))
			return await HttpHelper.Error(req, HttpStatusCode.Conflict, "Already registered for this event");

		// #11②: same-org blockRegistration gate. A registrant in the event's OWN org must hold
		// every tag that org marks blockRegistration; a cross-org registrant has no doc here to
		// carry a tag state, so is never blocked (the locked cross-org decision).
		if (self != null && string.Equals(ctx.TenantId, evt.OrganizationId, StringComparison.OrdinalIgnoreCase))
		{
			var org = await cosmos.GetTenantAsync(evt.OrganizationId);
			var missing = TagGate.MissingTags(org, self, TagEnforcement.BlockRegistration, DateTime.UtcNow);
			if (missing.Count > 0)
				return await HttpHelper.Error(req, HttpStatusCode.Conflict,
					$"Can't sign up yet — still needed: {string.Join(", ", missing)}. Ask an admin to record it, then try again.");
		}

		// If the event has shifts, the volunteer must choose one that isn't full.
		if (evt.Shifts.Count > 0)
		{
			if (string.IsNullOrWhiteSpace(body.ShiftId))
				return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Please choose a shift");
			var shift = evt.Shifts.FirstOrDefault(s => s.Id == body.ShiftId);
			if (shift == null)
				return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "That shift no longer exists");
			if (shift.Capacity > 0 && shift.Filled >= shift.Capacity)
				return await HttpHelper.Error(req, HttpStatusCode.Conflict, "That shift is full");
		}

		// Required custom questions must be answered.
		foreach (var q in evt.SignupQuestions.Where(q => q.Required))
		{
			var a = body.Answers?.FirstOrDefault(x => x.QuestionId == q.Id);
			if (a == null || string.IsNullOrWhiteSpace(a.Answer))
				return await HttpHelper.Error(req, HttpStatusCode.BadRequest, $"Please answer: {q.Label}");
		}

		// Keep only answers to real questions, stamped with the question text.
		var answers = new List<RegistrationAnswer>();
		foreach (var q in evt.SignupQuestions)
		{
			var a = body.Answers?.FirstOrDefault(x => x.QuestionId == q.Id);
			if (a != null && !string.IsNullOrWhiteSpace(a.Answer))
				answers.Add(new RegistrationAnswer { QuestionId = q.Id, Question = q.Label, Answer = a.Answer.Trim() });
		}

		var reg = new EventRegistration
		{
			EventId = body.EventId,
			UserId = ctx.UserId,
			MemberId = self?.Id,
			StudentName = ctx.DisplayName,
			SchoolId = ctx.TenantId,
			// The event's authoritative org (its partition key) — may differ from SchoolId
			// on a cross-org sign-up; cancel needs it to find the event.
			OrganizationId = evt.OrganizationId,
			Status = "Registered",
			ShiftId = evt.Shifts.Count > 0 ? body.ShiftId : null,
			Answers = answers,
		};

		var created = await cosmos.CreateRegistrationAsync(reg);

		const int maxRetries = 5;
		var slotUpdateSucceeded = false;
		for (var attempt = 0; attempt < maxRetries; attempt++)
		{
			var (freshEvt, etag) = await cosmos.GetEventWithETagAsync(body.EventId, evt.OrganizationId);
			if (freshEvt == null) break;

			if (freshEvt.MaxSlots > 0 && freshEvt.CurrentSlots >= freshEvt.MaxSlots)
			{
				reg.Status = "Cancelled";
				await cosmos.UpdateRegistrationAsync(reg);
				return await HttpHelper.Error(req, HttpStatusCode.Conflict, "Event is full");
			}

			// Per-shift capacity, checked/incremented atomically with the event doc.
			if (!string.IsNullOrWhiteSpace(reg.ShiftId))
			{
				var fShift = freshEvt.Shifts.FirstOrDefault(s => s.Id == reg.ShiftId);
				if (fShift != null && fShift.Capacity > 0 && fShift.Filled >= fShift.Capacity)
				{
					reg.Status = "Cancelled";
					await cosmos.UpdateRegistrationAsync(reg);
					return await HttpHelper.Error(req, HttpStatusCode.Conflict, "That shift is full");
				}
				if (fShift != null) fShift.Filled++;
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

		// Authorize per-org, never by the token level (Finding 9). You may always cancel your
		// own registration; cancelling someone else's needs EventAdmin+ IN THE EVENT'S OWN ORG
		// (a global super clears this everywhere).
		//
		// EventAdmin — not OrganizationAdmin — because whoever runs an event is the one who
		// clears no-shows on the day (decided 2026-07-14). This matches CreateEvent's level;
		// it is deliberately lower than delete-event/void-log, which are destructive and stay
		// at OrganizationAdmin+.
		//
		// The previous check keyed on ctx.IsStudentLevel, which was wrong in both directions:
		//   • a membership-based OrganizationAdmin carries no admin claim on their token, so
		//     they read as Student and were refused on their own member's registration;
		//   • a token-level admin from an UNRELATED org skipped the check entirely and could
		//     cancel any registration in any org.
		// Identity comes from BelongsTo, which accepts the canonical memberId or the legacy
		// externalId — so this keeps working on rows written before memberId existed, and will
		// keep working for a registrant who has no account at all.
		var self = await cosmos.GetUserByExternalIdAsync(ctx.UserId, ctx.TenantId);
		if (!reg.BelongsTo(ctx.UserId, self?.Id))
		{
			// The event's own org, with the same legacy SchoolId fallback used below to
			// locate the event itself.
			var regOrgId = string.IsNullOrWhiteSpace(reg.OrganizationId) ? reg.SchoolId : reg.OrganizationId;
			var actor = string.IsNullOrWhiteSpace(regOrgId)
				? null
				: await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, regOrgId);
			if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.EventAdmin))
				return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Cannot cancel another user's registration");
		}

		if (reg.Status == "Cancelled")
			return await HttpHelper.Error(req, HttpStatusCode.Conflict, "Registration is already cancelled");

		reg.Status = "Cancelled";
		await cosmos.UpdateRegistrationAsync(reg);

		const int maxRetries = 5;
		for (var attempt = 0; attempt < maxRetries; attempt++)
		{
			// Locate the event by its OWN org (partition key). Fall back to SchoolId for
			// pre-existing registrations saved before OrganizationId was recorded.
			var (freshEvt, etag) = await cosmos.GetEventWithETagAsync(reg.EventId, reg.OrganizationId ?? reg.SchoolId);
			if (freshEvt == null) break;

			if (freshEvt.CurrentSlots > 0) freshEvt.CurrentSlots--;
			if (!string.IsNullOrWhiteSpace(reg.ShiftId))
			{
				var fShift = freshEvt.Shifts.FirstOrDefault(s => s.Id == reg.ShiftId);
				if (fShift != null && fShift.Filled > 0) fShift.Filled--;
			}
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

	// ── POST /api/registrations/group ─────────────────────────────────────────

	/// <summary>
	/// Registers several people from one organization's roster for an event in a single
	/// action. All-or-nothing: if the group does not fit, or any registrant is invalid,
	/// nothing is written.
	///
	/// Authorization is on the org the REGISTRANTS belong to, not the event's org, and every
	/// registrant must belong to that same org. That is what "an admin signs up their own
	/// roster" means, and it is the case that matters: a school's EventAdmin signing students
	/// up for a community org's event holds no role in that community org. It grants nothing
	/// new — each of those people could self-register individually; this only does it in bulk.
	///
	/// Registrants are addressed by MemberId (their per-org User doc id), so a managed
	/// volunteer who has never signed in can be registered. See EventRegistration.MemberId.
	/// </summary>
	[Function("CreateGroupRegistration")]
	public async Task<HttpResponseData> RegisterGroup(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "registrations/group")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<GroupRegistrationRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.EventId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "eventId is required");
		if (string.IsNullOrWhiteSpace(body.RegistrantOrganizationId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "registrantOrganizationId is required");

		var requested = (body.Registrants ?? [])
			.Where(r => !string.IsNullOrWhiteSpace(r.MemberId))
			.ToList();
		if (requested.Count == 0)
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "At least one person is required");

		var duplicated = requested
			.GroupBy(r => r.MemberId!, StringComparer.OrdinalIgnoreCase)
			.Where(g => g.Count() > 1).Select(g => g.Key).ToList();
		if (duplicated.Count > 0)
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "The same person appears more than once in this group");

		// EventAdmin+ in the registrants' own org — the level Finding 9 settled on for acting
		// on someone else's registration.
		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, body.RegistrantOrganizationId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.EventAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "EventAdmin or higher is required in that organization");

		var evt = await cosmos.GetEventAsync(body.EventId, body.OrganizationId ?? string.Empty);
		if (evt == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Event not found");
		if (evt.Status != "Open")
			return await HttpHelper.Error(req, HttpStatusCode.Conflict, "Event is not open for registration");

		if (evt.Shifts.Count > 0)
		{
			if (string.IsNullOrWhiteSpace(body.ShiftId))
				return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Please choose a shift");
			if (evt.Shifts.All(s => s.Id != body.ShiftId))
				return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "That shift no longer exists");
		}
		var shiftId = evt.Shifts.Count > 0 ? body.ShiftId : null;

		// Resolve every registrant before writing anything.
		var members = new List<(GroupRegistrant Input, Models.User User)>();
		foreach (var r in requested)
		{
			var u = await cosmos.GetUserByIdAsync(r.MemberId!, body.RegistrantOrganizationId);
			if (u == null)
				return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "One of the selected people is not a member of that organization");
			if (!string.Equals(u.Status, "active", StringComparison.OrdinalIgnoreCase))
				return await HttpHelper.Error(req, HttpStatusCode.BadRequest, $"{DisplayNameOf(u)} is not an active member");
			members.Add((r, u));
		}

		// #11②: same-org blockRegistration gate. Only registrants IN THE EVENT'S OWN org are
		// gated (a cross-org group has no docs here to carry tag state). All-or-nothing, like the
		// rest of this endpoint: if anyone is short a required tag, nobody is registered.
		if (string.Equals(body.RegistrantOrganizationId, evt.OrganizationId, StringComparison.OrdinalIgnoreCase))
		{
			var gateOrg = await cosmos.GetTenantAsync(evt.OrganizationId);
			var now = DateTime.UtcNow;
			var blocked = new List<string>();
			foreach (var (_, user) in members)
			{
				var missing = TagGate.MissingTags(gateOrg, user, TagEnforcement.BlockRegistration, now);
				if (missing.Count > 0) blocked.Add($"{DisplayNameOf(user)} ({string.Join(", ", missing)})");
			}
			if (blocked.Count > 0)
				return await HttpHelper.Error(req, HttpStatusCode.Conflict,
					$"Can't sign up — still needed: {string.Join("; ", blocked)}. Record the tag(s), then try again.");
		}

		// Nobody in the group may already hold a live registration. One read of the event's
		// registrations serves the whole group rather than a per-person query.
		var existing = await cosmos.GetRegistrationsByEventAsync(body.EventId);
		var alreadyIn = members
			.Where(m => existing.Any(e =>
				!string.Equals(e.Status, "Cancelled", StringComparison.OrdinalIgnoreCase)
				&& e.BelongsTo(m.User.ExternalId, m.User.Id)))
			.Select(m => DisplayNameOf(m.User))
			.ToList();
		if (alreadyIn.Count > 0)
			return await HttpHelper.Error(req, HttpStatusCode.Conflict,
				$"Already registered for this event: {string.Join(", ", alreadyIn)}");

		// Required questions are answered PER PERSON — an admin cannot truthfully answer for
		// somebody else with one shared value, so the group form collects each person's own.
		var answersByMember = new Dictionary<string, List<RegistrationAnswer>>(StringComparer.OrdinalIgnoreCase);
		foreach (var (input, user) in members)
		{
			foreach (var q in evt.SignupQuestions.Where(q => q.Required))
			{
				var a = input.Answers?.FirstOrDefault(x => x.QuestionId == q.Id);
				if (a == null || string.IsNullOrWhiteSpace(a.Answer))
					return await HttpHelper.Error(req, HttpStatusCode.BadRequest,
						$"{DisplayNameOf(user)} still needs an answer for: {q.Label}");
			}

			var kept = new List<RegistrationAnswer>();
			foreach (var q in evt.SignupQuestions)
			{
				var a = input.Answers?.FirstOrDefault(x => x.QuestionId == q.Id);
				if (a != null && !string.IsNullOrWhiteSpace(a.Answer))
					kept.Add(new RegistrationAnswer { QuestionId = q.Id, Question = q.Label, Answer = a.Answer.Trim() });
			}
			answersByMember[user.Id] = kept;
		}

		// Reserve the whole group's capacity FIRST, in one update, then write the documents.
		// The single-registration path above still does the reverse — writes the doc, then
		// tries to take the slot, and marks the doc Cancelled if it cannot — which is why a
		// full event accumulates cancelled rows. At N people that would mean N wasted docs and
		// N compensating writes, so the group path reserves up front instead.
		var (reserved, reserveError) = await AdjustSlotsAsync(
			body.EventId, evt.OrganizationId, shiftId, members.Count, enforceCapacity: true);
		if (!reserved)
			return await HttpHelper.Error(req, HttpStatusCode.Conflict, reserveError ?? "Could not reserve spots for this group");

		var created = new List<EventRegistration>();
		try
		{
			foreach (var (_, user) in members)
			{
				created.Add(await cosmos.CreateRegistrationAsync(new EventRegistration
				{
					EventId = body.EventId,
					UserId = user.ExternalId,
					MemberId = user.Id,
					StudentName = DisplayNameOf(user),
					SchoolId = body.RegistrantOrganizationId,
					OrganizationId = evt.OrganizationId,
					Status = "Registered",
					ShiftId = shiftId,
					Answers = answersByMember[user.Id],
				}));
			}
		}
		catch (Exception ex)
		{
			// Give the reservation back and remove whatever landed, so a half-written group
			// does not hold spots nobody occupies. Best-effort: if this fails the event is
			// over-counted, which wrongly turns people away but never over-books — the safe
			// direction to fail in.
			logger.LogError(ex, "[GroupRegistration] Failed writing group for event {EventId}; rolling back {Count} reserved spot(s)", body.EventId, members.Count);
			foreach (var c in created)
			{
				try { await cosmos.DeleteRegistrationAsync(c.Id, c.EventId); }
				catch (Exception cleanupEx) { logger.LogError(cleanupEx, "[GroupRegistration] Could not remove partial registration {RegId}", c.Id); }
			}
			await AdjustSlotsAsync(body.EventId, evt.OrganizationId, shiftId, -members.Count, enforceCapacity: false);
			return await HttpHelper.Error(req, HttpStatusCode.InternalServerError, "Could not complete the group registration. No one was signed up.");
		}

		logger.LogInformation("[GroupRegistration] {Count} registered for event {EventId} by {Actor}", created.Count, body.EventId, ctx.UserId);
		return await HttpHelper.CreatedJson(req, new { registered = created.Count, registrations = created });
	}

	private static string DisplayNameOf(Models.User u) =>
		!string.IsNullOrWhiteSpace(u.DisplayName) ? u.DisplayName!
		: string.Join(' ', new[] { u.FirstName, u.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } n ? n
		: !string.IsNullOrWhiteSpace(u.Email) ? u.Email!
		: "This member";

	/// <summary>
	/// Moves the event's slot (and shift) counters by <paramref name="delta"/> under optimistic
	/// concurrency, retrying on a lost race.
	///
	/// With <paramref name="enforceCapacity"/> the whole delta must fit or nothing moves — an
	/// all-or-nothing reservation, so a group of 8 never half-fills a 5-spot event. Releasing a
	/// reservation passes false: a rollback must not be refused by the very capacity it is
	/// giving back.
	/// </summary>
	private async Task<(bool ok, string? error)> AdjustSlotsAsync(
		string eventId, string orgId, string? shiftId, int delta, bool enforceCapacity)
	{
		const int maxRetries = 5;
		for (var attempt = 0; attempt < maxRetries; attempt++)
		{
			var (fresh, etag) = await cosmos.GetEventWithETagAsync(eventId, orgId);
			if (fresh == null) return (false, "Event not found");

			if (enforceCapacity && fresh.MaxSlots > 0 && fresh.CurrentSlots + delta > fresh.MaxSlots)
			{
				var left = Math.Max(0, fresh.MaxSlots - fresh.CurrentSlots);
				return (false, left == 0
					? "This event is full."
					: $"Only {left} spot{(left == 1 ? "" : "s")} left — {delta} {(delta == 1 ? "person needs" : "people need")} a place. Nobody was signed up.");
			}

			EventShift? shift = null;
			if (!string.IsNullOrWhiteSpace(shiftId))
			{
				shift = fresh.Shifts.FirstOrDefault(s => s.Id == shiftId);
				if (shift == null) return (false, "That shift no longer exists");
				if (enforceCapacity && shift.Capacity > 0 && shift.Filled + delta > shift.Capacity)
				{
					var left = Math.Max(0, shift.Capacity - shift.Filled);
					return (false, left == 0
						? "That shift is full."
						: $"That shift has only {left} spot{(left == 1 ? "" : "s")} left — {delta} {(delta == 1 ? "person needs" : "people need")} a place. Nobody was signed up.");
				}
				shift.Filled = Math.Max(0, shift.Filled + delta);
			}

			fresh.CurrentSlots = Math.Max(0, fresh.CurrentSlots + delta);
			// Keep Status in step with the count in both directions, so releasing the last
			// reservation reopens an event that a rolled-back group had just closed.
			if (fresh.MaxSlots > 0)
				fresh.Status = fresh.CurrentSlots >= fresh.MaxSlots ? "Full"
					: string.Equals(fresh.Status, "Full", StringComparison.OrdinalIgnoreCase) ? "Open"
					: fresh.Status;

			try
			{
				await cosmos.UpdateEventAsync(fresh, etag);
				return (true, null);
			}
			catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
			{
				if (attempt == maxRetries - 1)
					logger.LogWarning("Failed to move slot count by {Delta} for event {EventId} after {Retries} retries", delta, eventId, maxRetries);
			}
		}
		return (false, "This event is being updated by someone else. Please try again.");
	}

	private sealed record RegistrationRequest(string EventId, string? OrganizationId, string? ShiftId, List<RegistrationAnswer>? Answers);

	private sealed record GroupRegistrationRequest(
		string EventId,
		string? OrganizationId,
		string? ShiftId,
		string? RegistrantOrganizationId,
		List<GroupRegistrant>? Registrants);

	private sealed record GroupRegistrant(string? MemberId, List<RegistrationAnswer>? Answers);
}
