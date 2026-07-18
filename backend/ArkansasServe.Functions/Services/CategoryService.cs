using ArkansasServe.Functions.Models;

namespace ArkansasServe.Functions.Services;

/// <summary>
/// The service-category vocabulary (#10②): the code-defined canonical list plus the stored
/// supplement (approved-new labels, aliases, and proposals) kept on the root tenant doc.
///
/// Everything that needs "the real list" or "is this a known category" goes through here so the
/// two halves never drift. A pending proposal is deliberately NOT part of the effective list —
/// it is storable on the org/event that proposed it, but invisible to every dropdown and facet
/// until a SuperAdmin approves it as a new value or an alias of an existing one.
/// </summary>
public class CategoryService(CosmosService cosmos)
{
	private const string RootId = Functions.TenantIds.Root;

	// The vocabulary changes only on a proposal or an approval — both rare — so a short cache
	// spares every /api/categories and event save a root-tenant read. Invalidated on any write.
	private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
	private readonly object _gate = new();
	private CategoryVocabulary? _cached;
	private DateTime _cachedAt;

	public sealed record EffectiveCategories(
		IReadOnlyList<string> Canonical,
		IReadOnlyList<string> Effective,
		IReadOnlyDictionary<string, string> Aliases);

	// ── Reads ───────────────────────────────────────────────────────────────────

	public async Task<CategoryVocabulary> GetVocabularyAsync()
	{
		lock (_gate)
		{
			if (_cached != null && DateTime.UtcNow - _cachedAt < CacheTtl)
				return _cached;
		}
		var root = await cosmos.GetTenantAsync(RootId);
		var vocab = root?.CategoryVocabulary ?? new CategoryVocabulary();
		lock (_gate) { _cached = vocab; _cachedAt = DateTime.UtcNow; }
		return vocab;
	}

