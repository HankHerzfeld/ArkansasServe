using ArkansasServe.Functions.Models;

namespace ArkansasServe.Functions.Functions;

// The seeded demo network (test fixtures). Every tenant here is IsDemo, every user IsDemoUser,
// every guardian IsDemo — so the isolation filters (see MembershipFunctions / EventFunctions) keep
// the whole network invisible to real users while a SuperAdmin sees it and can "Act as" any
// persona. The point is COVERAGE: one fixture for every org type/attribute and every user use case,
// so a SuperAdmin can exercise every flow end-to-end without touching real data.
//
// Kept out of AdminFunctions to stop that file bloating; the demo-data reset endpoint calls these.
// All builders are pure (no I/O) and idempotent by construction — every doc has a stable id, so a
// reset overwrites rather than duplicating.
public static class DemoData
{
	// ── Tenant ids ──────────────────────────────────────────────────────────────
	public const string OrgAlpha    = "demo-org-alpha";     // Community Org — home org for personas
	public const string OrgBeta     = "demo-org-beta";      // Community Org — secondary (cross-org)
	public const string OrgFaith    = "demo-org-faith";     // Community Org — faith-based + tags + branding
	public const string OrgAssign   = "demo-org-assign";    // Community Org — assign-only (no self-join)
	public const string School      = "demo-school";        // School — approval policy + guardian consent
	public const string Jdc         = "demo-jdc";           // JDC — guardian consent

	// ── Faith-org credential/tag ids (referenced by user tag states below) ────────
	public const string TagWaiver   = "demo-tag-waiver";    // blockCheckIn, expires
	public const string TagBgCheck  = "demo-tag-bgcheck";   // advisory
	public const string TagTraining = "demo-tag-training";  // blockRegistration

	// Minor/adult birthdates, computed so the personas stay the right side of 18.
	private static string MinorDob => DateTime.UtcNow.AddYears(-15).ToString("yyyy-MM-dd");
	private static string AdultDob => DateTime.UtcNow.AddYears(-30).ToString("yyyy-MM-dd");

	// ── Tenants ──────────────────────────────────────────────────────────────────
	public static List<Tenant> BuildTenants() =>
	[
		new Tenant
		{
			Id = OrgAlpha, Type = OrgTypes.Organization, Name = "Demo Community Organization (Alpha)",
			Description = "Seeded demo org — home org for the demo personas. Safe to reset.",
			ContactEmail = "demo.alpha@arkansasserve.local", ServiceCategory = "Food & Nutrition",
			Status = "active", IsDemo = true,
		},
		new Tenant
		{
			Id = OrgBeta, Type = OrgTypes.Organization, Name = "Demo Partner Organization (Beta)",
			Description = "Seeded demo org — secondary org for the cross-org persona. Safe to reset.",
			ContactEmail = "demo.beta@arkansasserve.local", ServiceCategory = "Youth & Education",
			Status = "active", IsDemo = true,
		},
		new Tenant
		{
			Id = OrgFaith, Type = OrgTypes.Organization, Name = "Demo Faith Organization",
			Description = "Seeded demo org — faith-based, defines credentials/tags, and carries branding.",
			ContactEmail = "demo.faith@arkansasserve.local", ServiceCategory = "Worship & Congregational Life",
			FaithBased = true, Status = "active", IsDemo = true,
			Branding = new TenantBranding { PrimaryColor = "#6d28d9" }, // purple — exercises palette override
			UserTags =
			[
				new TenantUserTag { Id = TagWaiver, Label = "Liability waiver", Enforcement = TagEnforcement.BlockCheckIn, ExpiresAfterDays = 365, Status = "active" },
				new TenantUserTag { Id = TagBgCheck, Label = "Background check", Enforcement = TagEnforcement.Advisory, Status = "active" },
				new TenantUserTag { Id = TagTraining, Label = "Safety training", Enforcement = TagEnforcement.BlockRegistration, Status = "active" },
			],
		},
		new Tenant
		{
			Id = OrgAssign, Type = OrgTypes.Organization, Name = "Demo Assign-Only Organization",
			Description = "Seeded demo org — assign-only: browsable, but an admin adds members (no self-join).",
			ContactEmail = "demo.assign@arkansasserve.local", ServiceCategory = "Housing & Shelter",
			AllowSelfJoin = false, Status = "active", IsDemo = true,
		},
		new Tenant
		{
			Id = School, Type = OrgTypes.School, Name = "Demo High School",
			Description = "Seeded demo School — requires guardian consent; has an approval policy.",
			ContactEmail = "demo.school@arkansasserve.local",
			RequireGuardianConsent = true, Status = "active", IsDemo = true,
			// Default: hours are reviewed. Preapprove one org and one category to exercise
			// most-specific-wins resolution (see ApprovalPolicy.Resolve).
			ApprovalPolicy = new ApprovalPolicy
			{
				Default = ApprovalPolicies.ApprovalRequired,
				ByOrg = { [OrgAlpha] = ApprovalPolicies.Preapproved },
				ByCategory = { ["Food & Nutrition"] = ApprovalPolicies.Preapproved },
			},
		},
		new Tenant
		{
			Id = Jdc, Type = OrgTypes.Jdc, Name = "Demo Juvenile Court Department",
			Description = "Seeded demo JDC — court-involved youth; requires guardian consent.",
			ContactEmail = "demo.jdc@arkansasserve.local",
			RequireGuardianConsent = true, Status = "active", IsDemo = true,
		},
	];

