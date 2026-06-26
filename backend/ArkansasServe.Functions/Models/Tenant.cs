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

	[JsonPropertyName("contractStartDate")]
	public DateTime? ContractStartDate { get; set; }
}
