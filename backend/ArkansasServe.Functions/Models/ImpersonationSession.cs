using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Models;

// A SuperAdmin "act as" session (Phase F #26). Opaque server-side record: the client
// holds only the id and sends it as X-Impersonation-Session; AuthMiddleware resolves
// the effective (target) context from this snapshot each request. Partitioned by
// adminUserId ("sessions started by this admin"). See docs/remote-access-impersonation-design.md.
public class ImpersonationSession : CosmosDocument
{
	[JsonPropertyName("adminUserId")]
	public string AdminUserId { get; set; } = string.Empty; // real super (partition key)

	[JsonPropertyName("adminName")]
	public string AdminName { get; set; } = string.Empty;

	[JsonPropertyName("adminEmail")]
	public string AdminEmail { get; set; } = string.Empty;

	[JsonPropertyName("targetUserId")]
	public string TargetUserId { get; set; } = string.Empty; // the impersonated User doc id

	[JsonPropertyName("targetActingId")]
	public string TargetActingId { get; set; } = string.Empty; // externalId ?? id — the effective UserId

	[JsonPropertyName("targetTenantId")]
	public string TargetTenantId { get; set; } = string.Empty;

	[JsonPropertyName("targetName")]
	public string TargetName { get; set; } = string.Empty;

	[JsonPropertyName("targetEmail")]
	public string TargetEmail { get; set; } = string.Empty;

	[JsonPropertyName("targetAdminLevel")]
	public string TargetAdminLevel { get; set; } = "Student"; // snapshot of the target's effective level

	[JsonPropertyName("targetIsDemo")]
	public bool TargetIsDemo { get; set; }

	[JsonPropertyName("reason")]
	public string Reason { get; set; } = string.Empty;

	[JsonPropertyName("mode")]
	public string Mode { get; set; } = "read-only"; // Phase 1 is read-only

	[JsonPropertyName("startedAt")]
	public DateTime StartedAt { get; set; } = DateTime.UtcNow;

	[JsonPropertyName("expiresAt")]
	public DateTime ExpiresAt { get; set; }

	[JsonPropertyName("endedAt")]
	public DateTime? EndedAt { get; set; }

	[JsonPropertyName("revoked")]
	public bool Revoked { get; set; }

	public bool IsActive(DateTime now) => !Revoked && EndedAt == null && now < ExpiresAt;
}
