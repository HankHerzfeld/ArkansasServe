using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Models;

/// <summary>
/// A School/JDC's rule for whether a student's logged hours auto-count or need review (#12),
/// decided by the ORG that ran the event and/or the event's service CATEGORY.
///
/// Lives on the sending body's Tenant — the school or juvenile-court a student belongs to
/// (<c>ServiceLog.SchoolId</c>), not the org that provided the service: it is the school that
/// decides which hours it trusts enough to count without a look.
///
/// Resolution is most-specific-wins: a rule for the exact org beats a rule for the category,
/// which beats <see cref="Default"/>. The default is <c>approvalRequired</c>, so a school that
/// has configured nothing keeps the pre-#12 behaviour (every log is reviewed) — preapproval is
/// always opt-in, and a student with no school never has a policy to apply.
/// </summary>
public class ApprovalPolicy
{
	/// <summary>Policy for an org/category the school has not listed. <c>approvalRequired</c> unless changed.</summary>
	[JsonPropertyName("default")]
	public string Default { get; set; } = ApprovalPolicies.ApprovalRequired;

	/// <summary>orgId → policy, for the org that RAN the event (<c>ServiceLog.OrganizationId</c>). Beats byCategory.</summary>
	[JsonPropertyName("byOrg")]
	public Dictionary<string, string> ByOrg { get; set; } = [];

	/// <summary>
	/// service category → policy (one of <see cref="ServiceCategories"/>). Lets a school gate a
	/// whole category — e.g. "Political Parties &amp; Campaigns" — without naming every org.
	/// </summary>
	[JsonPropertyName("byCategory")]
	public Dictionary<string, string> ByCategory { get; set; } = [];

	/// <summary>
	/// The effective policy for one logged event: the exact-org rule, else the category rule,
	/// else the default. Case-insensitive on both keys; an unrecognised stored value is ignored
	/// (falls through) so a typo can never silently preapprove.
	/// </summary>
	public string Resolve(string? orgId, string? category)
	{
		if (TryGet(ByOrg, orgId, out var byOrg)) return byOrg;
		if (TryGet(ByCategory, category, out var byCat)) return byCat;
		return ApprovalPolicies.IsValid(Default) ? Default : ApprovalPolicies.ApprovalRequired;
	}

	private static bool TryGet(Dictionary<string, string> map, string? key, out string policy)
	{
		policy = ApprovalPolicies.ApprovalRequired;
		if (string.IsNullOrWhiteSpace(key) || map.Count == 0) return false;
		foreach (var kv in map)
		{
			if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase) && ApprovalPolicies.IsValid(kv.Value))
			{
				policy = kv.Value;
				return true;
			}
		}
		return false;
	}
}

/// <summary>The two policy values a school can assign. See <see cref="ApprovalPolicy"/>.</summary>
public static class ApprovalPolicies
{
	/// <summary>Hours enter the school's approval queue and count only once reviewed. The default.</summary>
	public const string ApprovalRequired = "approvalRequired";

	/// <summary>Hours auto-count on submission — no review. Always opt-in, per org and/or category.</summary>
	public const string Preapproved = "preapproved";

	public static bool IsValid(string? v) => v is ApprovalRequired or Preapproved;
}
