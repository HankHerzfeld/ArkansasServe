namespace ArkansasServe.Functions.Functions;

// Who a person *is* — orthogonal to AdminLevels (what they may *do*). A person
// can be an AdultVolunteer who is also an OrganizationAdmin, or a Student with
// no elevated rights. Drives which intake fields are required (IntakeValidation)
// and minor-safety handling.
public static class PersonTypes
{
	public const string Student = "Student";
	public const string AdultVolunteer = "AdultVolunteer";
	public const string Staff = "Staff"; // reserved for future use

	public static bool IsValid(string? value) =>
		value is Student or AdultVolunteer or Staff;

	// NOTE: minor status is NOT derived from person type — it is computed from date of
	// birth in IntakeValidation.IsMinor, so a minor can't skip guardian consent by
	// choosing "AdultVolunteer".
}
