using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Models;

public class EventRegistration : CosmosDocument
{
	[JsonPropertyName("eventId")]
	public string EventId { get; set; } = string.Empty;

	// The registrant's Entra object id. Present only for people who have actually signed in:
	// an admin-created MANAGED volunteer has no account, so their User doc's ExternalId is
	// string.Empty and so is this. Kept for audit/display (who signed in) but NO LONGER a
	// matching key — identity is MemberId alone (see BelongsTo). Prefer MemberId to identify a
	// registrant.
	[JsonPropertyName("userId")]
	public string UserId { get; set; } = string.Empty;

	// The registrant's per-org User document id — the canonical identity of a registration.
	//
	// This exists because UserId cannot identify a registrant who has no account. Every roster
	// member has a User doc whether or not they have ever signed in, so a managed volunteer
	// can be registered (by an admin) only if the registration points here.
	//
	// It also closes a latent collision: ExternalId defaults to string.Empty, so keying on
	// UserId would make EVERY accountless registrant share the key "" — the first one would
	// then read as "already registered" and block all the rest.
	//
	// Nullable only for the record type's sake; in practice every row carries it. Reads key on
	// it ALONE as of 2026-07-22 — the legacy externalId fallback was dropped once prod was
	// verified to hold zero registrations without a memberId and every write path was made to
	// guarantee one (the single sign-up path now requires a resolved member; group and walk-in
	// always set it).
	[JsonPropertyName("memberId")]
	public string? MemberId { get; set; }

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

	/// <summary>
	/// True when this registration is the given person's own, keyed solely on the canonical
	/// <paramref name="memberId"/> (their per-org User doc id).
	///
	/// The legacy externalId arm was dropped 2026-07-22, once every registration carried a
	/// memberId (prod verified: zero rows without one) and every write path was made to guarantee
	/// it — the single sign-up path now requires a resolved member, and group and walk-in always
	/// set it. UserId stays on the row for audit/display but is no longer a matching key.
	///
	/// An empty id never matches, deliberately: an unset memberId must collide with nobody, not
	/// with the many accountless registrants — so it matches no one rather than everyone.
	/// </summary>
	public bool BelongsTo(string? memberId) =>
		!string.IsNullOrEmpty(memberId)
			&& string.Equals(MemberId, memberId, StringComparison.OrdinalIgnoreCase);
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
