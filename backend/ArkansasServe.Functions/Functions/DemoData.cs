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
				// Self-attestable: a waiver is signed by the volunteer, so it can be agreed to at
				// check-in (#19). The other two stay admin-recorded.
				new TenantUserTag { Id = TagWaiver, Label = "Liability waiver", Enforcement = TagEnforcement.BlockCheckIn, ExpiresAfterDays = 365, Status = "active", SelfAttestable = true },
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

		// Adult volunteer — complete (has emergency contacts). Assigned to an EventAdmin (#13
		// oversight) so the assignment/notify flow is represented.
		var adult = Person("demo-adult-complete", OrgAlpha, AdminLevels.Member, "Avery", "Adult", PersonTypes.AdultVolunteer, AdultDob);
		adult.EmergencyContactName = "Sam Kin"; adult.EmergencyContactPhone = "501-555-0101"; adult.Affiliation = "Acme Co.";
		adult.AssignedAdmins.Add(new UserAssignment { AdminId = "demo-eventadmin-1" });
		Add(adult);

		// Adult volunteer — INCOMPLETE (missing emergency contacts → intake wizard).
		Add(Person("demo-adult-incomplete", OrgAlpha, AdminLevels.Member, "Ingrid", "Incomplete", PersonTypes.AdultVolunteer, AdultDob));

		// Student minor — guardian consent GRANTED (see guardian doc). Complete profile.
		// Assigned to the OrganizationAdmin (#13 oversight).
		var minorGranted = Minor("demo-minor-granted", OrgAlpha, "Mira", "Granted", grade: "10");
		minorGranted.AssignedAdmins.Add(new UserAssignment { AdminId = "demo-organizationadmin-1" });
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

	// ── Activity: events / registrations / service logs ───────────────────────────
	// Seeded under the demo tenants (IsDemo throughout) so a SuperAdmin can exercise every flow
	// end-to-end by "Act as"-ing a persona. Stable ids so a reset (delete-all-demo then recreate)
	// is idempotent and the registrations/logs below can reference the events deterministically.
	public const string EvtHosted   = "demo-event-hosted";   // registerable, shifts + a question
	public const string EvtExternal = "demo-event-external"; // one-sided external listing
	public const string EvtSeries1  = "demo-event-series-1"; // two occurrences share a seriesId
	public const string EvtSeries2  = "demo-event-series-2";
	public const string EvtGuardian = "demo-event-guardian"; // School event FLAGGED for fresh guardian approval
	public const string EvtOvernight = "demo-event-overnight"; // spans 2 local days -> carve-out by computation
	public const string EvtTagGated = "demo-event-tag";      // Faith org — its blockReg/blockCheckIn tags apply
	public const string EvtPast     = "demo-event-past";     // archived, for post-event hour logging
	public const string EvtFull     = "demo-event-full";     // maxSlots reached

	private const string ShiftMorning = "demo-shift-morning";
	private const string ShiftAfternoon = "demo-shift-afternoon";

	private const string NameAlpha  = "Demo Community Organization (Alpha)";
	private const string NameFaith  = "Demo Faith Organization";
	private const string NameSchool = "Demo High School";

	public static List<Event> BuildEvents()
	{
		var now = DateTime.UtcNow;
		Event Ev(string id, string org, string orgName, string title, DateTime start, double hours, string category) => new()
		{
			Id = id, OrganizationId = org, OrganizationName = orgName, Title = title,
			Description = "Seeded demo event — safe to reset.", Location = "1400 W Markham St, Little Rock, AR 72201",
			Zip = "72201", City = "Little Rock", County = "Pulaski",
			StartDateTime = start, EndDateTime = start.AddHours(3), HoursValue = hours,
			Status = "Open", Category = category, IsDemo = true, CreatedByUserId = "demo-organizationadmin-1",
		};

		var hosted = Ev(EvtHosted, OrgAlpha, NameAlpha, "Demo Food Drive (hosted, shifts)", now.AddDays(7), 3, "Food & Nutrition");
		hosted.MaxSlots = 10;
		hosted.Shifts =
		[
			new EventShift { Id = ShiftMorning, Label = "Morning", StartDateTime = now.AddDays(7), EndDateTime = now.AddDays(7).AddHours(3), Capacity = 5 },
			new EventShift { Id = ShiftAfternoon, Label = "Afternoon", StartDateTime = now.AddDays(7).AddHours(3), EndDateTime = now.AddDays(7).AddHours(6), Capacity = 5 },
		];
		hosted.SignupQuestions = [new SignupQuestion { Id = "demo-q-shirt", Label = "T-shirt size?", Type = "choice", Required = true, Options = ["S", "M", "L"] }];

		var external = Ev(EvtExternal, OrgAlpha, NameAlpha, "Demo External Listing", now.AddDays(10), 0, "Community Development");
		external.ListingType = "external";
		external.HostOrganizationName = "Outside Partner Inc.";
		external.HostOrganizationUrl = "https://example.org/volunteer";

		var series1 = Ev(EvtSeries1, OrgAlpha, NameAlpha, "Demo Weekly Tutoring (occurrence 1)", now.AddDays(3), 2, "Youth & Education");
		series1.SeriesId = "demo-series-weekly";
		var series2 = Ev(EvtSeries2, OrgAlpha, NameAlpha, "Demo Weekly Tutoring (occurrence 2)", now.AddDays(10), 2, "Youth & Education");
		series2.SeriesId = "demo-series-weekly";

		var guardian = Ev(EvtGuardian, School, NameSchool, "Demo Flagged Trip (fresh guardian approval)", now.AddDays(14), 5, "Youth & Education");
		guardian.RequiresFreshGuardianApproval = true;

		// The OTHER carve-out trigger: spans more than one Central local day, so fresh approval is
		// required by computation even though the flag is off. Ends a full day after it starts.
		var overnight = Ev(EvtOvernight, School, NameSchool, "Demo Overnight Lock-In (spans two days)", now.AddDays(16), 6, "Youth & Education");
		overnight.EndDateTime = overnight.StartDateTime.AddDays(1);

		var tagGated = Ev(EvtTagGated, OrgFaith, NameFaith, "Demo Faith Service (tag-gated)", now.AddDays(8), 2, "Worship & Congregational Life");

		var past = Ev(EvtPast, OrgAlpha, NameAlpha, "Demo Past Event (archived)", now.AddDays(-10), 4, "Food & Nutrition");
		past.Status = "Archived";
		past.ArchivedAt = now.AddDays(-9);

		var full = Ev(EvtFull, OrgAlpha, NameAlpha, "Demo Full Event (no spots left)", now.AddDays(5), 2, "Health & Wellness");
		full.MaxSlots = 2;
		full.CurrentSlots = 2;

		return [hosted, external, series1, series2, guardian, overnight, tagGated, past, full];
	}

	public static List<EventRegistration> BuildRegistrations()
	{
		var now = DateTime.UtcNow;
		EventRegistration Reg(string id, string eventId, string memberId, string extId, string name, string regOrg, string eventOrg, string status) => new()
		{
			Id = id, EventId = eventId, MemberId = memberId, UserId = extId, StudentName = name,
			SchoolId = regOrg, OrganizationId = eventOrg, Status = status, IsDemo = true,
		};

		var registered = Reg("demo-reg-registered", EvtHosted, "demo-minor-granted", "demo-minor-granted", "Mira Granted", OrgAlpha, OrgAlpha, "Registered");
		registered.ShiftId = ShiftMorning;

		// Cross-org: a Beta person signs up for an Alpha event (registrant org ≠ event org).
		var crossOrg = Reg("demo-reg-crossorg", EvtHosted, "demo-crossorg-1-beta", "demo-crossorg-1", "Demo Cross-Org 1 (admin in Beta)", OrgBeta, OrgAlpha, "Registered");
		crossOrg.ShiftId = ShiftAfternoon;

		var cancelled = Reg("demo-reg-cancelled", EvtHosted, "demo-student-1", "demo-student-1", "Demo Student 1 (self-joined)", OrgAlpha, OrgAlpha, "Cancelled");

		// A group registration (two people signed up together by an admin).
		var group1 = Reg("demo-reg-group-1", EvtHosted, "demo-bg-cleared", "demo-bg-cleared", "Cleo Cleared", OrgAlpha, OrgAlpha, "Registered");
		group1.ShiftId = ShiftMorning;
		var group2 = Reg("demo-reg-group-2", EvtHosted, "demo-adult-complete", "demo-adult-complete", "Avery Adult", OrgAlpha, OrgAlpha, "Registered");
		group2.ShiftId = ShiftMorning;

		// Checked-in at the past event — enables the service log below.
		var checkedIn = Reg("demo-reg-checkedin", EvtPast, "demo-minor-granted", "demo-minor-granted", "Mira Granted", OrgAlpha, OrgAlpha, "Registered");
		checkedIn.CheckedInAt = now.AddDays(-10);

		// Two registrations that fill the full event.
		var full1 = Reg("demo-reg-full-1", EvtFull, "demo-adult-complete", "demo-adult-complete", "Avery Adult", OrgAlpha, OrgAlpha, "Registered");
		var full2 = Reg("demo-reg-full-2", EvtFull, "demo-bg-pending", "demo-bg-pending", "Peta Pending", OrgAlpha, OrgAlpha, "Registered");

		return [registered, crossOrg, cancelled, group1, group2, checkedIn, full1, full2];
	}

	public static List<ServiceLog> BuildServiceLogs()
	{
		var now = DateTime.UtcNow;
		ServiceLog Log(string id, string studentId, string studentName, string school, string org, string orgName, string eventId, string eventTitle, double hours, string status) => new()
		{
			Id = id, StudentId = studentId, StudentName = studentName, SchoolId = school,
			OrganizationId = org, OrganizationName = orgName, EventId = eventId, EventTitle = eventTitle,
			HoursLogged = hours, ServiceDate = now.AddDays(-10), Status = status,
			SubmittedByUserId = studentId, IsDemo = true,
		};

		// Pending: credited to the demo School, from a review-required org (Faith), so it sits in
		// the school's approval queue.
		var pending = Log("demo-log-pending", "demo-school-minor-ok", "Sol SchoolOk", School, OrgFaith, NameFaith, EvtPast, "Demo Past Event (archived)", 4, "Pending");

		// Approved: already reviewed by the school admin.
		var approved = Log("demo-log-approved", "demo-school-minor-ok", "Sol SchoolOk", School, OrgAlpha, NameAlpha, EvtPast, "Demo Past Event (archived)", 2, "Approved");
		approved.ReviewedByUserId = "demo-school-admin";
		approved.ReviewedAt = now.AddDays(-1);

		return [pending, approved];
	}
}
