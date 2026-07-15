using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Models;

public class Tenant : CosmosDocument
{
	/// <summary>
	/// The org's STYLE — level 1 of the taxonomy. See <see cref="OrgTypes"/>; canonical casing
	/// is Capitalized, but read it through <c>OrgTypes.IsOrganization</c> rather than comparing
	/// directly, because live data predates that decision.
	/// </summary>
	[JsonPropertyName("type")]
	public string Type { get; set; } = string.Empty;

	/// <summary>
	/// What the org DOES — level 2. One of <see cref="ServiceCategories"/>, shared with an
	/// event's Category so the two never drift into separate vocabularies.
	///
	/// Only meaningful for a Community Organization (<see cref="OrgTypes.IsOrganization"/>);
	/// null for schools and courts, which send volunteers rather than provide a service.
	/// Optional even then — an org that has not chosen one is not an error.
	/// </summary>
	[JsonPropertyName("serviceCategory")]
	public string? ServiceCategory { get; set; }

	/// <summary>
	/// Whether the org is faith-based. An ATTRIBUTE, not a category, and deliberately so:
	/// a church running a food pantry is both faith-based and doing food work, so putting
	/// faith in the category list would force it to drop one of two true things — and with
	/// many Arkansas service orgs being churches, that would be a large share of the
	/// directory, not an edge case.
	///
	/// Orthogonal means the filters compose: "faith-based" and "food work" are separate
	/// questions and can be asked together. A denominational `faithAffiliation` is a
	/// deliberate follow-up: it needs an agreed vocabulary, and freeform would fragment into
	/// Baptist/baptist/Southern Baptist exactly as an unmanaged category list would.
	/// </summary>
	[JsonPropertyName("faithBased")]
	public bool FaithBased { get; set; }

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

	// External logo URL (pasted by an admin) — served as-is.
	[JsonPropertyName("logoUrl")]
	public string? LogoUrl { get; set; }

	// Blob name for a logo uploaded into the private org-logos container. When set, read
	// paths sign it into a short-lived display URL (preferred over an external LogoUrl).
	[JsonPropertyName("logoBlobName")]
	public string? LogoBlobName { get; set; }

	// ── Public profile (rendered only when present) ─────────────────────────────
	[JsonPropertyName("description")]
	public string? Description { get; set; }

	[JsonPropertyName("mission")]
	public string? Mission { get; set; }

	[JsonPropertyName("website")]
	public string? Website { get; set; }

	[JsonPropertyName("status")]
	public string Status { get; set; } = "active";

	[JsonPropertyName("rbacEnabled")]
	public bool RbacEnabled { get; set; } = true;

	// When true (default), GroupAdmins may add managed volunteers organization-wide,
	// not just within their own groups.
	[JsonPropertyName("allowGroupAdminAddVolunteers")]
	public bool AllowGroupAdminAddVolunteers { get; set; } = true;

	// When true (default), members may edit their own profile. Set false to lock
	// profiles so only org admins can change them; admins can always self-edit.
	[JsonPropertyName("allowProfileSelfEdit")]
	public bool AllowProfileSelfEdit { get; set; } = true;

	// When true (default), anyone signed in may join this organization themselves from its
	// public page. Set false for an assign-only org: it stays fully visible and browsable,
	// but membership is created BY an admin rather than claimed by the person.
	//
	// "Assign-only" does not mean "closed". Someone whose managed record an admin already
	// created still adopts it on first sign-in — that IS the assign-only path working, so
	// JoinOrg gates only the create-from-nothing step, not adoption (see JoinOrg).
	//
	// Defaults to true so every existing Tenant doc keeps today's behaviour without a
	// backfill: a doc written before this field existed deserialises to true.
	[JsonPropertyName("allowSelfJoin")]
	public bool AllowSelfJoin { get; set; } = true;

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
