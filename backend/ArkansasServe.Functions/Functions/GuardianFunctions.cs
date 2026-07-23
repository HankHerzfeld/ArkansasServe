using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

/// <summary>
/// Guardian records and their one-time access links (#20).
///
/// Two audiences, deliberately split:
///   - ORG ADMINS attach a guardian to a minor and ask for a link to be issued. JWT-authorized,
///     EventAdmin+ in the minor's own organization.
///   - THE GUARDIAN THEMSELVES redeems that link. ANONYMOUS by necessity — a guardian has no
///     account, which is the whole design. The token is the credential.
///
/// This PR does not SEND anything. Issue returns the URL to the caller so the flow can be
/// exercised end to end before email delivery (ACS) is wired up, and so that a failure in
/// delivery can never be confused with a failure in the link itself.
/// </summary>
public class GuardianFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<GuardianFunctions> logger)
{
	/// <summary>
	/// POST /api/manage/guardians/link
	/// body: { organizationId, minorUserId, email, name?, phone?, reason? }
	///
	/// Attaches a guardian to a minor and issues a fresh access link. Idempotent on the guardian:
	/// an existing record for that email gains a link rather than being duplicated, which is what
	/// makes one parent with children at two schools a single inbox.
	///
	/// Authorization is on the MINOR'S organization — the people who know the family, not the
	/// org hosting some event the child might attend.
	/// </summary>
	[Function("LinkGuardian")]
	public async Task<HttpResponseData> LinkGuardian(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/guardians/link")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<LinkGuardianRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.OrganizationId) || string.IsNullOrWhiteSpace(body.MinorUserId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "organizationId and minorUserId are required");
		if (string.IsNullOrWhiteSpace(body.Email) || !body.Email.Contains('@'))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "A valid guardian email is required");

