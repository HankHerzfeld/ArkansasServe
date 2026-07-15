namespace ArkansasServe.Functions.Models;

/// <summary>
/// The single controlled vocabulary for "what kind of service is this" — used for BOTH an
/// organization's <see cref="Tenant.ServiceCategory"/> and an event's <c>Category</c>.
///
/// One list, deliberately. Two overlapping lists is how you end up with "Senior Care" on an
/// event and "elder care" on the org, meaning the same thing and filtering differently — and
/// the events search (PR #70) already indexes category, so any split shows up immediately in
/// the UX. Defined once here and mirrored in frontend/js/taxonomy.js.
///
/// Faith is NOT in this list, and that is the important decision. A church running a food
/// pantry is faith-based AND does food work; forcing one dropdown to hold both facts loses
/// one of them whichever way it is answered — and a large share of Arkansas service orgs are
/// churches, so that is not an edge case. Being faith-based is an ATTRIBUTE
/// (<see cref="Tenant.FaithBased"/>); the categories below are what an org DOES. The two
/// worship entries are for orgs whose service genuinely IS the faith, not for any church.
/// </summary>
public static class ServiceCategories
{
	/// <summary>Offered when an org's own category does not fit; see the proposal flow.</summary>
	public const string Other = "Other";

	public static readonly IReadOnlyList<string> All =
	[
		"Food & Nutrition",
		"Clothing & Basic Needs",
		"Housing & Shelter",
		"Elder Care",
		"Youth & Education",
		"Animal Welfare",
		"Environment & Conservation",
		"Parks & Recreation",
		"Health & Wellness",
		"Disaster Relief",
		// Faith AS A SERVICE — a congregation whose offering is worship or religious
		// education, rather than any org that happens to be faith-based (that is FaithBased).
		"Worship & Congregational Life",
		"Religious Education & Ministry",
		"Community Development",
		"Arts & Culture",
		// Nonpartisan civic work: poll working, voter registration, civic education.
		// Deliberately SEPARATE from the partisan entry below — they are different activities
		// with different implications for a school or court approving the hours, and merging
		// them would deny a school the ability to tell them apart.
		"Civic Engagement & Elections",
		"Political Parties & Campaigns",
		Other,
	];

	/// <summary>
	/// True when the value is one this vocabulary knows. Case-insensitive, and an empty value
	/// is valid: a category is optional, and an org that has not chosen one is not an error.
	/// </summary>
	public static bool IsValid(string? value) =>
		string.IsNullOrWhiteSpace(value) || All.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
}
