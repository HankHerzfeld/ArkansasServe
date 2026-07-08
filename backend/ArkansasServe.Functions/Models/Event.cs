using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Models;

public class Event : CosmosDocument
{
	[JsonPropertyName("organizationId")]
	public string OrganizationId { get; set; } = string.Empty;

	[JsonPropertyName("organizationName")]
	public string OrganizationName { get; set; } = string.Empty;

	[JsonPropertyName("title")]
	public string Title { get; set; } = string.Empty;

	[JsonPropertyName("description")]
	public string? Description { get; set; }

	[JsonPropertyName("location")]
	public string Location { get; set; } = string.Empty;

	[JsonPropertyName("startDateTime")]
	public DateTime StartDateTime { get; set; }

	[JsonPropertyName("endDateTime")]
	public DateTime EndDateTime { get; set; }

	[JsonPropertyName("maxSlots")]
	public int MaxSlots { get; set; } = 0;

	[JsonPropertyName("currentSlots")]
	public int CurrentSlots { get; set; } = 0;

	[JsonPropertyName("hoursValue")]
	public double HoursValue { get; set; }

	[JsonPropertyName("status")]
	public string Status { get; set; } = "Open";

	[JsonPropertyName("eligibleSchoolIds")]
	public List<string> EligibleSchoolIds { get; set; } = [];

	[JsonPropertyName("photoUrl")]
	public string? PhotoUrl { get; set; }

	[JsonPropertyName("category")]
	public string? Category { get; set; }

	// ── Optional enrichment (rendered only when present) ────────────────────────
	[JsonPropertyName("tags")]
	public List<string> Tags { get; set; } = [];

	// What volunteers should know or bring (free text).
	[JsonPropertyName("requirements")]
	public string? Requirements { get; set; }

	// External info/registration link.
	[JsonPropertyName("externalUrl")]
	public string? ExternalUrl { get; set; }

	[JsonPropertyName("contactName")]
	public string? ContactName { get; set; }

	[JsonPropertyName("contactEmail")]
	public string? ContactEmail { get; set; }

	[JsonPropertyName("contactPhone")]
	public string? ContactPhone { get; set; }

	// Optional nested-group association within the organization, used to scope
	// which events a GroupAdmin (and the group switcher) sees.
	[JsonPropertyName("groupId")]
	public string? GroupId { get; set; }

	// "org" (default) = visible only within the owning organization; "public" =
	// discoverable by admins in other orgs so they can add their own volunteers.
	[JsonPropertyName("visibility")]
	public string Visibility { get; set; } = "org";

	[JsonPropertyName("createdByUserId")]
	public string CreatedByUserId { get; set; } = string.Empty;
}
