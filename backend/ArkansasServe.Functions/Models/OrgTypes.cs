namespace ArkansasServe.Functions.Models;

/// <summary>
/// An organization's STYLE — the first level of the org taxonomy. What it does (the second
/// level) is <see cref="ServiceCategories"/>.
///
/// Canonical casing is Capitalized. Live data carries both cases: the platform-admin dropdown
/// wrote lowercase ("organization") while seeded/demo orgs were written capitalized
/// ("Organization"), so three of five orgs disagreed with the other two. Nothing branched on
/// the value — it renders as a badge — so it went unnoticed until <see cref="IsOrganization"/>
/// needed to mean something.
///
/// Hence <see cref="IsOrganization"/> compares case-INSENSITIVELY rather than trusting the
/// stored casing. Normalising the data is a tidy-up; not depending on it is the fix. A
/// case-sensitive check here would silently skip every org written the other way, which is
/// exactly the sort of bug that looks like "the field just doesn't work for some orgs".
/// </summary>
public static class OrgTypes
{
	public const string School = "School";
	public const string Jdc = "JDC";
	public const string Organization = "Organization";

	/// <summary>
	/// True for a community organization — the only style a service category applies to.
	/// A school's or a court's "service category" is a question with no good answer: they
	/// send volunteers, they do not distribute clothing. Asking anyway is how "Other" becomes
	/// the most popular value in a taxonomy.
	/// </summary>
	public static bool IsOrganization(string? type) =>
		string.Equals(type?.Trim(), Organization, StringComparison.OrdinalIgnoreCase);

	/// <summary>Folds any stored casing onto the canonical value; unknown values pass through.</summary>
	public static string Normalize(string? type)
	{
		var t = type?.Trim();
		if (string.IsNullOrEmpty(t)) return string.Empty;
		if (string.Equals(t, School, StringComparison.OrdinalIgnoreCase)) return School;
		if (string.Equals(t, Jdc, StringComparison.OrdinalIgnoreCase)) return Jdc;
		if (string.Equals(t, Organization, StringComparison.OrdinalIgnoreCase)) return Organization;
		return t;
	}
}
