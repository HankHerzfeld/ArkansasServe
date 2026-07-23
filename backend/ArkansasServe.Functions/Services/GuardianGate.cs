using ArkansasServe.Functions.Functions;
using ArkansasServe.Functions.Models;

namespace ArkansasServe.Functions.Services;

/// <summary>
/// Whether guardian consent permits a minor to register (#20). Mirrors <see cref="TagGate"/>:
/// a pure decision with no I/O, so the caller decides when it applies and owns the lookups.
///
/// THE RULES, and why they are asymmetric:
///
///   not a minor                        → allowed. Nothing to consent to.
///   minor, no guardian attached        → allowed UNLESS the tenant opted in. Owner decision:
///                                        "allow until attached", because requiring consent is
///                                        a tenant-by-tenant policy and a default of required
///                                        would lock every existing minor out on deploy day.
///   minor, guardian attached, granted  → allowed.
///   minor, guardian attached, no record→ the tenant's flag decides. Silence is not refusal.
///   minor, consent REVOKED             → BLOCKED ALWAYS, whatever the tenant flag says.
///
/// That last asymmetry is the point. Absence of consent is permissive; a guardian actively
/// withdrawing is a decision. Withdrawal already cancels that minor's future registrations, so
/// permitting a fresh sign-up a minute later would make the whole withdrawal theatre.
///
/// CARVE-OUTS (#20 remainder). Two kinds of event need FRESH, per-event approval on top of any
/// standing consent — an org-flagged one (<see cref="Event.RequiresFreshGuardianApproval"/>) and
/// an overnight/multi-day one. Standing consent covers routine sign-ups; a trip that runs past
/// midnight is a different thing to agree to, and the design's rule is that agreeing once to
/// "volunteering with this org" is not agreeing to that.
///
/// These land WITH their clearing path, deliberately: a guardian grants the per-event approval
/// through the same magic-link session they already use for consent (see
/// GuardianFunctions.SetGuardianConsent, which takes an optional eventId). A refusal with no
/// available action would strand a family, which is why this could not ship on the schema alone.
/// </summary>
public static class GuardianGate
{
	public enum Outcome
	{
		/// <summary>Nothing blocks this registration.</summary>
		Allowed,

		/// <summary>A guardian withdrew consent. Always blocking.</summary>
		Withdrawn,

		/// <summary>The org requires consent and none is on file. Blocking only for that org.</summary>
		Missing,

		/// <summary>
		/// This event needs FRESH per-event approval (org-flagged or overnight) and none is on
		/// file. Standing consent does not satisfy it — that is the whole point of the carve-out.
		/// </summary>
		EventApprovalMissing,
	}

	/// <summary>
	/// Whether this event needs fresh, per-event guardian approval. Two independent triggers:
	/// the org set the flag on it, or it is overnight/multi-day.
	///
	/// "Overnight" is decided on the CENTRAL local calendar day, not UTC: an event running
	/// 6pm–10pm Central is a single local evening but spans two UTC dates, so a UTC comparison
	/// would demand fresh approval for ordinary evening shifts. A missing/blank end time is not
	/// treated as overnight — absence of data is not evidence of a sleepover.
	/// </summary>
	public static bool RequiresFreshApproval(Event? evt)
	{
		if (evt == null) return false;
		if (evt.RequiresFreshGuardianApproval) return true;
		if (evt.StartDateTime == default || evt.EndDateTime == default) return false;
		if (evt.EndDateTime <= evt.StartDateTime) return false;
		return AppTimeZone.LocalDay(evt.EndDateTime) > AppTimeZone.LocalDay(evt.StartDateTime);
	}

	/// <summary>
	/// <paramref name="guardians"/> must be those linked to this member IN THIS ORGANIZATION —
	/// consent is per (guardian, minor, org), so passing a guardian's links from elsewhere would
	/// apply one school's decision to another's events.
	/// </summary>
	public static Outcome Evaluate(User? member, Tenant? org, IReadOnlyList<Guardian> guardians, string organizationId)
	{
		if (member == null) return Outcome.Allowed;
		if (!IntakeValidation.IsMinor(member)) return Outcome.Allowed;

		var relevant = guardians
			.SelectMany(g => g.Consents)
			.Where(c => string.Equals(c.MinorUserId, member.Id, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(c.OrganizationId, organizationId, StringComparison.OrdinalIgnoreCase))
			.ToList();

		// Checked FIRST and independently of the tenant flag: one guardian saying no outranks
		// another's silence, and outranks an org that has not opted in.
		if (relevant.Any(c => string.Equals(c.Status, GuardianConsentStatus.Revoked, StringComparison.OrdinalIgnoreCase)))
			return Outcome.Withdrawn;

		if (relevant.Any(c => c.IsActive())) return Outcome.Allowed;

		// No decision either way. Permissive unless this org asked for the stricter rule.
		return org?.RequireGuardianConsent == true ? Outcome.Missing : Outcome.Allowed;
	}

	/// <summary>
	/// The full gate for registering onto ONE event: standing consent first, then the per-event
	/// carve-out. Order matters — a WITHDRAWN consent outranks any per-event approval, so a
	/// guardian who has said no cannot be routed around by approving a single event.
	/// </summary>
	public static Outcome EvaluateForEvent(
		User? member, Tenant? org, IReadOnlyList<Guardian> guardians, string organizationId, Event? evt)
	{
		var standing = Evaluate(member, org, guardians, organizationId);
		if (standing != Outcome.Allowed) return standing;

		// Only a minor, and only on a carve-out event, needs anything further.
		if (member == null || !IntakeValidation.IsMinor(member)) return Outcome.Allowed;
		if (!RequiresFreshApproval(evt)) return Outcome.Allowed;

		// A REVOKED per-event approval fails IsActive(), so revoking one re-blocks that event
		// without touching the family's standing consent.
		var approved = guardians
			.SelectMany(g => g.EventApprovals)
			.Any(a => string.Equals(a.MinorUserId, member.Id, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(a.OrganizationId, organizationId, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(a.EventId, evt!.Id, StringComparison.OrdinalIgnoreCase)
				&& a.IsActive());

		return approved ? Outcome.Allowed : Outcome.EventApprovalMissing;
	}

	/// <summary>The message shown to whoever is being refused. Never names the guardian.</summary>
	public static string MessageFor(Outcome outcome, string? who = null) => outcome switch
	{
		Outcome.Withdrawn => who == null
			? "A parent or guardian has withdrawn consent for you to volunteer with this organization."
			: $"A parent or guardian has withdrawn consent for {who} to volunteer with this organization.",
		Outcome.Missing => who == null
			? "This organization needs a parent or guardian to give consent before you can sign up. Ask an admin to send them a consent link."
			: $"This organization needs a parent or guardian to give consent before {who} can sign up. Send them a consent link, then try again.",
		// Says WHY this event is different, so "but they already consented" has an answer.
		Outcome.EventApprovalMissing => who == null
			? "This event needs its own approval from a parent or guardian (it runs overnight or the organizer flagged it). Ask an admin to send them a link for this event."
			: $"This event needs its own approval from a parent or guardian before {who} can sign up (it runs overnight or the organizer flagged it). Send them a link for this event, then try again.",
		_ => string.Empty,
	};
}
