using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Models;

/// <summary>
/// An org-defined credential an org tracks against its people: "Waiver signed", "Masonry
/// training complete", "Food handler card". The DEFINITION lives on the Tenant; a person's
/// state against it is a <see cref="UserTagState"/> on their per-org User doc.
///
/// Org-defined, unlike <see cref="ServiceCategories"/>, and deliberately so: a service
/// category is a shared vocabulary that only works if everyone uses the same words, whereas
/// "masonry training complete" is meaningful to exactly one org and meaningless to compare
/// across them. Nothing here is searched across orgs, so nothing here needs controlling.
/// </summary>
public class TenantUserTag
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = Guid.NewGuid().ToString();

	[JsonPropertyName("label")]
	public string Label { get; set; } = string.Empty;

	/// <summary>Optional hint shown to admins, e.g. "Signed by a guardian for under-18s".</summary>
	[JsonPropertyName("description")]
	public string? Description { get; set; }

	/// <summary>
	/// What happens when someone lacks this tag. See <see cref="TagEnforcement"/>.
	/// Defaults to advisory: a tag that silently starts turning volunteers away the moment an
	/// admin creates it would be a nasty surprise, so blocking is opt-in.
	/// </summary>
	[JsonPropertyName("enforcement")]
	public string Enforcement { get; set; } = TagEnforcement.Advisory;

	/// <summary>
	/// Days until a completed tag goes stale; null means it never does. A waiver or a
	/// background check genuinely expires; "masonry training complete" does not. An expired
	/// credential that still reads as current is worse than not tracking it — it looks like
	/// compliance without being it.
	/// </summary>
	[JsonPropertyName("expiresAfterDays")]
	public int? ExpiresAfterDays { get; set; }

	/// <summary>Archived tags stop being offered but keep existing people's history readable.</summary>
	[JsonPropertyName("status")]
	public string Status { get; set; } = "active";
}

/// <summary>What lacking a tag does. Stored on the tag definition, per-tag (owner decision).</summary>
public static class TagEnforcement
{
	/// <summary>
	/// Visible to admins and flagged, but nobody is stopped. The DEFAULT, per the owner: a
	/// volunteer's first attempt should alert an admin, not refuse them.
	/// </summary>
	public const string Advisory = "advisory";

	/// <summary>Registration is refused, naming what is missing.</summary>
	public const string BlockRegistration = "blockRegistration";

	// Reserved, not implemented: "blockCheckIn" — let someone sign up but refuse them at the
	// door. That is the more useful rule for a waiver (sign up now, sign the form before you
	// serve), and it is what "refused at the gate" means. It lands with #14 (day-of check-in),
	// because until a check-in exists this setting would silently do nothing — and a control
	// that does nothing is worse than an absent one.

	public static bool IsValid(string? v) =>
		v is Advisory or BlockRegistration;
}

/// <summary>
/// One person's state against one of their org's tags. Lives on the per-org User doc, which is
/// already partitioned by org — so "per-org tags" needs no new structure.
///
/// State + date rather than a bare boolean (owner decision), mirroring the BackgroundCheckStatus
/// field that already works this way: "pending" is a real and different thing from "never
/// started", and an admin chasing a waiver needs to tell them apart.
/// </summary>
public class UserTagState
{
	/// <summary>Points at <see cref="TenantUserTag.Id"/> on the person's own org.</summary>
	[JsonPropertyName("tagId")]
	public string TagId { get; set; } = string.Empty;

	/// <summary>None | Pending | Complete — see <see cref="TagStatuses"/>.</summary>
	[JsonPropertyName("status")]
	public string Status { get; set; } = TagStatuses.None;

	[JsonPropertyName("completedAt")]
	public DateTime? CompletedAt { get; set; }

	/// <summary>
	/// Stamped when the tag is completed, from the definition's ExpiresAfterDays. STORED rather
	/// than computed on read, for two reasons: an admin can override one person's expiry
	/// without touching the org's policy, and changing the policy later does not silently
	/// expire — or un-expire — everyone who already completed it.
	/// </summary>
	[JsonPropertyName("expiresAt")]
	public DateTime? ExpiresAt { get; set; }

	/// <summary>Free note from the admin who set it, e.g. a certificate number.</summary>
	[JsonPropertyName("note")]
	public string? Note { get; set; }

	/// <summary>True only when complete AND not past its expiry. This is what a gate asks.</summary>
	public bool IsCurrentAt(DateTime now) =>
		string.Equals(Status, TagStatuses.Complete, StringComparison.OrdinalIgnoreCase)
		&& (!ExpiresAt.HasValue || ExpiresAt.Value > now);
}

public static class TagStatuses
{
	public const string None = "None";
	public const string Pending = "Pending";
	public const string Complete = "Complete";

	public static bool IsValid(string? v) =>
		v is not null && (v.Equals(None, StringComparison.OrdinalIgnoreCase)
			|| v.Equals(Pending, StringComparison.OrdinalIgnoreCase)
			|| v.Equals(Complete, StringComparison.OrdinalIgnoreCase));
}
