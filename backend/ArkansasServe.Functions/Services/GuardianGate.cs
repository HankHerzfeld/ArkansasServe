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
/// CARVE-OUTS ARE DELIBERATELY NOT HERE YET. Org-flagged and overnight/multi-day events are
/// meant to need FRESH approval, which requires a per-event approval mechanism that does not
/// exist. Blocking on them now would be a refusal with no way to clear it — worse than the
/// inert `blockCheckIn` setting the roadmap already regretted, because that merely did nothing
/// whereas this would strand a family with no available action. They land together.
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

	/// <summary>The message shown to whoever is being refused. Never names the guardian.</summary>
	public static string MessageFor(Outcome outcome, string? who = null) => outcome switch
	{
		Outcome.Withdrawn => who == null
			? "A parent or guardian has withdrawn consent for you to volunteer with this organization."
			: $"A parent or guardian has withdrawn consent for {who} to volunteer with this organization.",
		Outcome.Missing => who == null
			? "This organization needs a parent or guardian to give consent before you can sign up. Ask an admin to send them a consent link."
			: $"This organization needs a parent or guardian to give consent before {who} can sign up. Send them a consent link, then try again.",
		_ => string.Empty,
	};
}
