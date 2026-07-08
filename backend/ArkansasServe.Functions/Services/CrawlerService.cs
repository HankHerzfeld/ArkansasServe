using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArkansasServe.Functions.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Services;

/// <summary>
/// Fetches volunteer-event listings from external sources and normalises them into
/// <see cref="CrawledEvent"/> DTOs.  Each source adapter is self-contained: it reads
/// its own config keys, gracefully skips itself when keys are absent, and never lets
/// its failure propagate to other adapters.
///
/// Configuration keys (set via Azure Function App settings / local.settings.json):
///   Crawler__GivePulse__ApiKey        — GivePulse REST API key
///   Crawler__Eventbrite__ApiKey       — Eventbrite private token
///   Crawler__VolunteerMatch__ApiKey   — VolunteerMatch API key
///   Crawler__AllForGood__ApiKey       — All for Good / CNCS API key (optional)
///
/// JustServe is HTML-scraped and requires no key.
/// </summary>
public sealed class CrawlerService(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<CrawlerService> logger)
{
    // Major Arkansas population-centre zip codes used for JustServe radius searches.
    private static readonly string[] ArkansasZipCodes =
    [
        "72201", // Little Rock
        "72401", // Jonesboro
        "71601", // Pine Bluff
        "72701", // Fayetteville
        "71901", // Hot Springs
        "72101", // Conway (approx — closest match is 72034)
        "72450", // Paragould
        "71730", // El Dorado
    ];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs all enabled source adapters and returns a deduplicated list of events.
    /// Each adapter failure is caught and logged without aborting the others.
    /// </summary>
    /// <param name="sources">
    /// Optional allow-list of source names (case-insensitive).  Pass null to run
    /// every enabled adapter.
    /// </param>
    public async Task<IReadOnlyList<CrawledEvent>> FetchAllAsync(
        IEnumerable<string>? sources = null,
        CancellationToken cancellationToken = default)
    {
        var allowed = sources is null
            ? null
            : new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase);

        bool Include(string name) => allowed is null || allowed.Contains(name);

