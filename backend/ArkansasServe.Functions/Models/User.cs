using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Models;

public class User : CosmosDocument
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("externalId")]
    public string ExternalId { get; set; } = string.Empty;

    [JsonPropertyName("adminLevel")]
    public string AdminLevel { get; set; } = "Student";

    [JsonPropertyName("organizationId")]
    public string? OrganizationId { get; set; }

    [JsonPropertyName("groupIds")]
    public List<string> GroupIds { get; set; } = [];

    [JsonPropertyName("eventAdminEventIds")]
    public List<string> EventAdminEventIds { get; set; } = [];

    // ── Admin oversight (#13) ────────────────────────────────────────────────
    // The admins in THIS org who oversee this volunteer. Many-to-many: a volunteer may be
    // assigned to several admins, and each assignment carries its own notification prefs — so
    // one admin can mute a noisy assignee without affecting another admin's. Empty = unassigned.
    // OrganizationAdmin+ sets who is on the list; each assigned admin controls their own prefs.
    [JsonPropertyName("assignedAdmins")]
    public List<UserAssignment> AssignedAdmins { get; set; } = [];

    [JsonPropertyName("permissions")]
    public Dictionary<string, bool> Permissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Legal name, referenced everywhere (no separate usernames — see #24).
    // DisplayName stays as the rendered value; when First/Last are present it is
    // kept in sync via ComposeName. Legacy records may have only DisplayName.
    [JsonPropertyName("firstName")]
    public string? FirstName { get; set; }

    [JsonPropertyName("lastName")]
    public string? LastName { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    // Who the person is (Student | AdultVolunteer | Staff) — orthogonal to
    // AdminLevel. Null until captured at intake/first login (see #22).
    [JsonPropertyName("personType")]
    public string? PersonType { get; set; }

    // True once the required intake fields for this PersonType are present
    // (computed by IntakeValidation on write). Drives the first-login wizard.
    [JsonPropertyName("profileComplete")]
    public bool ProfileComplete { get; set; }

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("grade")]
    public string? Grade { get; set; }

    [JsonPropertyName("schoolId")]
    public string? SchoolId { get; set; }

    // ── Student intake (minor-safety) ────────────────────────────────────────
    [JsonPropertyName("dateOfBirth")]
    public string? DateOfBirth { get; set; } // ISO yyyy-MM-dd; optional

    [JsonPropertyName("guardianName")]
    public string? GuardianName { get; set; }

    [JsonPropertyName("guardianEmail")]
    public string? GuardianEmail { get; set; }

    [JsonPropertyName("guardianPhone")]
    public string? GuardianPhone { get; set; }

    // Attestation that a guardian consented to participation. Required for Students.
    [JsonPropertyName("guardianConsent")]
    public bool GuardianConsent { get; set; }

    // ── Adult-volunteer intake ───────────────────────────────────────────────
    [JsonPropertyName("affiliation")]
    public string? Affiliation { get; set; } // employer / org affiliation; optional

    [JsonPropertyName("emergencyContactName")]
    public string? EmergencyContactName { get; set; }

    [JsonPropertyName("emergencyContactPhone")]
    public string? EmergencyContactPhone { get; set; }

    // Admin-managed (NOT self-reported at intake): set by an org admin.
    [JsonPropertyName("backgroundCheckStatus")]
    public string? BackgroundCheckStatus { get; set; } // e.g. None | Pending | Cleared

    [JsonPropertyName("backgroundCheckCompletedAt")]
    public string? BackgroundCheckCompletedAt { get; set; } // ISO date

    // This person's state against their org's own credentials — see UserTag.cs. Admin-set,
    // like the background check above.
    //
    // Background check deliberately stays its own field rather than becoming a tag (owner
    // decision): folding it in would mean migrating live user records and rewiring every
    // reader, for no behaviour change. The cost is two ways to express a credential —
    // acceptable, and worth converging once tags have proved out.
    //
    // Per-org for free: a User doc IS per-org (one per person per organization, partitioned by
    // tenantId), so a waiver signed with one org says nothing about another. That is correct,
    // and it is also why gating a cross-org registration is a genuinely open question — the
    // registrant has no doc in the event's org to carry its tags.
    [JsonPropertyName("tags")]
    public List<UserTagState> Tags { get; set; } = [];

    // ── Terms & Privacy acceptance ───────────────────────────────────────────
    // The version of the Terms/Privacy documents this person accepted, and when.
    // Stored as the version string rather than a bool so that re-issuing the documents
    // re-prompts everyone (see PolicyVersions). AcceptedPolicyAt is stamped by the server
    // on acceptance — never taken from the client, since it is the evidence that consent
    // was given at a particular time.
    [JsonPropertyName("acceptedPolicyVersion")]
    public string? AcceptedPolicyVersion { get; set; }

    [JsonPropertyName("acceptedPolicyAt")]
    public DateTime? AcceptedPolicyAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "active";

    [JsonPropertyName("isDemoUser")]
    public bool IsDemoUser { get; set; }

    [JsonPropertyName("demoUserType")]
    public string? DemoUserType { get; set; }

    [JsonPropertyName("totalApprovedHours")]
    public double TotalApprovedHours { get; set; } = 0;

    // Managed volunteer: created by an admin, no login yet. On first sign-in with
    // a matching email, the record is adopted (IsManaged cleared, ExternalId set).
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("managedByUserId")]
    public string? ManagedByUserId { get; set; }

    // True when the person added this membership themselves via the public org
    // directory (a self-service Student membership), as opposed to an admin- or
    // token-provisioned one. Lets self-join be reversible without exposing a way
    // to drop admin memberships.
    [JsonPropertyName("selfJoined")]
    public bool SelfJoined { get; set; }

    // Single rendering rule for a person's name (no usernames). Prefers the
    // structured First/Last; falls back to any existing DisplayName, then email.
    public static string ComposeName(string? first, string? last, string? fallback = null)
    {
        var name = string.Join(' ', new[] { first?.Trim(), last?.Trim() }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        return string.IsNullOrWhiteSpace(name) ? (fallback?.Trim() ?? string.Empty) : name;
    }
}

/// <summary>
/// One admin's oversight of one volunteer (#13). Lives in the volunteer's
/// <see cref="User.AssignedAdmins"/> list. The notification flags are the assigned admin's own
/// per-volunteer preference — the assignment record IS the per-assignment pref store.
/// </summary>
public class UserAssignment
{
    /// <summary>The overseeing admin's user id (an EventAdmin+ member of the same org).</summary>
    [JsonPropertyName("adminId")]
    public string AdminId { get; set; } = string.Empty;

    /// <summary>Notify this admin when the volunteer logs service hours. Default on.</summary>
    [JsonPropertyName("notifyOnHours")]
    public bool NotifyOnHours { get; set; } = true;

    /// <summary>Notify this admin when the volunteer's logged hours need approval. Default on.</summary>
    [JsonPropertyName("notifyOnApproval")]
    public bool NotifyOnApproval { get; set; } = true;
}