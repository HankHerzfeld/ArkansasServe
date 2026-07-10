using ArkansasServe.Functions.Models;

namespace ArkansasServe.Functions.Functions;

// Required-field policy for user intake (#23), keyed by PersonType. Kept in one
// place so the first-login wizard, the admin add-user form, and the API all agree
// on what "complete" means. Background-check fields are admin-managed, NOT part of
// self-intake, so they are never required here.
public static class IntakeValidation
{
	// Returns the human-readable labels of the required fields still missing.
	// An empty list means the profile is complete for its PersonType.
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

		if (PersonTypes.IsMinorType(user.PersonType))
		{
			if (string.IsNullOrWhiteSpace(user.Grade)) missing.Add("grade");
			if (string.IsNullOrWhiteSpace(user.GuardianName)) missing.Add("guardian name");
			if (string.IsNullOrWhiteSpace(user.GuardianEmail) && string.IsNullOrWhiteSpace(user.GuardianPhone))
				missing.Add("guardian email or phone");
			if (!user.GuardianConsent) missing.Add("guardian consent");
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
