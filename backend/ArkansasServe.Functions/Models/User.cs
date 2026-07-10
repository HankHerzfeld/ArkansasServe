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