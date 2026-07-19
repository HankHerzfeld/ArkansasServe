using ArkansasServe.Functions.Models;

namespace ArkansasServe.Functions.Services;

/// <summary>
/// Shared same-org tag-gating check (#11②, #14). Returns the labels of a tenant's ACTIVE tags of
/// a given enforcement that a member lacks a CURRENT state for (empty when nothing blocks).
///
/// Whether the gate APPLIES is the caller's decision, and it is always same-org: a cross-org
/// registrant has no User doc in the event's org to carry a tag state, so there is nothing to
/// evaluate (the locked cross-org decision). Callers pass the event-org tenant and the member's
/// own doc only when those are the same org.
/// </summary>
public static class TagGate
{
	public static List<string> MissingTags(Tenant? tenant, User? member, string enforcement, DateTime now)
	{
		if (member == null) return [];

		var gating = tenant?.UserTags
			.Where(t => string.Equals(t.Enforcement, enforcement, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(t.Status, "active", StringComparison.OrdinalIgnoreCase))
			.ToList() ?? [];
		if (gating.Count == 0) return [];

		return gating
			.Where(t => !member.Tags.Any(s =>
				string.Equals(s.TagId, t.Id, StringComparison.OrdinalIgnoreCase) && s.IsCurrentAt(now)))
			.Select(t => t.Label)
			.ToList();
	}
}
