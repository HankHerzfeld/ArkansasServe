using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Models;

/// <summary>
/// A parent or guardian who oversees one or more minors (#20).
///
/// DELIBERATELY NOT AN ACCOUNT. A guardian has no Entra identity, no password and no
/// membership — they are someone outside the platform who is reachable by a signed one-time
/// link. That was the owner's decision over full accounts: it buys an identified signer,
/// revocable consent and an audit trail without adding an identity type, an invite flow and a
/// permission model. The accepted weakness is that whoever holds a live link can act as the
/// guardian, which is why links are single-use and short-lived.
///
/// ONE GUARDIAN, MANY MINORS, MANY ORGS. Keyed by email, so a parent with children at two
/// different schools is one record with one inbox — the case a per-minor-per-org model breaks
/// on. Consent is still recorded per (guardian, minor, organization).
///
/// STORAGE: the Users container, in the reserved `guardians` partition (see TenantIds), because
/// containers are Bicep-defined and the deploy does not run Bicep.
/// </summary>
public class Guardian
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = Guid.NewGuid().ToString();

	/// <summary>Partition key. Always <see cref="Functions.TenantIds.Guardians"/>.</summary>
	[JsonPropertyName("tenantId")]
	public string TenantId { get; set; } = Functions.TenantIds.Guardians;

	/// <summary>
	/// Discriminator. Guardian reads filter on this so isolation from member records never
	/// depends on the partition name alone.
	/// </summary>
	[JsonPropertyName("docType")]
	public string DocType { get; set; } = GuardianDocType.Value;

	/// <summary>
	/// The natural key, always stored lowercased — this is how a guardian is recognised across
	/// organizations and how a returning link request finds the existing record rather than
	/// creating a second one.
	/// </summary>
	[JsonPropertyName("email")]
	public string Email { get; set; } = string.Empty;

	[JsonPropertyName("name")]
	public string? Name { get; set; }

	[JsonPropertyName("phone")]
	public string? Phone { get; set; }

	/// <summary>Which minors this guardian oversees, and where.</summary>
	[JsonPropertyName("links")]
	public List<GuardianLink> Links { get; set; } = [];

	/// <summary>
	/// Consent per (minor, organization), ongoing until revoked. Kept as history rather than a
	/// flag: a withdrawal must stay visible after the fact, and re-granting must not erase that
	/// it was once withdrawn.
	/// </summary>
	[JsonPropertyName("consents")]
	public List<GuardianConsent> Consents { get; set; } = [];

	/// <summary>The one live magic link, if any. Minting a new one invalidates the previous.</summary>
	[JsonPropertyName("magicLink")]
	public MagicLinkState? MagicLink { get; set; }

	/// <summary>
	/// The short-lived session minted when a link is redeemed.
	///
	/// WHY THIS HAS TO EXIST. The magic link is single-use and is consumed by redemption, so
	/// without a session the guardian lands on their page holding no credential and cannot
	/// submit anything. The alternative — leaving the link live so it can also authorise the
	/// submission — would turn a seven-day forwardable email into standing access to a child's
	/// consent, which is exactly what single-use was chosen to prevent. So the link buys one
	/// thing: a brief working session.
	/// </summary>
	[JsonPropertyName("session")]
	public GuardianSessionState? Session { get; set; }

	[JsonPropertyName("createdAt")]
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

	[JsonPropertyName("updatedAt")]
	public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class GuardianDocType
{
	public const string Value = "guardian";
}

/// <summary>A guardian's link to one minor, in one organization.</summary>
public class GuardianLink
{
	/// <summary>The minor's per-org User document id.</summary>
	[JsonPropertyName("minorUserId")]
	public string MinorUserId { get; set; } = string.Empty;

	/// <summary>The organization that User doc belongs to — its partition key.</summary>
	[JsonPropertyName("organizationId")]
	public string OrganizationId { get; set; } = string.Empty;

	/// <summary>
	/// Denormalised so a guardian's own view can name the child without reading every org
	/// partition they are linked into. Refreshed when the link is re-asserted.
	/// </summary>
	[JsonPropertyName("minorName")]
	public string? MinorName { get; set; }

