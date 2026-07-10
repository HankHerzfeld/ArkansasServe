using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Models;

// Append-only audit record (Phase F #26). Partitioned by adminUserId. Impersonation
// start/stop are always logged; the start is fail-closed (no session without its log).
public class AuditEvent : CosmosDocument
{
	[JsonPropertyName("adminUserId")]
	public string AdminUserId { get; set; } = string.Empty; // partition key

	[JsonPropertyName("sessionId")]
	public string? SessionId { get; set; }

	[JsonPropertyName("targetUserId")]
	public string? TargetUserId { get; set; }

	[JsonPropertyName("action")]
	public string Action { get; set; } = string.Empty; // e.g. impersonation.start | impersonation.stop

	[JsonPropertyName("detail")]
	public string? Detail { get; set; }

	[JsonPropertyName("at")]
	public DateTime At { get; set; } = DateTime.UtcNow;
}
