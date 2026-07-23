namespace ArkansasServe.Functions.Functions;

// Single source of truth for the 5-level admin hierarchy and its ranking, used
// across Functions for per-org authorization checks.
//
// The base level is "Member" (rank 0 = no admin rights) — deliberately NOT
// "Student". This is the DO axis (what a person may do). Who a person *is* — a
// K–12 schoolchild — lives on the orthogonal WHO axis as PersonTypes.Student.
// The two were once both the string "Student", which conflated the axes; the
// admin axis was renamed to "Member" to separate them (owner, 2026-07-23).
public static class AdminLevels
{
	public const string Member = "Member";
	public const string EventAdmin = "EventAdmin";
	public const string GroupAdmin = "GroupAdmin";
	public const string OrganizationAdmin = "OrganizationAdmin";
	public const string SuperAdmin = "SuperAdmin";

	private static readonly IReadOnlyDictionary<string, int> Ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
	{
		[Member] = 0,
		[EventAdmin] = 1,
		[GroupAdmin] = 2,
		[OrganizationAdmin] = 3,
		[SuperAdmin] = 4,
	};

	public static int RankOf(string? level) => level != null && Ranks.TryGetValue(level, out var r) ? r : 0;

	public static bool AtLeast(string? level, string required) => RankOf(level) >= RankOf(required);

	// Boundary adapter for the Entra token's legacy `extension_Role`/`roles` claim,
	// which still speaks the old 4-role vocabulary. This is the ONLY place the
	// legacy role names survive — everything downstream uses adminLevel. Retire it
	// once the CIAM user flow emits adminLevel directly.
	//
	// Unknown/absent role folds to the base level (Member). The CIAM flow does not
	// emit "Member", so every ordinary sign-in lands here — this default IS the base
	// level for token-derived users, and it must stay in lockstep with the constant.
	public static string FromLegacyRole(string? role) => role switch
	{
		"PlatformAdmin" => SuperAdmin,
		"SchoolAdmin" => OrganizationAdmin,
		"OrgStaff" => EventAdmin,
		_ => Member,
	};
}
