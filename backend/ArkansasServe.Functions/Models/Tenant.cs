using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Models;

public class Tenant : CosmosDocument
{
	[JsonPropertyName("type")]
	public string Type { get; set; } = string.Empty;

	[JsonPropertyName("name")]
	public string Name { get; set; } = string.Empty;

	[JsonPropertyName("ssoDomain")]
	public string? SsoDomain { get; set; }

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
	public string Status { get; set; } = "active";

	[JsonPropertyName("rbacEnabled")]
	public bool RbacEnabled { get; set; } = true;

	// When true (default), GroupAdmins may add managed volunteers organization-wide,
	// not just within their own groups.
	[JsonPropertyName("allowGroupAdminAddVolunteers")]
	public bool AllowGroupAdminAddVolunteers { get; set; } = true;

	[JsonPropertyName("groups")]
	public List<TenantGroup> Groups { get; set; } = [];

	[JsonPropertyName("eventScopeRules")]
	public List<EventScopeRule> EventScopeRules { get; set; } = [];

	[JsonPropertyName("contractStartDate")]
	public DateTime? ContractStartDate { get; set; }
}

public class TenantGroup
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = Guid.NewGuid().ToString();

	[JsonPropertyName("name")]
	public string Name { get; set; } = string.Empty;

	[JsonPropertyName("status")]
	public string Status { get; set; } = "active";

	[JsonPropertyName("organizationId")]
	public string OrganizationId { get; set; } = string.Empty;
}

public class EventScopeRule
{
	[JsonPropertyName("eventId")]
	public string EventId { get; set; } = string.Empty;

	[JsonPropertyName("groupId")]
	public string? GroupId { get; set; }

	[JsonPropertyName("organizationId")]
	public string OrganizationId { get; set; } = string.Empty;
}
