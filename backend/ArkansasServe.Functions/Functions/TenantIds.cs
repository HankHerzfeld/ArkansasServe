namespace ArkansasServe.Functions.Functions;

// Reserved tenant (organization) partition keys. These are NOT organizations anyone can
// join or browse — they are fixed partitions the platform relies on, so they are named
// here once rather than spelled out at each call site.
public static class TenantIds
{
	// The platform's own host organization. Real, seeded, and has a Tenant doc.
	public const string Root = "arkansas-serve-root";

	// Where a signed-in person's profile lives while they have NO assigned or joined
	// organization. Deliberately NOT a real org: it has no Tenant doc, so it is omitted
	// from /manage/me/memberships and can never be scoped to, browsed, or joined — the
	// UI reports "no assigned or joined organization" instead.
	//
	// This exists because a person's profile document is partitioned BY organization, so
	// someone with no org still needs somewhere to keep their name and intake answers
	// until they join one. On their first join the profile is migrated into the new org's
	// document and this one is deleted (see MembershipFunctions.JoinOrg).
	//
	// It replaces the old behaviour of falling back to the Entra directory id ("tid"),
	// which silently invented an organization out of the identity provider's own GUID.
	public const string Unassigned = "unassigned";

	// Where GUARDIAN records live (#20). A guardian is deliberately NOT an organization member
	// and NOT an Entra account: they are a person outside the platform who oversees a minor,
	// reachable only by a signed one-time link.
	//
	// They share the Users CONTAINER because containers are Bicep-defined and the deploy does
	// not run Bicep — a new container cannot be added without infra work. A reserved partition
	// is the same device `Unassigned` already uses, and it isolates them cleanly: every member
	// query is either partition-scoped (an org id, never "guardians") or keyed on ExternalId
	// (which a guardian has none of), so a guardian document can never surface as a member.
	// Guardian reads additionally filter on `docType` so that isolation does not rest on
	// partition naming alone.
	public const string Guardians = "guardians";
}
