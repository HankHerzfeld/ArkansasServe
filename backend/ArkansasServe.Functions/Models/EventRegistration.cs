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

	// The event's owning org — this is the Events partition key, which can differ from
	// SchoolId (the registrant's home tenant) for a cross-org sign-up. Used to locate the
	// event when adjusting slot/shift counts on cancel. Nullable for pre-existing records.
	[JsonPropertyName("organizationId")]
	public string? OrganizationId { get; set; }

	[JsonPropertyName("status")]
	public string Status { get; set; } = "Registered";

	[JsonPropertyName("checkedInAt")]
	public DateTime? CheckedInAt { get; set; }

	// The shift the volunteer chose, when the event has shifts.
	[JsonPropertyName("shiftId")]
	public string? ShiftId { get; set; }

	// Answers to the event's custom sign-up questions.
	[JsonPropertyName("answers")]
	public List<RegistrationAnswer> Answers { get; set; } = [];
}

public class RegistrationAnswer
{
	[JsonPropertyName("questionId")]
	public string QuestionId { get; set; } = string.Empty;

	[JsonPropertyName("question")]
	public string Question { get; set; } = string.Empty;

	[JsonPropertyName("answer")]
	public string Answer { get; set; } = string.Empty;
}