        var results = new List<CrawledEvent>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        async Task RunAdapter(string name, Func<CancellationToken, Task<IReadOnlyList<CrawledEvent>>> adapter)
        {
            if (!Include(name)) return;
            try
            {
                logger.LogInformation("[Crawler] Starting adapter: {Adapter}", name);
                var events = await adapter(cancellationToken);
                var added = 0;
                foreach (var e in events)
                {
                    if (seenIds.Add(e.SourceId))
                    {
                        results.Add(e);
                        added++;
                    }
                }
                logger.LogInformation("[Crawler] {Adapter}: fetched {Count} unique event(s)", name, added);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Crawler] Adapter {Adapter} failed — skipping", name);
            }
        }

        await RunAdapter("GivePulse",       FetchGivePulseAsync);
        await RunAdapter("Eventbrite",      FetchEventbriteAsync);
        await RunAdapter("VolunteerMatch",  FetchVolunteerMatchAsync);
        await RunAdapter("JustServe",       FetchJustServeAsync);
        await RunAdapter("AllForGood",      FetchAllForGoodAsync);

        return results.AsReadOnly();
    }

    // ── GivePulse ─────────────────────────────────────────────────────────────

    /// <summary>
    /// GivePulse REST API — returns public volunteer events in Arkansas.
    /// Requires: Crawler__GivePulse__ApiKey
    /// Docs: https://app.givepulse.com/api
    /// </summary>
    private async Task<IReadOnlyList<CrawledEvent>> FetchGivePulseAsync(CancellationToken ct)
    {
        var apiKey = config["Crawler__GivePulse__ApiKey"] ?? config["Crawler:GivePulse:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("[Crawler] GivePulse: no API key configured (Crawler__GivePulse__ApiKey) — skipping");
            return [];
        }

        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var results = new List<CrawledEvent>();
        int page = 1;
        bool hasMore = true;

        while (hasMore)
        {
            var url = $"https://app.givepulse.com/api/events?state=Arkansas&page={page}&per_page=100&status=active";
            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("[Crawler] GivePulse: HTTP {Status} from API on page {Page}", (int)response.StatusCode, page);
                break;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var root = JsonNode.Parse(json);
            var items = root?["data"]?.AsArray() ?? root?.AsArray();

            if (items is null || items.Count == 0) break;

            foreach (var item in items)
            {
                if (item is null) continue;
                try
                {
                    var id       = item["id"]?.ToString() ?? Guid.NewGuid().ToString();
                    var title    = item["title"]?.ToString() ?? item["name"]?.ToString() ?? "(Untitled)";
                    var desc     = item["description"]?.ToString() ?? item["summary"]?.ToString();
                    var location = FormatLocation(
                        item["address"]?.ToString() ?? item["city"]?.ToString(),
                        item["state"]?.ToString(),
                        item["zip"]?.ToString());
                    var startRaw = item["start_datetime"]?.ToString() ?? item["start_date"]?.ToString();
                    var endRaw   = item["end_datetime"]?.ToString() ?? item["end_date"]?.ToString();
                    var start    = ParseDate(startRaw);
                    var end      = ParseDate(endRaw);
                    var orgName  = item["group"]?["name"]?.ToString() ?? item["organization"]?.ToString() ?? "GivePulse Event";
                    var contactEmail = item["email"]?.ToString() ?? item["contact_email"]?.ToString();
                    var contactUrl   = $"https://app.givepulse.com/events/{id}";

                    if (start == default) continue; // skip events without a valid date

                    results.Add(new CrawledEvent
                    {
                        SourceId      = $"givepulse:{id}",
                        SourceName    = "GivePulse",
                        SourceUrl     = contactUrl,
                        Title         = title,
                        Description   = desc,
                        Location      = location,
                        StartDateTime = start,
                        EndDateTime   = end == default ? start.AddHours(2) : end,
                        OrganizationName = orgName,
                        ContactEmail  = contactEmail,
                        ContactUrl    = item["group"]?["url"]?.ToString(),
                        RawJson       = item.ToJsonString(),
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[Crawler] GivePulse: failed to parse item");
                }
            }

            // Paginate until we get a short page or explicit total metadata.
            hasMore = items.Count == 100;
            page++;
            if (page > 20) break; // safety cap
        }

        return results.AsReadOnly();
    }

    // ── Eventbrite ────────────────────────────────────────────────────────────

    /// <summary>
    /// Eventbrite REST API — volunteer/charity events in Arkansas.
    /// Requires: Crawler__Eventbrite__ApiKey (private token)
    /// Docs: https://www.eventbrite.com/platform/api
    /// Category 111 = Charity &amp; Causes
    /// </summary>
    private async Task<IReadOnlyList<CrawledEvent>> FetchEventbriteAsync(CancellationToken ct)
    {
        var apiKey = config["Crawler__Eventbrite__ApiKey"] ?? config["Crawler:Eventbrite:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("[Crawler] Eventbrite: no API key configured (Crawler__Eventbrite__ApiKey) — skipping");
            return [];
        }

        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var results = new List<CrawledEvent>();
        int page = 1;
        bool hasMore = true;

        while (hasMore)
        {
            // AR = Arkansas, category 111 = Charity & Causes
            var url = $"https://www.eventbriteapi.com/v3/events/search/" +
                      $"?location.address.region=AR" +
                      $"&categories=111" +
                      $"&expand=venue,organizer" +
                      $"&page={page}" +
                      $"&page_size=50" +
                      $"&start_date.range_start={DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}";

            var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("[Crawler] Eventbrite: HTTP {Status}", (int)response.StatusCode);
                break;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var root = JsonNode.Parse(json);
            var items = root?["events"]?.AsArray();
            var pagination = root?["pagination"];

            if (items is null || items.Count == 0) break;

            foreach (var item in items)
            {
                if (item is null) continue;
                try
                {
                    var id    = item["id"]?.ToString() ?? Guid.NewGuid().ToString();
                    var title = item["name"]?["text"]?.ToString() ?? "(Untitled)";
                    var desc  = item["description"]?["text"]?.ToString() ?? item["summary"]?.ToString();
                    var start = ParseDate(item["start"]?["utc"]?.ToString());
                    var end   = ParseDate(item["end"]?["utc"]?.ToString());

                    if (start == default) continue;

                    var venue    = item["venue"];
                    var location = FormatLocation(
                        venue?["address"]?["localized_address_display"]?.ToString() ?? venue?["name"]?.ToString(),
                        "AR", null);

                    var organizer   = item["organizer"];
                    var orgName     = organizer?["name"]?.ToString() ?? "Eventbrite Event";
                    var contactUrl  = organizer?["url"]?.ToString() ?? item["url"]?.ToString();
                    var sourceUrl   = item["url"]?.ToString() ?? $"https://www.eventbrite.com/e/{id}";

                    results.Add(new CrawledEvent
                    {
                        SourceId      = $"eventbrite:{id}",
                        SourceName    = "Eventbrite",
                        SourceUrl     = sourceUrl,
                        Title         = title,
                        Description   = desc,
                        Location      = location,
                        StartDateTime = start,
                        EndDateTime   = end == default ? start.AddHours(2) : end,
                        OrganizationName = orgName,
                        ContactUrl    = contactUrl,
                        RawJson       = item.ToJsonString(),
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[Crawler] Eventbrite: failed to parse item");
                }
            }

            var hasMorePages = pagination?["has_more_items"]?.GetValue<bool>() ?? false;
            hasMore = hasMorePages && page < 10;
            page++;
        }

        return results.AsReadOnly();
    }

    // ── VolunteerMatch ────────────────────────────────────────────────────────

    /// <summary>
    /// VolunteerMatch REST API — Arkansas volunteer opportunities.
    /// Requires: Crawler__VolunteerMatch__ApiKey
    /// Docs: https://www.volunteermatch.org/developers/
    /// </summary>
    private async Task<IReadOnlyList<CrawledEvent>> FetchVolunteerMatchAsync(CancellationToken ct)
    {
        var apiKey = config["Crawler__VolunteerMatch__ApiKey"] ?? config["Crawler:VolunteerMatch:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("[Crawler] VolunteerMatch: no API key configured (Crawler__VolunteerMatch__ApiKey) — skipping");
            return [];
        }

        // VolunteerMatch uses HTTP Basic: key is "user:apikey" base64-encoded.
        var vmUser = config["Crawler__VolunteerMatch__Username"] ?? "api";
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{vmUser}:{apiKey}"));

        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // VolunteerMatch JSON-RPC style: POST to /api/json
        const string endpoint = "https://www.volunteermatch.org/api/json";
        var payload = new
        {
            action = "searchOpportunities",
            query = new
            {
                location = "Arkansas, US",
                radius = 100,
                fieldsToDisplay = new[] { "id", "title", "description", "location", "startDate", "endDate", "organization" },
                numberOfResults = 100,
            }
        };

        var requestContent = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var response = await client.PostAsync(endpoint, requestContent, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("[Crawler] VolunteerMatch: HTTP {Status}", (int)response.StatusCode);
            return [];
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var root = JsonNode.Parse(json);
        var opps = root?["opportunities"]?.AsArray() ?? root?["data"]?.AsArray();
        if (opps is null) return [];

        var results = new List<CrawledEvent>();
        foreach (var item in opps)
        {
            if (item is null) continue;
            try
            {
                var id       = item["id"]?.ToString() ?? Guid.NewGuid().ToString();
                var title    = item["title"]?.ToString() ?? "(Untitled)";
                var desc     = item["description"]?.ToString();
                var loc      = item["location"]?["city"]?.ToString();
                var state    = item["location"]?["region"]?.ToString();
                var start    = ParseDate(item["startDate"]?.ToString() ?? item["date"]?.ToString());
                var end      = ParseDate(item["endDate"]?.ToString());
                var orgName  = item["organization"]?["name"]?.ToString() ?? "VolunteerMatch Opportunity";
                var orgUrl   = item["organization"]?["url"]?.ToString();
                var sourceUrl = $"https://www.volunteermatch.org/search/opp{id}.jsp";

                if (start == default) continue;

                results.Add(new CrawledEvent
                {
                    SourceId      = $"volunteermatch:{id}",
                    SourceName    = "VolunteerMatch",
                    SourceUrl     = sourceUrl,
                    Title         = title,
                    Description   = desc,
                    Location      = FormatLocation(loc, state, null),
                    StartDateTime = start,
                    EndDateTime   = end == default ? start.AddHours(3) : end,
                    OrganizationName = orgName,
                    ContactUrl    = orgUrl,
                    RawJson       = item.ToJsonString(),
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Crawler] VolunteerMatch: failed to parse item");
            }
        }

        return results.AsReadOnly();
    }

    // ── JustServe ─────────────────────────────────────────────────────────────

    /// <summary>
    /// JustServe.org — HTML-scraped volunteer projects across Arkansas zip codes.
    /// No API key required; scraping the public search page is permitted per the
    /// site's content (projects are publicly listed to attract volunteers).
    /// </summary>
    private async Task<IReadOnlyList<CrawledEvent>> FetchJustServeAsync(CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent",
            "ArkansasServe-Crawler/1.0 (+https://arkansasserve.com)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var results = new List<CrawledEvent>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var zip in ArkansasZipCodes)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await FetchJustServeZipAsync(client, zip, results, seenIds, ct);
                // Be polite — don't hammer the server
                await Task.Delay(500, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Crawler] JustServe: failed for zip {Zip}", zip);
            }
        }

        return results.AsReadOnly();
    }

    private static async Task FetchJustServeZipAsync(
        HttpClient client,
        string zip,
        List<CrawledEvent> results,
        HashSet<string> seenIds,
        CancellationToken ct)
    {
        // JustServe returns structured data in <script type="application/ld+json"> blocks
        // and also in data attributes.  We parse both paths for maximum coverage.
        var url = $"https://www.justserve.org/projects?zip={zip}&miles=50";
        var response = await client.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return;

        var html = await response.Content.ReadAsStringAsync(ct);

        // Extract JSON-LD structured data blocks (most reliable path).
        var ldBlocks = ExtractJsonLdBlocks(html);
        foreach (var block in ldBlocks)
        {
            try
            {
                var node = JsonNode.Parse(block);
                if (node?["@type"]?.ToString() != "Event") continue;

                var id    = node["identifier"]?.ToString() ?? node["url"]?.ToString() ?? Guid.NewGuid().ToString();
                var dedup = $"justserve:{Stable(id)}";
                if (!seenIds.Add(dedup)) continue;

                var title = node["name"]?.ToString() ?? "(Untitled)";
                var desc  = node["description"]?.ToString();
                var start = ParseDate(node["startDate"]?.ToString());
                var end   = ParseDate(node["endDate"]?.ToString());
                if (start == default) continue;

                var loc = node["location"]?["address"]?["streetAddress"]?.ToString()
                       ?? node["location"]?["name"]?.ToString();
                var city  = node["location"]?["address"]?["addressLocality"]?.ToString();
                var state = node["location"]?["address"]?["addressRegion"]?.ToString();
                var orgName = node["organizer"]?["name"]?.ToString() ?? "JustServe Project";
                var orgUrl  = node["organizer"]?["url"]?.ToString();
                var sourceUrl = node["url"]?.ToString() ?? $"https://www.justserve.org/projects?zip={zip}";

                results.Add(new CrawledEvent
                {
                    SourceId      = dedup,
                    SourceName    = "JustServe",
                    SourceUrl     = sourceUrl,
                    Title         = title,
                    Description   = desc,
                    Location      = FormatLocation(loc ?? city, state, null),
                    StartDateTime = start,
                    EndDateTime   = end == default ? start.AddHours(2) : end,
                    OrganizationName = orgName,
                    ContactUrl    = orgUrl,
                    RawJson       = block,
                });
            }
            catch { /* skip malformed block */ }
        }
    }

    // ── All for Good ──────────────────────────────────────────────────────────

    /// <summary>
    /// All for Good / CNCS federal volunteer database.
    /// Requires: Crawler__AllForGood__ApiKey (optional — some endpoints are open)
    /// Docs: https://www.allforgood.org/docs
    /// </summary>
    private async Task<IReadOnlyList<CrawledEvent>> FetchAllForGoodAsync(CancellationToken ct)
    {
        var apiKey = config["Crawler__AllForGood__ApiKey"] ?? config["Crawler:AllForGood:ApiKey"];
        // All for Good has an open endpoint for basic searches; include key if present.

        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var urlBuilder = new StringBuilder(
            "https://www.allforgood.org/api/?output=json&q=volunteer&vol_loc=Arkansas,US&num=100");
        if (!string.IsNullOrWhiteSpace(apiKey))
            urlBuilder.Append($"&key={Uri.EscapeDataString(apiKey)}");

        var response = await client.GetAsync(urlBuilder.ToString(), ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("[Crawler] AllForGood: HTTP {Status}", (int)response.StatusCode);
            return [];
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var root = JsonNode.Parse(json);
        var items = root?["opportunities"]?.AsArray()
                 ?? root?["feed"]?["entry"]?.AsArray();

        if (items is null) return [];

        var results = new List<CrawledEvent>();
        foreach (var item in items)
        {
            if (item is null) continue;
            try
            {
                var id       = item["id"]?.ToString() ?? item["id_and_version"]?.ToString() ?? Guid.NewGuid().ToString();
                var title    = item["title"]?.ToString() ?? item["title_detail"]?["value"]?.ToString() ?? "(Untitled)";
                var desc     = item["content"]?.ToString() ?? item["summary"]?.ToString();
                var start    = ParseDate(item["start_date"]?.ToString() ?? item["openEnded"]?.ToString());
                var orgName  = item["org_name"]?.ToString() ?? item["sponsor"]?.ToString() ?? "All for Good";
                var orgUrl   = item["org_url"]?.ToString();
                var sourceUrl = item["base_url"]?.ToString() ?? item["link"]?.ToString() ?? "https://www.allforgood.org/";
                var city     = item["city"]?.ToString();
                var state    = item["state"]?.ToString();

                if (start == default) continue;

                results.Add(new CrawledEvent
                {
                    SourceId      = $"allforgood:{Stable(id)}",
                    SourceName    = "All for Good",
                    SourceUrl     = sourceUrl,
                    Title         = title,
                    Description   = desc,
                    Location      = FormatLocation(city, state, null),
                    StartDateTime = start,
                    EndDateTime   = start.AddHours(3),
                    OrganizationName = orgName,
                    ContactUrl    = orgUrl,
                    RawJson       = item.ToJsonString(),
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Crawler] AllForGood: failed to parse item");
            }
        }

        return results.AsReadOnly();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DateTime ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return default;
        return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToUniversalTime()
            : default;
    }

    private static string FormatLocation(string? addressOrCity, string? state, string? zip)
    {
        var parts = new[] { addressOrCity, state, zip }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim());
        var joined = string.Join(", ", parts);
        return string.IsNullOrWhiteSpace(joined) ? "Arkansas" : joined;
    }

    /// <summary>Stable short hash for a string, used to build dedup keys from URLs.</summary>
    private static string Stable(string input)
    {
        // Simple 32-bit hash — not cryptographic, just stable across runs.
        uint hash = 2166136261u;
        foreach (char c in input)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash.ToString("x8");
    }

    /// <summary>
    /// Extracts the content of all &lt;script type="application/ld+json"&gt; blocks
    /// from an HTML string.
    /// </summary>
    private static List<string> ExtractJsonLdBlocks(string html)
    {
        var results = new List<string>();
        const string open  = "<script type=\"application/ld+json\">";
        const string close = "</script>";
        int pos = 0;
        while (true)
        {
            int start = html.IndexOf(open, pos, StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;
            int contentStart = start + open.Length;
            int end = html.IndexOf(close, contentStart, StringComparison.OrdinalIgnoreCase);
            if (end < 0) break;
            results.Add(html[contentStart..end].Trim());
            pos = end + close.Length;
        }
        return results;
    }
}
