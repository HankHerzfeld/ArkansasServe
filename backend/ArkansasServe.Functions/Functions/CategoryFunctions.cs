using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

/// <summary>
/// Service-category vocabulary endpoints (#10②):
///   • GET /api/categories — the effective list every category dropdown/facet renders from
///     (canonical + approved-new, never a pending value) plus the alias map, so a client can
///     resolve a stored value for display without a per-item server round-trip.
///   • the SuperAdmin proposal queue (list + resolve), under manage/backend/.
/// Proposals are RECORDED by EventFunctions / AdminFunctions when an org or event is saved with
/// an unknown label — this class only reads the effective list and lets a super resolve them.
/// </summary>
public class CategoryFunctions(CategoryService categories, CosmosService cosmos, AuthConfig authConfig, ILogger<CategoryFunctions> logger)
{
	/// <summary>GET /api/categories — effective list + aliases. Any signed-in user (fills dropdowns).</summary>
	[Function("GetCategories")]
	public async Task<HttpResponseData> GetCategories(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "categories")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var eff = await categories.GetEffectiveAsync();
		return await HttpHelper.OkJson(req, new
		{
			canonical = eff.Canonical,
			effective = eff.Effective,
			aliases = eff.Aliases,
		});
	}

	/// <summary>GET /api/manage/backend/category-proposals — pending proposals for the SuperAdmin queue.</summary>
	[Function("GetCategoryProposals")]
	public async Task<HttpResponseData> GetCategoryProposals(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/backend/category-proposals")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await cosmos.IsGlobalSuperAsync(ctx.UserId, ctx.AdminLevel))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "SuperAdmin only");

		var vocab = await categories.GetVocabularyAsync();
		var pending = vocab.Proposals
			.Where(p => p.Status == CategoryProposalStatus.Pending)
			.OrderBy(p => p.CreatedAt)
			.ToList();
		var eff = await categories.GetEffectiveAsync();
		// Return the effective list too so the queue can offer alias targets without a second call.
		return await HttpHelper.OkJson(req, new { pending, aliasTargets = eff.Effective });
	}

	/// <summary>
	/// POST /api/manage/backend/category-proposals/resolve
	/// body: { label, action: "approveNew" | "approveAlias" | "reject", canonical? }
	/// </summary>
	[Function("ResolveCategoryProposal")]
	public async Task<HttpResponseData> ResolveCategoryProposal(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/backend/category-proposals/resolve")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;
		if (!await cosmos.IsGlobalSuperAsync(ctx.UserId, ctx.AdminLevel))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "SuperAdmin only");

		var body = await HttpHelper.ReadBody<ResolveRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.Label) || string.IsNullOrWhiteSpace(body.Action))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "label and action are required");

		try
		{
			var vocab = body.Action switch
			{
				"approveNew"   => await categories.ApproveAsNewAsync(body.Label, ctx.UserId),
				"approveAlias" => await categories.ApproveAsAliasAsync(body.Label, body.Canonical ?? string.Empty, ctx.UserId),
				"reject"       => await categories.RejectAsync(body.Label, ctx.UserId),
				_ => throw new ArgumentException($"\"{body.Action}\" is not a valid action."),
			};
			logger.LogInformation("[Categories] {Actor} resolved proposal \"{Label}\" via {Action}{Target}",
				ctx.UserId, body.Label, body.Action, body.Action == "approveAlias" ? $" -> {body.Canonical}" : "");
			return await HttpHelper.OkJson(req, vocab);
		}
		catch (InvalidOperationException ex)
		{
			return await HttpHelper.Error(req, HttpStatusCode.NotFound, ex.Message);
		}
		catch (ArgumentException ex)
		{
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, ex.Message);
		}
	}

	private sealed record ResolveRequest(string? Label, string? Action, string? Canonical);
}
