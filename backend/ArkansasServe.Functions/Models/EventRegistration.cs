using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Models;

public class EventRegistration : CosmosDocument
{
	[JsonPropertyName("eventId")]
	public string EventId { get; set; } = string.Empty;

	// The registrant's Entra object id. Present only for people who have actually signed in:
	// an admin-created MANAGED volunteer has no account, so their User doc's ExternalId is
	// string.Empty and so is this. Prefer MemberId below to identify a registrant.
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
	// Nullable because records written before this field existed do not carry it; reads accept
	// either key until the backfill is confirmed complete (see the callers).
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
	/// True when this registration is the given person's own.
	///
	/// Matches on EITHER key: <paramref name="memberId"/> (canonical) or
	/// <paramref name="externalId"/> (on rows written before MemberId existed). The legacy arm
	/// is what keeps un-backfilled rows working; it can be dropped once every row carries a
	/// memberId.
	///
	/// An empty id never matches, deliberately: <c>User.ExternalId</c> defaults to
	/// <c>string.Empty</c>, so comparing on an empty value would make every accountless
	/// registrant look like the same person.
	/// </summary>
	public bool BelongsTo(string? externalId, string? memberId) =>
		(!string.IsNullOrEmpty(memberId)
			&& string.Equals(MemberId, memberId, StringComparison.OrdinalIgnoreCase))
		|| (!string.IsNullOrEmpty(externalId)
			&& string.Equals(UserId, externalId, StringComparison.OrdinalIgnoreCase));
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
