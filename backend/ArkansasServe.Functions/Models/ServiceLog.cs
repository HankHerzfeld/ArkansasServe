using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Models;

public class ServiceLog : CosmosDocument
{
	[JsonPropertyName("studentId")]
	public string StudentId { get; set; } = string.Empty;

	[JsonPropertyName("studentName")]
	public string StudentName { get; set; } = string.Empty;

	[JsonPropertyName("schoolId")]
	public string SchoolId { get; set; } = string.Empty;

	[JsonPropertyName("eventId")]
	public string EventId { get; set; } = string.Empty;

	[JsonPropertyName("eventTitle")]
	public string EventTitle { get; set; } = string.Empty;

	[JsonPropertyName("organizationId")]
	public string OrganizationId { get; set; } = string.Empty;

	[JsonPropertyName("organizationName")]
	public string OrganizationName { get; set; } = string.Empty;

	[JsonPropertyName("hoursLogged")]
	public double HoursLogged { get; set; }

	[JsonPropertyName("serviceDate")]
	public DateTime ServiceDate { get; set; }

	[JsonPropertyName("status")]
	public string Status { get; set; } = "Pending";

	[JsonPropertyName("submittedByUserId")]
	public string SubmittedByUserId { get; set; } = string.Empty;

	[JsonPropertyName("reviewedByUserId")]
	public string? ReviewedByUserId { get; set; }

	[JsonPropertyName("reviewNote")]
	public string? ReviewNote { get; set; }

	[JsonPropertyName("reviewedAt")]
	public DateTime? ReviewedAt { get; set; }

	[JsonPropertyName("verificationDocUrl")]
	public string? VerificationDocUrl { get; set; }

	// A DEMO service log (belongs to a demo persona/event). Defaults false. Reset bookkeeping only.
	[JsonPropertyName("isDemo")]
	public bool IsDemo { get; set; }
}
