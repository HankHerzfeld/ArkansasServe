using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Models;

// ── Shared base ──────────────────────────────────────────────────────────────
public abstract class CosmosDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// ── Tenant (schools + community orgs) ────────────────────────────────────────
public class Tenant : CosmosDocument
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;   // "school" | "organization" | "jdc"

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("ssoDomain")]
    public string? SsoDomain { get; set; }             // e.g. "littlerockschools.org" for M365 federation

    [JsonPropertyName("googleWorkspaceDomain")]
    public string? GoogleWorkspaceDomain { get; set; }

    [JsonPropertyName("contactEmail")]
    public string? ContactEmail { get; set; }

    [JsonPropertyName("contactPhone")]
    public string? ContactPhone { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("logoUrl")]
    public string? LogoUrl { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "active";     // "active" | "suspended" | "pending"

    [JsonPropertyName("contractStartDate")]
    public DateTime? ContractStartDate { get; set; }
}

// ── User ─────────────────────────────────────────────────────────────────────
public class User : CosmosDocument
{
    [JsonPropertyName("tenantId")]                     // PARTITION KEY
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("externalId")]                   // Entra object ID
    public string ExternalId { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;  // "Student" | "OrgStaff" | "SchoolAdmin" | "PlatformAdmin"

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("grade")]
    public string? Grade { get; set; }                 // for students

    [JsonPropertyName("schoolId")]
    public string? SchoolId { get; set; }              // for students — which school they belong to

    [JsonPropertyName("status")]
    public string Status { get; set; } = "active";

    [JsonPropertyName("totalApprovedHours")]
    public double TotalApprovedHours { get; set; } = 0;
}

// ── Event ─────────────────────────────────────────────────────────────────────
public class Event : CosmosDocument
{
    [JsonPropertyName("organizationId")]               // PARTITION KEY
    public string OrganizationId { get; set; } = string.Empty;

    [JsonPropertyName("organizationName")]             // denormalized for display
    public string OrganizationName { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("startDateTime")]
    public DateTime StartDateTime { get; set; }

    [JsonPropertyName("endDateTime")]
    public DateTime EndDateTime { get; set; }

    [JsonPropertyName("maxSlots")]
    public int MaxSlots { get; set; } = 0;            // 0 = unlimited

    [JsonPropertyName("currentSlots")]
    public int CurrentSlots { get; set; } = 0;

    [JsonPropertyName("hoursValue")]
    public double HoursValue { get; set; }            // hours credited to students who attend

    [JsonPropertyName("status")]
    public string Status { get; set; } = "Open";      // "Open" | "Full" | "Cancelled" | "Completed"

    [JsonPropertyName("eligibleSchoolIds")]
    public List<string> EligibleSchoolIds { get; set; } = [];  // empty = open to all schools

    [JsonPropertyName("photoUrl")]
    public string? PhotoUrl { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }              // e.g. "Environmental", "Food Bank", "Tutoring"

    [JsonPropertyName("createdByUserId")]
    public string CreatedByUserId { get; set; } = string.Empty;
}

// ── EventRegistration ─────────────────────────────────────────────────────────
public class EventRegistration : CosmosDocument
{
    [JsonPropertyName("eventId")]                      // PARTITION KEY
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("studentName")]                  // denormalized for org check-in view
    public string StudentName { get; set; } = string.Empty;

    [JsonPropertyName("schoolId")]
    public string SchoolId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "Registered"; // "Registered" | "CheckedIn" | "NoShow" | "Cancelled"

    [JsonPropertyName("checkedInAt")]
    public DateTime? CheckedInAt { get; set; }
}

// ── ServiceLog ────────────────────────────────────────────────────────────────
public class ServiceLog : CosmosDocument
{
    [JsonPropertyName("studentId")]                    // PARTITION KEY
    public string StudentId { get; set; } = string.Empty;

    [JsonPropertyName("studentName")]                  // denormalized
    public string StudentName { get; set; } = string.Empty;

    [JsonPropertyName("schoolId")]
    public string SchoolId { get; set; } = string.Empty;

    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("eventTitle")]                   // denormalized
    public string EventTitle { get; set; } = string.Empty;

    [JsonPropertyName("organizationId")]
    public string OrganizationId { get; set; } = string.Empty;

    [JsonPropertyName("organizationName")]             // denormalized
    public string OrganizationName { get; set; } = string.Empty;

    [JsonPropertyName("hoursLogged")]
    public double HoursLogged { get; set; }

    [JsonPropertyName("serviceDate")]
    public DateTime ServiceDate { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "Pending";   // "Pending" | "Approved" | "Rejected"

    [JsonPropertyName("submittedByUserId")]
    public string SubmittedByUserId { get; set; } = string.Empty;

    [JsonPropertyName("reviewedByUserId")]
    public string? ReviewedByUserId { get; set; }

    [JsonPropertyName("reviewNote")]
    public string? ReviewNote { get; set; }

    [JsonPropertyName("reviewedAt")]
    public DateTime? ReviewedAt { get; set; }

    [JsonPropertyName("verificationDocUrl")]
    public string? VerificationDocUrl { get; set; }
}

// ── PendingApproval ────────────────────────────────────────────────────────────
public class PendingApproval : CosmosDocument
{
    [JsonPropertyName("schoolId")]                     // PARTITION KEY
    public string SchoolId { get; set; } = string.Empty;

    [JsonPropertyName("serviceLogId")]
    public string ServiceLogId { get; set; } = string.Empty;

    [JsonPropertyName("studentId")]
    public string StudentId { get; set; } = string.Empty;

    [JsonPropertyName("studentName")]
    public string StudentName { get; set; } = string.Empty;

    [JsonPropertyName("organizationName")]
    public string OrganizationName { get; set; } = string.Empty;

    [JsonPropertyName("eventTitle")]
    public string EventTitle { get; set; } = string.Empty;

    [JsonPropertyName("hoursLogged")]
    public double HoursLogged { get; set; }

    [JsonPropertyName("serviceDate")]
    public DateTime ServiceDate { get; set; }
}

// ── Notification ──────────────────────────────────────────────────────────────
public class Notification : CosmosDocument
{
    [JsonPropertyName("userId")]                       // PARTITION KEY
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;  // "HoursApproved" | "HoursRejected" | "EventReminder"

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("isRead")]
    public bool IsRead { get; set; } = false;

    [JsonPropertyName("relatedId")]
    public string? RelatedId { get; set; }             // serviceLogId or eventId
}
