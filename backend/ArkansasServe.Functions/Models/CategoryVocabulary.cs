using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Models;

/// <summary>
/// The stored half of the service-category vocabulary (#10②). The canonical list stays
/// code-defined in <see cref="ServiceCategories"/>; this holds what orgs have added on top:
/// approved-new labels that extend the list, aliases that fold a proposed spelling onto a
/// canonical value, and the proposals (pending and resolved) a SuperAdmin reviews.
///
/// Stored as ONE object on the root tenant doc (<c>arkansas-serve-root</c>), not a new
/// container: it is a single platform-wide singleton read with the tenant, and adding a
/// container would need infra provisioning the app deploy does not run.
/// </summary>
public class CategoryVocabulary
{
	/// <summary>Approved-new labels that extend the canonical list. These ARE real categories now.</summary>
	[JsonPropertyName("approvedNew")]
	public List<string> ApprovedNew { get; set; } = [];

	/// <summary>
	/// Proposed label → the canonical value it was approved as an alias of. Keyed by the label
	/// as proposed; consumers match case-insensitively. This is the anti-fragmentation path:
	/// "Food Bank"/"food bank"/"Foodbank" all alias onto "Food &amp; Nutrition".
	/// </summary>
	[JsonPropertyName("aliases")]
	public Dictionary<string, string> Aliases { get; set; } = [];

	/// <summary>Every proposal, pending and resolved alike (kept for the audit trail and the queue).</summary>
	[JsonPropertyName("proposals")]
	public List<ProposedCategory> Proposals { get; set; } = [];
}

/// <summary>
/// One org's request to add a service category the canonical list does not have. Recorded the
/// moment an org (or an event in it) is saved with an unknown label, and resolved by a
/// SuperAdmin as either a new canonical value or an alias of an existing one.
/// </summary>
public class ProposedCategory
{
	/// <summary>The label exactly as the org typed it — what is stored on their org/event until resolved.</summary>
	[JsonPropertyName("label")]
	public string Label { get; set; } = string.Empty;

	/// <summary>The org that first proposed it (for the queue's "who asked" and the audit trail).</summary>
	[JsonPropertyName("proposingOrgId")]
	public string ProposingOrgId { get; set; } = string.Empty;

	[JsonPropertyName("proposingOrgName")]
	public string? ProposingOrgName { get; set; }

	/// <summary>Where it was proposed: an org's <c>serviceCategory</c> or an event's <c>category</c>.</summary>
	[JsonPropertyName("source")]
	public string Source { get; set; } = CategoryProposalSources.Org;

	[JsonPropertyName("status")]
	public string Status { get; set; } = CategoryProposalStatus.Pending;

	/// <summary>Set only when <see cref="Status"/> is approvedAlias: the canonical value it maps onto.</summary>
	[JsonPropertyName("aliasOfCanonical")]
	public string? AliasOfCanonical { get; set; }

	[JsonPropertyName("createdAt")]
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

	[JsonPropertyName("resolvedAt")]
	public DateTime? ResolvedAt { get; set; }

	[JsonPropertyName("resolvedByUserId")]
	public string? ResolvedByUserId { get; set; }
}

public static class CategoryProposalStatus
{
	public const string Pending = "pending";
	public const string ApprovedNew = "approvedNew";
	public const string ApprovedAlias = "approvedAlias";
	public const string Rejected = "rejected";

	public static bool IsResolved(string? s) =>
		s is ApprovedNew or ApprovedAlias or Rejected;
}

public static class CategoryProposalSources
{
	public const string Org = "org";
	public const string Event = "event";
}
