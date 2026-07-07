namespace ArkansasServe.Functions.Functions;

// Single source of truth for the 5-level admin hierarchy and its ranking, used
// across Functions for per-org authorization checks.
public static class AdminLevels
{
	public const string Student = "Student";
	public const string EventAdmin = "EventAdmin";
	public const string GroupAdmin = "GroupAdmin";
	public const string OrganizationAdmin = "OrganizationAdmin";
	public const string SuperAdmin = "SuperAdmin";

	private static readonly IReadOnlyDictionary<string, int> Ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
	{
		[Student] = 0,
		[EventAdmin] = 1,
		[GroupAdmin] = 2,
		[OrganizationAdmin] = 3,
		[SuperAdmin] = 4,
	};

	public static int RankOf(string? level) => level != null && Ranks.TryGetValue(level, out var r) ? r : 0;

	public static bool AtLeast(string? level, string required) => RankOf(level) >= RankOf(required);

	// Floor mapping from the legacy role when a precise adminLevel isn't available.
	public static string FromLegacyRole(string? role) => role switch
	{
		"PlatformAdmin" => SuperAdmin,
		"SchoolAdmin" => OrganizationAdmin,
		"OrgStaff" => EventAdmin,
		_ => Student,
	};

	public static string ToLegacyRole(string? adminLevel) => adminLevel switch
	{
		SuperAdmin => "PlatformAdmin",
		OrganizationAdmin => "SchoolAdmin",
		GroupAdmin => "OrgStaff",
		EventAdmin => "OrgStaff",
		_ => "Student",
	};
}
