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

		await cosmos.UpsertGuardianAsync(guardian!);
		logger.LogInformation("[Guardian] {GuardianId} redeemed a link", guardian!.Id);

		return await HttpHelper.OkJson(req, new
		{
			guardianId = guardian.Id,
			name = guardian.Name,
			email = guardian.Email,
			reason = guardian.MagicLink?.Reason,
			children = guardian.Links.Select(l => new
			{
				minorUserId = l.MinorUserId,
				organizationId = l.OrganizationId,
				minorName = l.MinorName,
				consent = ConsentView(guardian, l),
			}),
		});
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
}
