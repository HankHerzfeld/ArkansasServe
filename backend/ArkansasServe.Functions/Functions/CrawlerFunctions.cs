using System.Net;
using System.Security.Cryptography;
using System.Text;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

/// <summary>
/// HTTP endpoints that drive the external-event crawler.
/// These routes touch every partition in the Events container and write under the
/// system "ark-serve-crawler" org, so they require PlatformAdmin/SuperAdmin — with one
/// deliberate exception, noted below.
///
/// Routes
/// ──────
///   POST   /api/manage/events/crawl           — run crawl, return import summary
///   GET    /api/manage/events/crawl/queue     — list Draft events awaiting review
///   POST   /api/manage/events/crawl/{id}/publish — approve a draft → "Open"
///   DELETE /api/manage/events/crawl/{id}      — dismiss/delete a draft
///
/// Auth
/// ────
/// Every route takes an Entra JWT + SuperAdmin. The crawl route ALSO accepts a shared
/// secret in <c>X-Crawler-Secret</c>, because Entra tokens expire in ~1h and so cannot
/// drive the unattended daily workflow. That second path is weaker on purpose and is
/// confined to the crawl route: it only imports events as Drafts, which a human must
/// still review and publish through the JWT-only routes. Nothing it can do is visible
/// to students without that review — which is what makes the weaker path acceptable.
/// See <c>AuthConfig.CrawlerSharedSecret</c>; unset the setting and the path vanishes.
/// </summary>
public class CrawlerFunctions(
    CosmosService cosmos,
    CrawlerService crawler,
    AuthConfig authConfig,
    ILogger<CrawlerFunctions> logger)
{
    /// <summary>Header carrying the machine-to-machine secret. Crawl route only.</summary>
    private const string CrawlerSecretHeader = "X-Crawler-Secret";

    /// <summary>Synthetic caller id recorded in logs for secret-authenticated runs.</summary>
    private const string CrawlerServiceCallerId = "crawler-service";

    /// <summary>
    /// Constant-time check of the <see cref="CrawlerSecretHeader"/> against the configured
    /// secret. Returns false when no secret is configured, so a deployment that has not set
    /// one has no header path at all rather than one that waves through a blank value.
    /// </summary>
    private bool IsValidCrawlerSecret(HttpRequestData req)
    {
        var expected = authConfig.CrawlerSharedSecret;
        if (string.IsNullOrWhiteSpace(expected)) return false;

        if (!req.Headers.TryGetValues(CrawlerSecretHeader, out var values)) return false;
        var presented = values.FirstOrDefault();
        if (string.IsNullOrEmpty(presented)) return false;

        // FixedTimeEquals is constant-time only across equal-length inputs; it returns early
        // on a length mismatch. That leaks the secret's length and nothing more, which is
        // acceptable — the value is high-entropy, so length is not a useful lever.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented),
            Encoding.UTF8.GetBytes(expected));
    }

    // ── POST /api/manage/events/crawl ─────────────────────────────────────────

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
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/events/crawl")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        // ── Authentication: two accepted paths ────────────────────────────────
        // 1. X-Crawler-Secret — unattended callers (the daily workflow). Entra access
        //    tokens expire in ~1h, so a static GitHub secret cannot drive a schedule.
        // 2. Entra JWT + SuperAdmin — a human running a crawl on demand.
        //
        // Presenting the header commits the caller to path 1: a bad secret is a 401, not
        // a fallthrough to path 2. Falling through would turn every rejected machine call
        // into an anonymous JWT check and muddy the logs, and there is no caller that
        // legitimately holds both.
        string callerId;
        if (req.Headers.Contains(CrawlerSecretHeader))
        {
            if (!IsValidCrawlerSecret(req))
            {
                logger.LogWarning("[Crawler] Rejected a request presenting an invalid {Header}.", CrawlerSecretHeader);
                return await HttpHelper.Error(req, HttpStatusCode.Unauthorized, "Invalid crawler credentials");
            }
            callerId = CrawlerServiceCallerId;
        }
        else
        {
            var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
            if (ctx == null) return authError!;
            if (!await cosmos.IsGlobalSuperAsync(ctx.UserId, ctx.AdminLevel, cancellationToken))
                return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "SuperAdmin required");
            callerId = ctx.UserId;
        }

        var body = await HttpHelper.ReadBody<CrawlRequest>(req);
        var isDryRun = body?.DryRun ?? false;
        var sources = body?.Sources; // null = all

        logger.LogInformation(
            "[Crawler] Crawl triggered by {UserId}. Sources: {Sources}. DryRun: {DryRun}",
            callerId,
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
                errors.Add($"{crawled.SourceId}: persist failed");
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

    // ── GET /api/manage/events/crawl/queue ───────────────────────────────────

    /// <summary>
    /// Returns all Draft events that the crawler has imported and that a PlatformAdmin
    /// has not yet published or dismissed.
    /// </summary>
    [Function("GetCrawlQueue")]
    public async Task<HttpResponseData> GetQueue(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/events/crawl/queue")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
        if (ctx == null) return authError!;
        if (!await cosmos.IsGlobalSuperAsync(ctx.UserId, ctx.AdminLevel, cancellationToken))
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

    // ── POST /api/manage/events/crawl/{id}/publish ────────────────────────────

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
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/events/crawl/{id}/publish")] HttpRequestData req,
        string id,
        CancellationToken cancellationToken)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
        if (ctx == null) return authError!;
        if (!await cosmos.IsGlobalSuperAsync(ctx.UserId, ctx.AdminLevel, cancellationToken))
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

    // ── DELETE /api/manage/events/crawl/{id} ─────────────────────────────────

    /// <summary>
    /// Dismisses a crawled Draft event from the review queue.
    /// Implementation note: this should *not* hard-delete the document if you want dismissals
    /// to remain permanent across crawl runs (dedup relies on the stored crawlerSourceId).
    /// If the event was already published or missing, returns 200 OK.
    /// </summary>
    [Function("DismissCrawledEvent")]
    public async Task<HttpResponseData> Dismiss(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "manage/events/crawl/{id}")] HttpRequestData req,
        string id,
        CancellationToken cancellationToken)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
        if (ctx == null) return authError!;
        if (!await cosmos.IsGlobalSuperAsync(ctx.UserId, ctx.AdminLevel, cancellationToken))
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
