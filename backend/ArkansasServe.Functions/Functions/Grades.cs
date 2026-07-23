namespace ArkansasServe.Functions.Functions;

// School grade levels, K–12. Grade is meaningful ONLY for a Student (see PersonTypes) and is
// captured at intake. Kept as a small validated vocabulary so a nonsensical value — the
// "Grade 17" class of bug — can never be stored: the intake wizard offers these as a dropdown,
// and the server folds/validates on write (see UserFunctions) and on completeness
// (see IntakeValidation).
public static class Grades
{
	// Canonical stored forms: "K", then "1".."12".
	public static readonly IReadOnlyList<string> All =
		new[] { "K" }.Concat(Enumerable.Range(1, 12).Select(n => n.ToString())).ToList();

	public static bool IsValid(string? grade) => Normalize(grade) != null;

	/// <summary>
	/// Folds a supplied grade onto its canonical form, or null when it is not a real K–12 grade.
	/// Accepts "K"/"Kindergarten" and 1–12 with or without an ordinal suffix ("10", "10th"), so a
	/// legacy free-text value tidies up rather than being lost; anything else (e.g. "17") → null.
	/// </summary>
	public static string? Normalize(string? grade)
	{
		var g = grade?.Trim();
		if (string.IsNullOrEmpty(g)) return null;
		if (g.Equals("K", StringComparison.OrdinalIgnoreCase)
			|| g.Equals("Kindergarten", StringComparison.OrdinalIgnoreCase)) return "K";
		var digits = new string(g.TakeWhile(char.IsDigit).ToArray());
		return int.TryParse(digits, out var n) && n is >= 1 and <= 12 ? n.ToString() : null;
	}
}