	/// <summary>
	/// The list clients render in dropdowns and build facets from: canonical + approved-new,
	/// de-duplicated, with "Other" kept last. Never contains a pending label.
	/// </summary>
	public async Task<EffectiveCategories> GetEffectiveAsync()
	{
		var vocab = await GetVocabularyAsync();
		var canonical = ServiceCategories.All.ToList();

		var seen = new HashSet<string>(canonical, StringComparer.OrdinalIgnoreCase);
		var effective = canonical.Where(c => !string.Equals(c, ServiceCategories.Other, StringComparison.OrdinalIgnoreCase)).ToList();
		foreach (var label in vocab.ApprovedNew)
		{
			if (!string.IsNullOrWhiteSpace(label) && seen.Add(label)) effective.Add(label);
		}
		effective.Add(ServiceCategories.Other); // always last

		return new EffectiveCategories(canonical, effective,
			new Dictionary<string, string>(vocab.Aliases, StringComparer.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Classifies a stored category value: none | canonical | approvedNew | alias | pending |
	/// unknown. "unknown" is a value that is neither known nor an open proposal (e.g. a label
	/// whose proposal was rejected) — clients render it, like pending, as plain "Other".
	/// </summary>
	public async Task<string> ClassifyAsync(string? value)
	{
		if (string.IsNullOrWhiteSpace(value)) return "none";
		var v = value.Trim();
		if (ServiceCategories.All.Contains(v, StringComparer.OrdinalIgnoreCase)) return "canonical";

		var vocab = await GetVocabularyAsync();
		if (vocab.ApprovedNew.Contains(v, StringComparer.OrdinalIgnoreCase)) return "approvedNew";
		if (vocab.Aliases.Keys.Contains(v, StringComparer.OrdinalIgnoreCase)) return "alias";
		if (vocab.Proposals.Any(p => Same(p.Label, v) && p.Status == CategoryProposalStatus.Pending)) return "pending";
		return "unknown";
	}

	// ── Writes ──────────────────────────────────────────────────────────────────

	/// <summary>
	/// Records <paramref name="value"/> as a pending proposal when it is a brand-new label — i.e.
	/// not empty, not already canonical / approved-new / an alias, and not already an open
	/// proposal. Idempotent: a duplicate save does not stack proposals. No-op for known values,
	/// so callers can call it unconditionally after validating the rest of the save.
	/// </summary>
	public async Task RecordProposalIfNewAsync(string? value, string proposingOrgId, string? proposingOrgName, string source)
	{
		if (string.IsNullOrWhiteSpace(value)) return;
		var label = value.Trim();
		var kind = await ClassifyAsync(label);
		if (kind is "canonical" or "approvedNew" or "alias" or "pending") return;

		await MutateAsync(vocab =>
		{
			// Re-check under the fresh read (another save may have proposed it since Classify).
			if (vocab.Proposals.Any(p => Same(p.Label, label) && p.Status == CategoryProposalStatus.Pending))
				return false;
			vocab.Proposals.Add(new ProposedCategory
			{
				Label = label,
				ProposingOrgId = proposingOrgId,
				ProposingOrgName = proposingOrgName,
				Source = source == CategoryProposalSources.Event ? CategoryProposalSources.Event : CategoryProposalSources.Org,
				Status = CategoryProposalStatus.Pending,
			});
			return true;
		});
	}

	/// <summary>Approve a pending proposal as a NEW canonical-equivalent value that extends the list.</summary>
	public Task<CategoryVocabulary> ApproveAsNewAsync(string label, string byUserId) =>
		MutateReturningAsync(vocab =>
		{
			var prop = FindPending(vocab, label) ?? throw new InvalidOperationException("No pending proposal with that label.");
			if (!vocab.ApprovedNew.Contains(prop.Label, StringComparer.OrdinalIgnoreCase))
				vocab.ApprovedNew.Add(prop.Label);
			Resolve(vocab, prop.Label, CategoryProposalStatus.ApprovedNew, null, byUserId);
		});

	/// <summary>Approve a pending proposal as an ALIAS of an existing canonical (or approved-new) value.</summary>
	public Task<CategoryVocabulary> ApproveAsAliasAsync(string label, string canonical, string byUserId) =>
		MutateReturningAsync(vocab =>
		{
			if (string.IsNullOrWhiteSpace(canonical)) throw new ArgumentException("A canonical target is required.");
			var target = canonical.Trim();
			var isKnown = ServiceCategories.All.Contains(target, StringComparer.OrdinalIgnoreCase)
				|| vocab.ApprovedNew.Contains(target, StringComparer.OrdinalIgnoreCase);
			if (!isKnown) throw new ArgumentException($"\"{target}\" is not an existing category to alias onto.");

			var prop = FindPending(vocab, label) ?? throw new InvalidOperationException("No pending proposal with that label.");
			vocab.Aliases[prop.Label] = target;
			Resolve(vocab, prop.Label, CategoryProposalStatus.ApprovedAlias, target, byUserId);
		});

	/// <summary>Reject a pending proposal. The label stops being offered; the org shows "Other".</summary>
	public Task<CategoryVocabulary> RejectAsync(string label, string byUserId) =>
		MutateReturningAsync(vocab =>
		{
			var prop = FindPending(vocab, label) ?? throw new InvalidOperationException("No pending proposal with that label.");
			Resolve(vocab, prop.Label, CategoryProposalStatus.Rejected, null, byUserId);
		});

	// ── helpers ───────────────────────────────────────────────────────────────

	private static bool Same(string? a, string? b) =>
		string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

	private static ProposedCategory? FindPending(CategoryVocabulary vocab, string label) =>
		vocab.Proposals.FirstOrDefault(p => Same(p.Label, label) && p.Status == CategoryProposalStatus.Pending);

	private static void Resolve(CategoryVocabulary vocab, string label, string status, string? aliasOf, string byUserId)
	{
		// Resolve EVERY pending proposal with this label (two orgs may have proposed the same word).
		foreach (var p in vocab.Proposals.Where(p => Same(p.Label, label) && p.Status == CategoryProposalStatus.Pending))
		{
			p.Status = status;
			p.AliasOfCanonical = aliasOf;
			p.ResolvedAt = DateTime.UtcNow;
			p.ResolvedByUserId = byUserId;
		}
	}

	private async Task<CategoryVocabulary> MutateReturningAsync(Action<CategoryVocabulary> mutate)
	{
		var root = await cosmos.GetTenantAsync(RootId)
			?? throw new InvalidOperationException("Root tenant not found.");
		root.CategoryVocabulary ??= new CategoryVocabulary();
		mutate(root.CategoryVocabulary);
		var saved = await cosmos.UpdateTenantAsync(root);
		Invalidate();
		return saved.CategoryVocabulary ?? new CategoryVocabulary();
	}

	// Mutation that may decline to write (returns false) — used by the idempotent proposal path.
	private async Task MutateAsync(Func<CategoryVocabulary, bool> mutate)
	{
		var root = await cosmos.GetTenantAsync(RootId);
		if (root == null) return;
		root.CategoryVocabulary ??= new CategoryVocabulary();
		if (!mutate(root.CategoryVocabulary)) return;
		await cosmos.UpdateTenantAsync(root);
		Invalidate();
	}

	private void Invalidate()
	{
		lock (_gate) { _cached = null; _cachedAt = DateTime.MinValue; }
	}
}
