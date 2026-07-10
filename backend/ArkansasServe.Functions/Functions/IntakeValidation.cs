using System.Globalization;
using ArkansasServe.Functions.Models;

namespace ArkansasServe.Functions.Functions;

// Required-field policy for user intake (#23). Date of birth is required for everyone
// and is the AUTHORITATIVE source of minor status — guardian consent is gated on
// computed age, NOT on the self-declared person type (a minor cannot skip consent by
// picking "AdultVolunteer"). PersonType still drives the non-safety fields (grade vs
// emergency contact). Background-check fields are admin-managed and never required here.
public static class IntakeValidation
{
	public const int MinorAgeThreshold = 18;

	/// <summary>Age in whole years from an ISO date string as of <paramref name="asOf"/>,
	/// or null if missing, unparseable, or in the future.</summary>
	public static int? TryComputeAge(string? dateOfBirth, DateTime asOf)
	{
		if (string.IsNullOrWhiteSpace(dateOfBirth)) return null;
		if (!DateTime.TryParse(dateOfBirth, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dob))
			return null;

		var age = asOf.Year - dob.Year;
		if (dob.Date > asOf.Date.AddYears(-age)) age--;
		return age < 0 ? null : age; // negative => future date => treat as invalid
	}

	/// <summary>True only when a valid DOB proves the person is under the threshold.
	/// A missing/invalid DOB is NOT treated as adult — the profile is incomplete until
	/// a valid DOB is supplied, at which point this is re-evaluated.</summary>
	public static bool IsMinor(User user) =>
		TryComputeAge(user.DateOfBirth, DateTime.UtcNow) is int age && age < MinorAgeThreshold;

	// Returns the human-readable labels of the required fields still missing.
	// An empty list means the profile is complete.
	public static IReadOnlyList<string> MissingRequiredFields(User user)
	{
		var missing = new List<string>();

		if (string.IsNullOrWhiteSpace(user.FirstName)) missing.Add("first name");
		if (string.IsNullOrWhiteSpace(user.LastName)) missing.Add("last name");

		if (!PersonTypes.IsValid(user.PersonType))
		{
			missing.Add("person type");
			return missing; // can't check type-specific fields without a valid type
		}

		// DOB required for all — it determines minor status below.
		var age = TryComputeAge(user.DateOfBirth, DateTime.UtcNow);
		if (age == null) missing.Add("date of birth");

		// Minor-safety gate: anyone under 18 (by DOB) needs guardian consent, whatever
		// their self-declared person type. Only enforced once a valid DOB proves it.
		if (age is int a && a < MinorAgeThreshold)
		{
			if (string.IsNullOrWhiteSpace(user.GuardianName)) missing.Add("guardian name");
			if (string.IsNullOrWhiteSpace(user.GuardianEmail) && string.IsNullOrWhiteSpace(user.GuardianPhone))
				missing.Add("guardian email or phone");
			if (!user.GuardianConsent) missing.Add("guardian consent");
		}

		// Person-type-specific (non-safety) fields.
		if (string.Equals(user.PersonType, PersonTypes.Student, StringComparison.OrdinalIgnoreCase))
		{
			if (string.IsNullOrWhiteSpace(user.Grade)) missing.Add("grade");
		}
		else // AdultVolunteer / Staff
		{
			if (string.IsNullOrWhiteSpace(user.EmergencyContactName)) missing.Add("emergency contact name");
			if (string.IsNullOrWhiteSpace(user.EmergencyContactPhone)) missing.Add("emergency contact phone");
		}

		return missing;
	}

	public static bool IsComplete(User user) => MissingRequiredFields(user).Count == 0;
}
