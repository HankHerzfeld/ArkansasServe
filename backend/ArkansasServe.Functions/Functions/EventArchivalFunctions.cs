using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

public class EventArchivalFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<EventArchivalFunctions> logger)
{
    // Daily at 08:00 UTC (~2–3am Central). Flips events whose end time has passed from Open to
    // Archived, so they drop off the public "upcoming" list while staying visible to admins (who
    // still log hours after the event). Idempotent — only status='Open' is touched.
    [Function("ArchivePastEvents")]
    public async Task RunScheduled([TimerTrigger("0 0 8 * * *")] TimerInfo timer)
    {
        var now = DateTime.UtcNow;
        try
        {
            var count = await cosmos.ArchivePastEventsAsync(now);
            logger.LogInformation("[Archival] Archived {Count} past event(s) at {Now:o}", count, now);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Archival] Scheduled archival run failed");
        }
    }

    // Same sweep, on demand. Lets a SuperAdmin archive immediately rather than waiting for the
    // nightly run (and is how the feature is verified after deploy). Platform-wide, so it needs a
    // SuperAdmin membership — checked against memberships, not the token level (Finding 9).
    [Function("ArchivePastEventsNow")]
    public async Task<HttpResponseData> RunManual(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/events/archive-past")] HttpRequestData req)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
        if (ctx == null) return authError!;
        if (!await cosmos.IsAtLeastInAnyOrgAsync(ctx.UserId, ctx.AdminLevel, AdminLevels.SuperAdmin))
            return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Forbidden");

        var now = DateTime.UtcNow;
        var count = await cosmos.ArchivePastEventsAsync(now);
        logger.LogInformation("[Archival] Manual archival by {UserId} archived {Count} event(s)", ctx.UserId, count);
        return await HttpHelper.OkJson(req, new { archived = count, at = now });
    }
}
