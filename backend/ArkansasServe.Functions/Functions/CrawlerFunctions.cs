using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

/// <summary>
/// HTTP endpoints that drive the external-event crawler.
/// All routes require PlatformAdmin/SuperAdmin — the crawler touches every partition
/// in the Events container and writes under the system "ark-serve-crawler" org.
///
/// Routes
/// ──────
///   POST   /api/admin/events/crawl           — run crawl, return import summary
///   GET    /api/admin/events/crawl/queue     — list Draft events awaiting review
///   POST   /api/admin/events/crawl/{id}/publish — approve a draft → "Open"
///   DELETE /api/admin/events/crawl/{id}      — dismiss/delete a draft
/// </summary>
public class CrawlerFunctions(
    CosmosService cosmos,
    CrawlerService crawler,
    AuthConfig authConfig,
    ILogger<CrawlerFunctions> logger)
{
    // ── POST /api/admin/events/crawl ──────────────────────────────────────────

    /// <summary>
    /// Runs the event crawler against one or more external sources and persists new
    /// events as Draft documents under the "ark-serve-crawler" organization partition.
    ///
    /// Request body (optional JSON):
    /// <code>
    /// {
    ///   "sources": ["GivePulse", "Eventbrite"],  // omit → all enabled sources
    ///   "dryRun":  false                          // true → fetch but do not persist
    /// }
    /// </code>
    ///
    /// Response:
    /// <code>
    /// {
    ///   "imported": 12,
    ///   "skipped":   3,
    ///   "dryRun":  false,
    ///   "errors":  []
    /// }
    /// </code>
    /// </summary>
    [Function("RunEventCrawl")]
    public async Task<HttpResponseData> RunCrawl(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "admin/events/crawl")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
        if (ctx == null) return authError!;
        if (!await cosmos.IsGlobalSuperAsync(ctx.UserId, ctx.Role))
            return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "PlatformAdmin required");

        var body = await HttpHelper.ReadBody<CrawlRequest>(req);
        var isDryRun = body?.DryRun ?? false;
        var sources = body?.Sources; // null = all

        logger.LogInformation(
            "[Crawler] Crawl triggered by {UserId}. Sources: {Sources}. DryRun: {DryRun}",
            ctx.UserId,
            sources is null ? "all" : string.Join(',', sources),
            isDryRun);

        IReadOnlyList<Models.CrawledEvent> fetched;
        try
        {
            fetched = await crawler.FetchAllAsync(sources, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Crawler] FetchAllAsync threw unexpectedly");
            return await HttpHelper.Error(req, HttpStatusCode.InternalServerError, "Crawl fetch failed");
        }

        if (isDryRun)
        {
            return await HttpHelper.OkJson(req, new
            {
                imported = 0,
                skipped  = 0,
                fetched  = fetched.Count,
                dryRun   = true,
                preview  = fetched.Select(e => new { e.SourceId, e.SourceName, e.Title, e.StartDateTime }),
            });
        }

        // Persist new events; skip duplicates.
        var imported = 0;
        var skipped  = 0;
        var errors   = new List<string>();

        foreach (var crawled in fetched)
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                var exists = await cosmos.CrawledEventExistsAsync(crawled.SourceId, cancellationToken);
                if (exists)
                {
                    skipped++;
                    continue;
                }
                await cosmos.CreateCrawledEventAsync(crawled, cancellationToken);
                imported++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Crawler] Failed to persist event {SourceId}", crawled.SourceId);
                errors.Add($"{crawled.SourceId}: {ex.Message}");
            }
        }

        logger.LogInformation(
            "[Crawler] Complete — imported {Imported}, skipped {Skipped}, errors {Errors}",
            imported, skipped, errors.Count);

        return await HttpHelper.OkJson(req, new
        {
            imported,
            skipped,
            dryRun = false,
            errors,
        });
    }

    // ── GET /api/admin/events/crawl/queue ─────────────────────────────────────

    /// <summary>
    /// Returns all Draft events that the crawler has imported and that a PlatformAdmin
    /// has not yet published or dismissed.
    /// </summary>
    [Function("GetCrawlQueue")]
    public async Task<HttpResponseData> GetQueue(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin/events/crawl/queue")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
        if (ctx == null) return authError!;
        if (!await cosmos.IsGlobalSuperAsync(ctx.UserId, ctx.Role))
            return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "PlatformAdmin required");

        try
        {
            var drafts = await cosmos.GetCrawledDraftEventsAsync(cancellationToken);
            return await HttpHelper.OkJson(req, drafts);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Crawler] Failed to load crawl queue");
            return await HttpHelper.Error(req, HttpStatusCode.InternalServerError, "Unable to load crawl queue");
        }
    }

    // ── POST /api/admin/events/crawl/{id}/publish ─────────────────────────────

    /// <summary>
    /// Promotes a Draft crawled event to "Open", making it visible to students in the
    /// public event browser.  An optional <c>organizationName</c> in the request body
    /// overrides the name inherited from the source.
    ///
    /// Request body (optional):
    /// <code>{ "organizationName": "Habitat for Humanity — Central Arkansas" }</code>
    /// </summary>
    [Function("PublishCrawledEvent")]
    public async Task<HttpResponseData> Publish(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "admin/events/crawl/{id}/publish")] HttpRequestData req,
        string id,
        CancellationToken cancellationToken)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
        if (ctx == null) return authError!;
        if (!await cosmos.IsGlobalSuperAsync(ctx.UserId, ctx.Role))
            return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "PlatformAdmin required");

        var body = await HttpHelper.ReadBody<PublishRequest>(req);

        try
        {
            var published = await cosmos.PublishCrawledEventAsync(id, body?.OrganizationName, cancellationToken);
            if (published is null)
                return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Event not found");

            logger.LogInformation(
                "[Crawler] Event {EventId} published by {UserId}", id, ctx.UserId);

            return await HttpHelper.OkJson(req, published);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Crawler] Failed to publish event {EventId}", id);
            return await HttpHelper.Error(req, HttpStatusCode.InternalServerError, "Unable to publish event");
        }
    }

    // ── DELETE /api/admin/events/crawl/{id} ───────────────────────────────────

    /// <summary>
    /// Dismisses (permanently deletes) a crawled Draft event.  The crawler will not
    /// re-import it on subsequent runs because <c>CrawledEventExistsAsync</c> checks
    /// all statuses.  If the event was already published or deleted, returns 200 OK.
    /// </summary>
    [Function("DismissCrawledEvent")]
    public async Task<HttpResponseData> Dismiss(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "admin/events/crawl/{id}")] HttpRequestData req,
        string id,
        CancellationToken cancellationToken)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
        if (ctx == null) return authError!;
        if (!await cosmos.IsGlobalSuperAsync(ctx.UserId, ctx.Role))
            return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "PlatformAdmin required");

        try
        {
            await cosmos.DismissCrawledEventAsync(id, cancellationToken);
            logger.LogInformation("[Crawler] Event {EventId} dismissed by {UserId}", id, ctx.UserId);
            return await HttpHelper.OkJson(req, new { dismissed = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Crawler] Failed to dismiss event {EventId}", id);
            return await HttpHelper.Error(req, HttpStatusCode.InternalServerError, "Unable to dismiss event");
        }
    }

    // ── Request records ───────────────────────────────────────────────────────

    private sealed record CrawlRequest(List<string>? Sources, bool DryRun);
    private sealed record PublishRequest(string? OrganizationName);
}