	[JsonPropertyName("createdAt")]
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Ongoing consent for one minor in one organization. Carve-outs (an org-flagged event, or an
/// overnight/multi-day one) still require fresh approval and are NOT covered by this.
/// </summary>
public class GuardianConsent
{
	[JsonPropertyName("minorUserId")]
	public string MinorUserId { get; set; } = string.Empty;

	[JsonPropertyName("organizationId")]
	public string OrganizationId { get; set; } = string.Empty;

	[JsonPropertyName("status")]
	public string Status { get; set; } = GuardianConsentStatus.Granted;

	[JsonPropertyName("grantedAt")]
	public DateTime? GrantedAt { get; set; }

	[JsonPropertyName("revokedAt")]
	public DateTime? RevokedAt { get; set; }

	/// <summary>
	/// The policy/waiver wording in force when consent was given. Recorded because re-issuing
	/// the documents must be able to re-prompt, exactly as `acceptedPolicyVersion` does for
	/// Terms — consent to superseded wording is not consent to the current wording.
	/// </summary>
	[JsonPropertyName("documentVersion")]
	public string? DocumentVersion { get; set; }

	/// <summary>Evidence of the attestation: recorded server-side, never sent by the client.</summary>
	[JsonPropertyName("attestedFromIp")]
	public string? AttestedFromIp { get; set; }

	public bool IsActive() =>
		string.Equals(Status, GuardianConsentStatus.Granted, StringComparison.OrdinalIgnoreCase)
		&& RevokedAt == null;
}

public static class GuardianConsentStatus
{
	public const string Granted = "granted";
	public const string Revoked = "revoked";

	public static bool IsValid(string? s) =>
		string.Equals(s, Granted, StringComparison.OrdinalIgnoreCase)
		|| string.Equals(s, Revoked, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The state of the guardian's one-time access link.
///
/// ⚠️ ONLY A HASH IS STORED. The raw token is returned once, at mint, and never persisted.
/// The Users container is readable through the SuperAdmin DB console, so a raw token sitting
/// in a document would let anyone with read access act as that guardian — which is precisely
/// the authority the link carries.
/// </summary>
public class MagicLinkState
{
	/// <summary>Base64 SHA-256 of the raw token.</summary>
	[JsonPropertyName("tokenHash")]
	public string TokenHash { get; set; } = string.Empty;

	[JsonPropertyName("expiresAt")]
	public DateTime ExpiresAt { get; set; }

	/// <summary>Set the moment the link is redeemed. Single-use: a consumed link never works again.</summary>
	[JsonPropertyName("consumedAt")]
	public DateTime? ConsumedAt { get; set; }

	[JsonPropertyName("issuedAt")]
	public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

	/// <summary>
	/// Why the link was sent, so the redeeming page can say what it is for and an audit trail
	/// records intent (e.g. "consent requested by Demo School for Jane Doe").
	/// </summary>
	[JsonPropertyName("reason")]
	public string? Reason { get; set; }

	/// <summary>
	/// Live means: not consumed AND not expired. Both halves matter — a link that is merely
	/// unexpired is still spent, and a link that is merely unconsumed is still stale.
	/// </summary>
	public bool IsLive(DateTime now) => ConsumedAt == null && ExpiresAt > now;
}

/// <summary>
/// A guardian's working session, minted at redemption. Deliberately SHORT: it exists to cover
/// one sitting at the consent page, not to be a login. Hashed for the same reason the link is —
/// the Users container is readable through the SuperAdmin DB console.
/// </summary>
public class GuardianSessionState
{
	/// <summary>Base64 SHA-256 of the raw session token.</summary>
	[JsonPropertyName("tokenHash")]
	public string TokenHash { get; set; } = string.Empty;

	[JsonPropertyName("issuedAt")]
	public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

	[JsonPropertyName("expiresAt")]
	public DateTime ExpiresAt { get; set; }

	public bool IsLive(DateTime now) => ExpiresAt > now;
}
