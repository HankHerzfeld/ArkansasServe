using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Models;

/// <summary>
/// How an event series repeats. Supplied on create; the server expands it into real Event
/// documents, one per occurrence, sharing a <see cref="Event.SeriesId"/>.
///
/// Deliberately NOT a general iCal RRULE. Every field that could contradict the event's own
/// start is DERIVED from it instead of accepted, so a rule can never disagree with the event
/// it belongs to — the start is always the first occurrence, as in RFC 5545's DTSTART.
///
/// A series must be bounded (<see cref="Until"/> or <see cref="Count"/>): occurrences are
/// materialised up front, and an endless series would need a rolling window plus a scheduled
/// job to keep extending it.
/// </summary>
public class RecurrenceRule
{
	/// <summary>"daily" | "weekly" | "monthly".</summary>
	[JsonPropertyName("frequency")]
	public string Frequency { get; set; } = string.Empty;

	/// <summary>
	/// Weekly only. Days to repeat on, 0=Sunday … 6=Saturday. Defaults to the start's own
	/// weekday. If supplied it MUST contain the start's weekday, otherwise the start would
	/// not be one of its own occurrences.
	/// </summary>
	[JsonPropertyName("daysOfWeek")]
	public List<int>? DaysOfWeek { get; set; }

	/// <summary>
	/// Monthly only. false → "on the 17th of each month" (the start's day-of-month).
	/// true → "the 3rd Saturday of each month" (the start's nth-weekday).
	/// Both are derived from the start, so neither can contradict it.
	/// </summary>
	[JsonPropertyName("byNthWeekday")]
	public bool ByNthWeekday { get; set; }

	/// <summary>
	/// Inclusive end. Compared on the LOCAL calendar day, never as a UTC instant — an
	/// evening event otherwise falls on the following UTC day and a series ends one
	/// occurrence early or late. Mutually exclusive with <see cref="Count"/>.
	/// </summary>
	[JsonPropertyName("until")]
	public DateTime? Until { get; set; }

	/// <summary>Total occurrences INCLUDING the first. Mutually exclusive with Until.</summary>
	[JsonPropertyName("count")]
	public int? Count { get; set; }
}
