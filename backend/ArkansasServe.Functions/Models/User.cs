using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Models;

public class User : CosmosDocument
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("externalId")]
    public string ExternalId { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

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

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("grade")]
    public string? Grade { get; set; }

    [JsonPropertyName("schoolId")]
    public string? SchoolId { get; set; }

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
}