		// Per-org, never the token level (Finding 9): a membership-based admin carries no admin
		// claim, so trusting the token would refuse exactly the people who run the roster.
		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, body.OrganizationId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.EventAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "EventAdmin or higher is required in this organization");

		var minor = await cosmos.GetUserByIdAsync(body.MinorUserId, body.OrganizationId);
		if (minor == null)
			return await HttpHelper.Error(req, HttpStatusCode.NotFound, "That member was not found in this organization");

		var email = body.Email.Trim().ToLowerInvariant();
		var guardian = await cosmos.GetGuardianByEmailAsync(email) ?? new Guardian { Email = email };

		// A guardian linked to a demo minor, or created by a demo persona (impersonation is
		// demo-only), is a demo guardian. Never un-set an already-demo flag.
		guardian.IsDemo = guardian.IsDemo || minor.IsDemoUser || ctx.IsImpersonating;

		// Fill only what is missing, so an org adding a second child cannot overwrite the name
		// or phone the guardian already gave another org.
		if (string.IsNullOrWhiteSpace(guardian.Name)  && !string.IsNullOrWhiteSpace(body.Name))  guardian.Name = body.Name!.Trim();
		if (string.IsNullOrWhiteSpace(guardian.Phone) && !string.IsNullOrWhiteSpace(body.Phone)) guardian.Phone = body.Phone!.Trim();

		var existing = guardian.Links.FirstOrDefault(l =>
			string.Equals(l.MinorUserId, body.MinorUserId, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(l.OrganizationId, body.OrganizationId, StringComparison.OrdinalIgnoreCase));
		if (existing == null)
		{
			guardian.Links.Add(new GuardianLink
			{
				MinorUserId = body.MinorUserId,
				OrganizationId = body.OrganizationId,
				MinorName = DisplayNameOf(minor),
			});
		}
		else
		{
			// Re-asserting the link refreshes the denormalised name rather than duplicating it.
			existing.MinorName = DisplayNameOf(minor);
		}

		var token = GuardianLinkService.Issue(guardian, body.Reason, DateTime.UtcNow);
		await cosmos.UpsertGuardianAsync(guardian);

		logger.LogInformation(
			"[Guardian] {Actor} linked guardian {GuardianId} to minor {MinorId} in {OrgId} and issued a link",
			ctx.UserId, guardian.Id, body.MinorUserId, body.OrganizationId);

		// The RAW TOKEN is returned exactly once and never persisted. The client builds the full
		// URL from its OWN origin: /api/* reaches the Function App through the SWA linked-backend
		// proxy, so the request Host here is *.azurewebsites.net, not the public site.
		return await HttpHelper.OkJson(req, new
		{
			guardianId = guardian.Id,
			email = guardian.Email,
			token,
			expiresAt = guardian.MagicLink!.ExpiresAt,
			path = $"/guardian.html?token={Uri.EscapeDataString(token)}",
		});
	}

	/// <summary>
	/// POST /api/guardian/redeem   body: { token }
	///
	/// ANONYMOUS by design — the token is the credential, because a guardian has no account.
	/// Single-use: success consumes the link, so a forwarded or cached URL is dead on arrival.
	///
	/// Returns everything the guardian's page needs in one call, since they cannot come back for
	/// more without another link.
	/// </summary>
	[Function("RedeemGuardianLink")]
	public async Task<HttpResponseData> RedeemGuardianLink(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "guardian/redeem")] HttpRequestData req)
	{
		var body = await HttpHelper.ReadBody<RedeemRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.Token))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "token is required");

		var hash = GuardianLinkService.HashToken(body.Token.Trim());
		var guardian = await cosmos.GetGuardianByTokenHashAsync(hash);

		var outcome = GuardianLinkService.Redeem(guardian, DateTime.UtcNow);
		if (outcome != GuardianLinkService.RedeemResult.Ok)
		{
			// A token that matched nothing is reported the same way as one that is spent or
			// stale, and always with the same 401 — the differences below are only ever reached
			// for a token that genuinely matched a record, so they leak nothing to someone
			// guessing. Every one of them ends at "ask for a new link", which is the only
			// action available.
			var reason = outcome switch
			{
				GuardianLinkService.RedeemResult.Expired     => "This link has expired.",
				GuardianLinkService.RedeemResult.AlreadyUsed => "This link has already been used.",
				_                                            => "This link is not valid.",
			};
			logger.LogInformation("[Guardian] link redemption refused: {Outcome}", outcome);
			return await HttpHelper.Error(req, HttpStatusCode.Unauthorized, $"{reason} Please ask the organization for a new one.");
		}

		// The link is now spent, so mint the short working session that lets them actually
		// submit a decision — see GuardianSessionState for why this is not optional.
		var sessionToken = GuardianLinkService.IssueSession(guardian!, DateTime.UtcNow);
		await cosmos.UpsertGuardianAsync(guardian!);
		logger.LogInformation("[Guardian] {GuardianId} redeemed a link", guardian!.Id);

		return await HttpHelper.OkJson(req, new
		{
			sessionToken,
			sessionExpiresAt = guardian.Session!.ExpiresAt,
			guardianId = guardian.Id,
			name = guardian.Name,
			email = guardian.Email,
			reason = guardian.MagicLink?.Reason,
			children = await ChildrenViewAsync(guardian),
		});
	}

	/// <summary>
	/// POST /api/guardian/consent
	/// body: { sessionToken, minorUserId, organizationId, action: "grant" | "revoke" }
	///
	/// ANONYMOUS, authorised by the short session minted at redemption — a guardian has no
	/// account. The session is checked against the links the guardian actually holds, so a valid
	/// session can only ever act on that guardian's own children in the orgs they are linked to.
	///
	/// REVOKING IS NOT JUST A FLAG. Withdrawal cancels the minor's FUTURE registrations in that
	/// organization and notifies the org, because a consent switch that changed nothing on the
	/// ground would be the same kind of lie as an enforcement setting that does not enforce.
	/// Past and in-progress registrations are left alone: they already happened.
	/// </summary>
	[Function("SetGuardianConsent")]
	public async Task<HttpResponseData> SetGuardianConsent(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "guardian/consent")] HttpRequestData req)
	{
		var body = await HttpHelper.ReadBody<ConsentRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.SessionToken))
			return await HttpHelper.Error(req, HttpStatusCode.Unauthorized, "Your session has ended. Please use a fresh link.");
		if (string.IsNullOrWhiteSpace(body.MinorUserId) || string.IsNullOrWhiteSpace(body.OrganizationId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "minorUserId and organizationId are required");
		if (!string.Equals(body.Action, "grant", StringComparison.OrdinalIgnoreCase)
			&& !string.Equals(body.Action, "revoke", StringComparison.OrdinalIgnoreCase))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "action must be 'grant' or 'revoke'");

		var now = DateTime.UtcNow;
		var guardian = await cosmos.GetGuardianBySessionHashAsync(GuardianLinkService.HashToken(body.SessionToken.Trim()));
		if (guardian?.Session == null || !guardian.Session.IsLive(now))
			return await HttpHelper.Error(req, HttpStatusCode.Unauthorized, "Your session has ended. Please use a fresh link.");

		// The session proves WHO; the links prove WHAT they may act on. Without this a valid
		// session could set consent for any child in any organization.
		var link = guardian.Links.FirstOrDefault(l =>
			string.Equals(l.MinorUserId, body.MinorUserId, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(l.OrganizationId, body.OrganizationId, StringComparison.OrdinalIgnoreCase));
		if (link == null)
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "You are not listed as a guardian for that person.");

		var granting = string.Equals(body.Action, "grant", StringComparison.OrdinalIgnoreCase);

		// ── Per-event approval (#20 carve-out) ──────────────────────────────────────
		// With an eventId this records the FRESH approval an org-flagged or overnight event
		// needs, and deliberately leaves standing consent alone: approving one trip is not
		// broadening the family's general consent, and revoking one trip is not withdrawing it.
		// Authorized by the same session + link check above, so it cannot reach another child.
		if (!string.IsNullOrWhiteSpace(body.EventId))
		{
			var eventId = body.EventId!.Trim();
			var approval = guardian.EventApprovals.FirstOrDefault(a =>
				string.Equals(a.MinorUserId, body.MinorUserId, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(a.OrganizationId, body.OrganizationId, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(a.EventId, eventId, StringComparison.OrdinalIgnoreCase));
			if (approval == null)
			{
				approval = new GuardianEventApproval
				{
					MinorUserId = body.MinorUserId,
					OrganizationId = body.OrganizationId,
					EventId = eventId,
				};
				guardian.EventApprovals.Add(approval);
			}

			if (granting)
			{
				approval.Status = GuardianConsentStatus.Granted;
				approval.ApprovedAt = now;
				approval.RevokedAt = null;
				approval.DocumentVersion = PolicyVersions.Current;
				approval.AttestedFromIp = ClientIpOf(req);
			}
			else
			{
				approval.Status = GuardianConsentStatus.Revoked;
				approval.RevokedAt = now;
			}

			await cosmos.UpsertGuardianAsync(guardian);
			logger.LogInformation("[Guardian] per-event approval {Action} for minor {MinorId} on event {EventId} in org {OrgId}",
				granting ? "granted" : "revoked", body.MinorUserId, eventId, body.OrganizationId);

			return await HttpHelper.OkJson(req, new
			{
				scope = "event",
				eventId,
				status = approval.Status,
				approvedAt = approval.ApprovedAt,
				revokedAt = approval.RevokedAt,
			});
		}

		var consent = guardian.Consents.FirstOrDefault(c =>
			string.Equals(c.MinorUserId, body.MinorUserId, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(c.OrganizationId, body.OrganizationId, StringComparison.OrdinalIgnoreCase));
		if (consent == null)
		{
			consent = new GuardianConsent { MinorUserId = body.MinorUserId, OrganizationId = body.OrganizationId };
			guardian.Consents.Add(consent);
		}

		if (granting)
		{
			consent.Status = GuardianConsentStatus.Granted;
			consent.GrantedAt = now;
			consent.RevokedAt = null;
			consent.DocumentVersion = PolicyVersions.Current;
			// Evidence is taken from the connection, never from the body — a client-supplied
			// address is not evidence of anything.
			consent.AttestedFromIp = ClientIpOf(req);
		}
		else
		{
			consent.Status = GuardianConsentStatus.Revoked;
			consent.RevokedAt = now;
		}

		await cosmos.UpsertGuardianAsync(guardian);

		var cancelled = 0;
		if (!granting)
			cancelled = await CancelFutureRegistrationsAsync(body.MinorUserId, body.OrganizationId, link.MinorName, guardian, now);

		logger.LogInformation(
			"[Guardian] {GuardianId} set consent {Action} for {MinorId} in {OrgId}; {Cancelled} future registration(s) cancelled",
			guardian.Id, body.Action, body.MinorUserId, body.OrganizationId, cancelled);

		return await HttpHelper.OkJson(req, new
		{
			status = consent.Status,
			grantedAt = consent.GrantedAt,
			revokedAt = consent.RevokedAt,
			active = consent.IsActive(),
			registrationsCancelled = cancelled,
		});
	}

	/// <summary>
	/// Cancels the minor's future registrations in one organization and tells the people who
	/// need to know. Returns how many were cancelled.
	///
	/// Slot counters are put right by RECOMPUTING each affected event from its registrations
	/// rather than decrementing by hand — hand-decrementing is exactly the path whose failure
	/// modes produced the drift fixed in PR #112, and a bulk cancel would multiply it.
	/// </summary>
	private async Task<int> CancelFutureRegistrationsAsync(
		string minorUserId, string organizationId, string? minorName, Guardian guardian, DateTime now)
	{
		var regs = await cosmos.GetActiveRegistrationsByMemberAsync(minorUserId);
		var affectedEvents = new Dictionary<string, string>();   // eventId -> orgId
		var cancelled = 0;

		foreach (var reg in regs)
		{
			var orgId = reg.OrganizationId ?? reg.SchoolId;
			// Consent is per-organization, so a withdrawal for one org must not touch a
			// sign-up made through another.
			if (!string.Equals(orgId, organizationId, StringComparison.OrdinalIgnoreCase)) continue;

			var evt = await cosmos.GetEventAsync(reg.EventId, orgId);
			// Already-attended events are left alone: they happened, and rewriting the past
			// would also strip hours the volunteer earned.
			if (evt == null || evt.StartDateTime <= now) continue;

			reg.Status = "Cancelled";
			await cosmos.UpdateRegistrationAsync(reg);
			affectedEvents[reg.EventId] = orgId!;
			cancelled++;

			await NotifyOrgAdminsAsync(orgId!, evt.Title, minorName ?? "A volunteer", guardian);
		}

		foreach (var (eventId, orgId) in affectedEvents)
		{
			try { await cosmos.ReconcileEventSlotsAsync(eventId, orgId); }
			catch (Exception ex)
			{
				// A failed recount leaves the event OVER-counted, which turns people away
				// rather than over-booking — the safe direction — and reconcile-slots repairs it.
				logger.LogError(ex, "[Guardian] slot recount failed for event {EventId} after consent withdrawal", eventId);
			}
		}

		return cancelled;
	}

	private async Task NotifyOrgAdminsAsync(string orgId, string eventTitle, string minorName, Guardian guardian)
	{
		try
		{
			var members = await cosmos.GetUsersByTenantAsync(orgId);
			var admins = members.Where(m => AdminLevels.AtLeast(m.AdminLevel, AdminLevels.EventAdmin));
			foreach (var admin in admins)
			{
				await cosmos.CreateNotificationAsync(new Notification
				{
					UserId = admin.Id,
					Type = "guardian_consent_withdrawn",
					Message = $"{minorName}'s guardian withdrew consent — their registration for \"{eventTitle}\" was cancelled.",
				});
			}
		}
		catch (Exception ex)
		{
			// Never fail the withdrawal because a notice could not be delivered: the consent
			// decision is the thing that must stick.
			logger.LogError(ex, "[Guardian] could not notify admins in {OrgId} of a consent withdrawal", orgId);
		}
	}

	/// <summary>
	/// The client address from X-Forwarded-For, port stripped.
	///
	/// ⚠️ IPv6 IS WHY THIS IS NOT A ONE-LINER. Azure appends :port, so the obvious
	/// `split(':')[0]` works for `1.2.3.4:443` and DESTROYS `2600:1700:…:443`, which it
	/// truncates to "2600". Observed in prod on the first real consent record — the evidence
	/// this field exists to preserve was reduced to a single hextet, and it would have looked
	/// like a plausible value forever.
	///
	/// A colon count distinguishes the cases: IPv4-with-port has exactly one, bare IPv6 has
	/// several, and a bracketed IPv6 carries its own delimiters.
	/// </summary>
	private static string? ClientIpOf(HttpRequestData req)
	{
		if (!req.Headers.TryGetValues("X-Forwarded-For", out var xff)) return null;

		// Proxies chain left-to-right; the first entry is the original client.
		var first = xff.FirstOrDefault()?.Split(',').FirstOrDefault()?.Trim();
		if (string.IsNullOrWhiteSpace(first)) return null;

		// Bracketed IPv6: "[2600:1700::1]:443"
		if (first.StartsWith('['))
		{
			var close = first.IndexOf(']');
			return close > 1 ? first[1..close] : first;
		}

		// Exactly one colon means IPv4 with a port. Several means a bare IPv6 address, which
		// must be kept whole. None means a bare IPv4.
		return first.Count(c => c == ':') == 1 ? first.Split(':')[0] : first;
	}

	/// <summary>
	/// The children view, with each organization's REAL NAME resolved.
	///
	/// A guardian must never be shown a raw tenant id — the platform has already shipped that
	/// bug once (a membership chip reading as a GUID) and a stranger to the system has no way at
	/// all to interpret one. If the tenant cannot be read the link is still listed, because
	/// hiding a child would be worse than naming their organization vaguely.
	/// </summary>
	private async Task<List<object>> ChildrenViewAsync(Guardian guardian)
	{
		var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var orgId in guardian.Links.Select(l => l.OrganizationId).Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (string.IsNullOrWhiteSpace(orgId)) continue;
			var tenant = await cosmos.GetTenantAsync(orgId);
			if (!string.IsNullOrWhiteSpace(tenant?.Name)) names[orgId] = tenant!.Name;
		}

		return guardian.Links.Select(l => (object)new
		{
			minorUserId = l.MinorUserId,
			organizationId = l.OrganizationId,
			organizationName = names.TryGetValue(l.OrganizationId ?? string.Empty, out var n) ? n : "this organization",
			minorName = l.MinorName,
			consent = ConsentView(guardian, l),
		}).ToList();
	}

	private static object? ConsentView(Guardian g, GuardianLink l)
	{
		var c = g.Consents.FirstOrDefault(x =>
			string.Equals(x.MinorUserId, l.MinorUserId, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(x.OrganizationId, l.OrganizationId, StringComparison.OrdinalIgnoreCase));
		if (c == null) return null;
		return new { status = c.Status, grantedAt = c.GrantedAt, revokedAt = c.RevokedAt, active = c.IsActive() };
	}

	private static string DisplayNameOf(User u) =>
		!string.IsNullOrWhiteSpace(u.DisplayName) ? u.DisplayName
		: string.Join(' ', new[] { u.FirstName, u.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim() is { Length: > 0 } n ? n
		: u.Email;

	private sealed record LinkGuardianRequest(
		string OrganizationId, string MinorUserId, string Email, string? Name, string? Phone, string? Reason);

	private sealed record RedeemRequest(string Token);

	// EventId is optional: absent, this grants/revokes STANDING consent for the org; present, it
	// grants/revokes the per-event approval a carve-out event needs (#20 remainder).
	private sealed record ConsentRequest(
		string SessionToken, string MinorUserId, string OrganizationId, string Action, string? EventId);
}
