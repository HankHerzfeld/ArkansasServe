using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Models;

public class Notification : CosmosDocument
{
	[JsonPropertyName("userId")]
	public string UserId { get; set; } = string.Empty;

	[JsonPropertyName("type")]
	public string Type { get; set; } = string.Empty;

	[JsonPropertyName("message")]
	public string Message { get; set; } = string.Empty;

	[JsonPropertyName("isRead")]
	public bool IsRead { get; set; } = false;

	[JsonPropertyName("relatedId")]
	public string? RelatedId { get; set; }
}