	// ── Users ────────────────────────────────────────────────────────────────────
	public static List<User> BuildUsers()
	{
		var users = new List<User>();
		void Add(User u) { u.IsDemoUser = true; u.ProfileComplete = IntakeValidation.IsComplete(u); users.Add(u); }

		// Base factory. externalId defaults to id; a SHARED externalId makes one person span orgs.
		static User U(string id, string tenant, string level, string name, string? externalId = null) => new()
		{
			Id = id, ExternalId = externalId ?? id, TenantId = tenant, OrganizationId = tenant,
			AdminLevel = level, DemoUserType = level, DisplayName = name, Email = $"{id}@arkansasserve.local",
		};

		// Adds the structured name + a valid DOB so a persona that SHOULD be complete actually is.
		static User Person(string id, string tenant, string level, string first, string last, string personType, string? dob)
		{
			var u = U(id, tenant, level, $"{first} {last}");
			u.FirstName = first; u.LastName = last; u.PersonType = personType; u.DateOfBirth = dob;
			return u;
		}

		// ── Platform operators (root) ────────────────────────────────────────────
		for (var i = 1; i <= 2; i++)
		{
			var s = U($"demo-superadmin-{i}", TenantIds.Root, AdminLevels.SuperAdmin, $"Demo SuperAdmin {i}");
			s.PersonType = PersonTypes.Staff; // platform operators are Staff, not schoolchildren
			Add(s);
		}

		// ── Alpha (home org): the admin ladder + the person-type/state matrix ─────
		Add(U("demo-organizationadmin-1", OrgAlpha, AdminLevels.OrganizationAdmin, "Demo OrganizationAdmin 1"));
		Add(U("demo-groupadmin-1", OrgAlpha, AdminLevels.GroupAdmin, "Demo GroupAdmin 1"));
		Add(U("demo-eventadmin-1", OrgAlpha, AdminLevels.EventAdmin, "Demo EventAdmin 1"));

		// Staff — intake-exempt (no grade/guardian/emergency required).
		var staff = Person("demo-staff-1", OrgAlpha, AdminLevels.Member, "Dana", "Staff", PersonTypes.Staff, AdultDob);
		Add(staff);

		// Adult volunteer — complete (has emergency contacts).
		var adult = Person("demo-adult-complete", OrgAlpha, AdminLevels.Member, "Avery", "Adult", PersonTypes.AdultVolunteer, AdultDob);
		adult.EmergencyContactName = "Sam Kin"; adult.EmergencyContactPhone = "501-555-0101"; adult.Affiliation = "Acme Co.";
		Add(adult);

		// Adult volunteer — INCOMPLETE (missing emergency contacts → intake wizard).
		Add(Person("demo-adult-incomplete", OrgAlpha, AdminLevels.Member, "Ingrid", "Incomplete", PersonTypes.AdultVolunteer, AdultDob));

		// Student minor — guardian consent GRANTED (see guardian doc). Complete profile.
		var minorGranted = Minor("demo-minor-granted", OrgAlpha, "Mira", "Granted", grade: "10");
		Add(minorGranted);

		// Student minor — guardian consent WITHDRAWN (guardian doc revokes it).
		Add(Minor("demo-minor-withdrawn", OrgAlpha, "Wade", "Withdrawn", grade: "9"));

		// Student minor — guardian consent MISSING (no guardian doc / no consent on file).
		Add(Minor("demo-minor-missing", OrgAlpha, "Noor", "Missing", grade: "11"));

		// Student minor — PROFILE INCOMPLETE (no DOB/grade → intake wizard).
		Add(Person("demo-minor-incomplete", OrgAlpha, AdminLevels.Member, "Piper", "Incomplete", PersonTypes.Student, dob: null));

		// Student who is actually an ADULT (18+) — edge: student personType, adult age.
		Add(StudentAdult("demo-student-adult", OrgAlpha, "Eli", "Eighteen", grade: "12"));

		// Self-joined vs adopted — the A/B for Finding 6 (Leave allowed vs refused).
		var selfJoined = Minor("demo-student-1", OrgAlpha, "Demo", "Student 1 (self-joined)", grade: "10"); selfJoined.SelfJoined = true; Add(selfJoined);
		var adopted = Minor("demo-student-2", OrgAlpha, "Demo", "Student 2 (adopted)", grade: "10"); adopted.SelfJoined = false; Add(adopted);

		// Managed volunteer — created by an admin, NO login yet (IsManaged, empty externalId).
		var managed = U("demo-managed-1", OrgAlpha, AdminLevels.Member, "Morgan Managed");
		managed.ExternalId = string.Empty; managed.IsManaged = true; managed.ManagedByUserId = "demo-organizationadmin-1";
		managed.PersonType = PersonTypes.AdultVolunteer;
		Add(managed);

		// Background-check states (admin-managed) on three adult volunteers.
		Add(WithBgCheck(Person("demo-bg-none", OrgAlpha, AdminLevels.Member, "Bo", "NoCheck", PersonTypes.AdultVolunteer, AdultDob), "None"));
		Add(WithBgCheck(Person("demo-bg-pending", OrgAlpha, AdminLevels.Member, "Peta", "Pending", PersonTypes.AdultVolunteer, AdultDob), "Pending"));
		Add(WithBgCheck(Person("demo-bg-cleared", OrgAlpha, AdminLevels.Member, "Cleo", "Cleared", PersonTypes.AdultVolunteer, AdultDob), "Cleared"));

		// ── Beta (secondary org): the cross-org person (shared externalId) ────────
		Add(U("demo-crossorg-1-alpha", OrgAlpha, AdminLevels.Member, "Demo Cross-Org 1 (volunteer in Alpha)", "demo-crossorg-1"));
		Add(U("demo-crossorg-1-beta", OrgBeta, AdminLevels.OrganizationAdmin, "Demo Cross-Org 1 (admin in Beta)", "demo-crossorg-1"));

		// ── Faith org: admin + tag states (against the faith org's tags) ──────────
		Add(U("demo-faith-admin", OrgFaith, AdminLevels.OrganizationAdmin, "Demo Faith Admin"));
		// Waiver COMPLETE (not expired) — passes the blockCheckIn gate.
		Add(WithTag(Person("demo-faith-waiver-ok", OrgFaith, AdminLevels.Member, "Willa", "WaiverOk", PersonTypes.AdultVolunteer, AdultDob),
			TagWaiver, TagStatuses.Complete, expiresInDays: 200));
		// Waiver PENDING — blocked at check-in until completed.
		Add(WithTag(Person("demo-faith-waiver-pending", OrgFaith, AdminLevels.Member, "Pax", "WaiverPending", PersonTypes.AdultVolunteer, AdultDob),
			TagWaiver, TagStatuses.Pending, expiresInDays: null));
		// Waiver EXPIRED — completed but past expiry, so it no longer reads as current.
		Add(WithTag(Person("demo-faith-waiver-expired", OrgFaith, AdminLevels.Member, "Xena", "WaiverExpired", PersonTypes.AdultVolunteer, AdultDob),
			TagWaiver, TagStatuses.Complete, expiresInDays: -5));
		// Missing the blockRegistration training tag entirely — blocked at sign-up.
		Add(Person("demo-faith-untrained", OrgFaith, AdminLevels.Member, "Uma", "Untrained", PersonTypes.AdultVolunteer, AdultDob));

		// ── Assign-only org: admin + a managed volunteer (the assign-only path) ───
		Add(U("demo-assign-admin", OrgAssign, AdminLevels.OrganizationAdmin, "Demo Assign-Only Admin"));
		var assignManaged = U("demo-assign-managed", OrgAssign, AdminLevels.Member, "Quinn Assigned");
		assignManaged.ExternalId = string.Empty; assignManaged.IsManaged = true; assignManaged.ManagedByUserId = "demo-assign-admin";
		assignManaged.PersonType = PersonTypes.AdultVolunteer;
		Add(assignManaged);

		// ── School: admin + minors (one consented, one consent-required-but-missing) ─
		Add(U("demo-school-admin", School, AdminLevels.OrganizationAdmin, "Demo School Admin"));
		var schoolMinorOk = Minor("demo-school-minor-ok", School, "Sol", "SchoolOk", grade: "11"); schoolMinorOk.SchoolId = School; Add(schoolMinorOk);
		var schoolMinorBlocked = Minor("demo-school-minor-blocked", School, "Bram", "SchoolBlocked", grade: "9"); schoolMinorBlocked.SchoolId = School; Add(schoolMinorBlocked);

		// ── JDC: admin + court-involved youth (minor) ────────────────────────────
		Add(U("demo-jdc-admin", Jdc, AdminLevels.OrganizationAdmin, "Demo JDC Admin"));
		var jdcYouth = Minor("demo-jdc-youth", Jdc, "Jory", "Youth", grade: "10"); jdcYouth.SchoolId = Jdc; Add(jdcYouth);

		return users;

		// A minor Student with the full intake set so the profile is complete AND the guardian
		// attestation is on file — the guardian DOC (below) then models the magic-link consent.
		static User Minor(string id, string tenant, string first, string last, string grade)
		{
			var u = Person(id, tenant, AdminLevels.Member, first, last, PersonTypes.Student, MinorDob);
			u.Grade = grade;
			u.GuardianName = "Guardian of " + first;
			u.GuardianEmail = $"guardian.{id}@arkansasserve.local";
			u.GuardianConsent = true;
			return u;
		}

		static User StudentAdult(string id, string tenant, string first, string last, string grade)
		{
			var u = Person(id, tenant, AdminLevels.Member, first, last, PersonTypes.Student, AdultDob);
			u.Grade = grade;
			return u;
		}

		static User WithBgCheck(User u, string status)
		{
			u.BackgroundCheckStatus = status;
			if (status == "Cleared") u.BackgroundCheckCompletedAt = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
			return u;
		}

		static User WithTag(User u, string tagId, string status, int? expiresInDays)
		{
			u.Tags.Add(new UserTagState
			{
				TagId = tagId,
				Status = status,
				CompletedAt = status == TagStatuses.Complete ? DateTime.UtcNow.AddDays(-10) : null,
				ExpiresAt = expiresInDays.HasValue ? DateTime.UtcNow.AddDays(expiresInDays.Value) : null,
			});
			return u;
		}
	}

