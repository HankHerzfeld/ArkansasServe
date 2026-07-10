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

	// Minors are handled as Students (guardian consent, tighter exposure). Staff
	// and AdultVolunteers are adults.
	public static bool IsMinorType(string? value) =>
		string.Equals(value, Student, StringComparison.OrdinalIgnoreCase);
}
