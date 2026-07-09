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

	// Optional shifts/time slots. When present, sign-up requires choosing one and
	// capacity is tracked per shift (alongside the overall slot count).
	[JsonPropertyName("shifts")]
	public List<EventShift> Shifts { get; set; } = [];

	// Optional questions volunteers answer when signing up.
	[JsonPropertyName("signupQuestions")]
	public List<SignupQuestion> SignupQuestions { get; set; } = [];

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

	[JsonPropertyName("contactUrl")]
	public string? ContactUrl { get; set; }

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

	// ── Crawler / External Source Attribution ─────────────────────────────────
	// Populated only on events that originated from an external source (GivePulse,
	// Eventbrite, VolunteerMatch, JustServe, All for Good, etc.).
	// isCrawled == true is the canonical flag that this event came from outside
	// Arkansas Serve and must display its source attribution.

	/// <summary>True when this event was imported by the event crawler.</summary>
	[JsonPropertyName("isCrawled")]
	public bool IsCrawled { get; set; } = false;

	/// <summary>
	/// Unique key used for deduplication across crawl runs.
	/// Typically the external platform's own event ID prefixed by source name,
	/// e.g. "givepulse:12345" or "eventbrite:987654321".
	/// </summary>
	[JsonPropertyName("crawlerSourceId")]
	public string? CrawlerSourceId { get; set; }

	/// <summary>Human-readable source platform name, e.g. "GivePulse".</summary>
	[JsonPropertyName("crawlerSourceName")]
	public string? CrawlerSourceName { get; set; }

	/// <summary>
	/// Direct URL to the original event listing on the source platform.
	/// Always preserved and displayed as "View original listing" for crawled events.
	/// </summary>
	[JsonPropertyName("crawlerSourceUrl")]
	public string? CrawlerSourceUrl { get; set; }

	/// <summary>Attribution sentence shown on the event card and detail view.</summary>
	[JsonPropertyName("crawlerAttribution")]
	public string? CrawlerAttribution { get; set; }

	/// <summary>UTC timestamp when the crawler last fetched/touched this event.</summary>
	[JsonPropertyName("crawledAt")]
	public DateTime? CrawledAt { get; set; }

}

public class EventShift
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = Guid.NewGuid().ToString();

	[JsonPropertyName("label")]
	public string Label { get; set; } = string.Empty;

	[JsonPropertyName("startDateTime")]
	public DateTime? StartDateTime { get; set; }

	[JsonPropertyName("endDateTime")]
	public DateTime? EndDateTime { get; set; }

	// 0 = unlimited.
	[JsonPropertyName("capacity")]
	public int Capacity { get; set; } = 0;

	[JsonPropertyName("filled")]
	public int Filled { get; set; } = 0;
}

public class SignupQuestion
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = Guid.NewGuid().ToString();

	[JsonPropertyName("label")]
	public string Label { get; set; } = string.Empty;

	// "text" or "choice".
	[JsonPropertyName("type")]
	public string Type { get; set; } = "text";

	[JsonPropertyName("required")]
	public bool Required { get; set; }

	[JsonPropertyName("options")]
	public List<string> Options { get; set; } = [];
}