	// ── Guardians ─────────────────────────────────────────────────────────────────
	// Keyed by email, linked to the demo minors, with consents in the three states the gate
	// distinguishes. The "missing" minor deliberately has NO guardian doc.
	public static List<Guardian> BuildGuardians()
	{
		Guardian G(string minorId, string org, string minorName, string status) => new()
		{
			Id = "demo-guardian-" + minorId,
			Email = $"guardian.{minorId}@arkansasserve.local",
			Name = "Guardian of " + minorName,
			IsDemo = true,
			Links = [new GuardianLink { MinorUserId = minorId, OrganizationId = org, MinorName = minorName }],
			Consents =
			[
				new GuardianConsent
				{
					MinorUserId = minorId, OrganizationId = org, Status = status,
					GrantedAt = DateTime.UtcNow.AddDays(-20),
					RevokedAt = status == GuardianConsentStatus.Revoked ? DateTime.UtcNow.AddDays(-2) : null,
				},
			],
		};

		return
		[
			G("demo-minor-granted", OrgAlpha, "Mira Granted", GuardianConsentStatus.Granted),
			G("demo-minor-withdrawn", OrgAlpha, "Wade Withdrawn", GuardianConsentStatus.Revoked),
			G("demo-school-minor-ok", School, "Sol SchoolOk", GuardianConsentStatus.Granted),
			// demo-school-minor-blocked and demo-jdc-youth have NO guardian doc → consent missing,
			// which (with RequireGuardianConsent on School/JDC) blocks their registration.
		];
	}
}
