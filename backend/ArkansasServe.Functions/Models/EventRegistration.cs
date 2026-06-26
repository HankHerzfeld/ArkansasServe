using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Models;

public class EventRegistration : CosmosDocument
{
	[JsonPropertyName("eventId")]
	public string EventId { get; set; } = string.Empty;

	[JsonPropertyName("userId")]
	public string UserId { get; set; } = string.Empty;

	[JsonPropertyName("studentName")]
	public string StudentName { get; set; } = string.Empty;

	[JsonPropertyName("schoolId")]
	public string SchoolId { get; set; } = string.Empty;

	[JsonPropertyName("status")]
	public string Status { get; set; } = "Registered";

	[JsonPropertyName("checkedInAt")]
	public DateTime? CheckedInAt { get; set; }
}
