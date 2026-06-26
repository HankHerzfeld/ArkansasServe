using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Models;

public class PendingApproval : CosmosDocument
{
	[JsonPropertyName("schoolId")]
	public string SchoolId { get; set; } = string.Empty;

	[JsonPropertyName("serviceLogId")]
	public string ServiceLogId { get; set; } = string.Empty;

	[JsonPropertyName("studentId")]
	public string StudentId { get; set; } = string.Empty;

	[JsonPropertyName("studentName")]
	public string StudentName { get; set; } = string.Empty;

	[JsonPropertyName("organizationName")]
	public string OrganizationName { get; set; } = string.Empty;

	[JsonPropertyName("eventTitle")]
	public string EventTitle { get; set; } = string.Empty;

	[JsonPropertyName("hoursLogged")]
	public double HoursLogged { get; set; }

	[JsonPropertyName("serviceDate")]
	public DateTime ServiceDate { get; set; }
}
