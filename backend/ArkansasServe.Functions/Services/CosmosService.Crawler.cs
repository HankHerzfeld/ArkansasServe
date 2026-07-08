using ArkansasServe.Functions.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Services;

public partial class CosmosService
{
    /// <summary>
    /// Unique organizationId used as the Cosmos partition key for all crawled draft
    /// events.  Events remain in this partition even after being published (status
    /// changes from "Draft" to "Open"); they are visible via cross-partition queries
    /// and show up in the student-facing event browser once published.
    /// </summary>
    public const string CrawlerOrgId = "ark-serve-crawler";

    /// <summary>
    /// Returns all events that were imported by the crawler and are still awaiting
    /// PlatformAdmin review (status == "Draft").
    /// Cross-partition query — acceptable for this low-frequency admin tool.
    /// </summary>
    public async Task<List<Event>> GetCrawledDraftEventsAsync(CancellationToken cancellationToken = default)
    {
        var queryDef = new QueryDefinition(
            "SELECT * FROM c WHERE c.isCrawled = true AND c.status = 'Draft' ORDER BY c.startDateTime ASC");

        try
        {
            var results = new List<Event>();
            var iterator = Events.GetItemQueryIterator<Event>(queryDef);
            while (iterator.HasMoreResults)
                results.AddRange(await iterator.ReadNextAsync(cancellationToken));
            return results;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Failed to query crawled draft events");
            throw;
        }
    }

    /// <summary>
    /// Promotes a crawled draft event to "Open" so it becomes visible to students.
    /// Optionally assigns a real organization name supplied by the reviewing admin.
    /// </summary>
    public async Task<Event?> PublishCrawledEventAsync(
        string eventId,
        string? assignedOrgName,
        CancellationToken cancellationToken = default)
    {
        var evt = await GetEventAsync(eventId, CrawlerOrgId, cancellationToken);
        if (evt is null) return null;

        evt.Status = "Open";
        if (!string.IsNullOrWhiteSpace(assignedOrgName))
            evt.OrganizationName = assignedOrgName.Trim();

        return await UpdateEventAsync(evt, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Dismisses a crawled draft — hard-deletes the document so it is permanently
    /// removed from the review queue and will not re-appear on the next crawl
    /// (dedup logic prevents re-import of the same source ID).
    /// </summary>
    public async Task DismissCrawledEventAsync(string eventId, CancellationToken cancellationToken = default)
    {
        try
        {
            await Events.DeleteItemAsync<Event>(eventId, new PartitionKey(CrawlerOrgId), cancellationToken: cancellationToken);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone — treat as success.
        }
    }

    /// <summary>
    /// Checks whether a crawled event with the given deduplication key already exists
    /// in any status (Draft, Open, etc.), so the crawler can skip re-importing it.
    /// Cross-partition query against the crawler partition only.
    /// </summary>
    public async Task<bool> CrawledEventExistsAsync(string crawlerSourceId, CancellationToken cancellationToken = default)
    {
        var queryDef = new QueryDefinition(
            "SELECT VALUE COUNT(1) FROM c WHERE c.crawlerSourceId = @sourceId")
            .WithParameter("@sourceId", crawlerSourceId);

        try
        {
            var iterator = Events.GetItemQueryIterator<int>(
                queryDef,
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(CrawlerOrgId) });

            while (iterator.HasMoreResults)
            {
                var response = await iterator.ReadNextAsync(cancellationToken);
                if (response.FirstOrDefault() > 0) return true;
            }
            return false;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Failed to check existence for crawlerSourceId {SourceId}", crawlerSourceId);
            return false;
        }
    }

    /// <summary>
    /// Persists a <see cref="CrawledEvent"/> as a new Draft <see cref="Event"/>
    /// document in the crawler org partition.
    /// </summary>
    public async Task<Event> CreateCrawledEventAsync(CrawledEvent crawled, CancellationToken cancellationToken = default)
    {
        var attribution = $"Added from {crawled.SourceName} — view original listing: {crawled.SourceUrl}";

        var evt = new Event
        {
            OrganizationId    = CrawlerOrgId,
            OrganizationName  = crawled.OrganizationName,
            Title             = crawled.Title,
            Description       = crawled.Description,
            Location          = crawled.Location,
            StartDateTime     = crawled.StartDateTime,
            EndDateTime       = crawled.EndDateTime == default ? crawled.StartDateTime.AddHours(2) : crawled.EndDateTime,
            HoursValue        = crawled.HoursEstimate,
            Status            = "Draft",
            Visibility        = "public",
            CreatedByUserId   = "crawler",
            IsCrawled         = true,
            CrawlerSourceId   = crawled.SourceId,
            CrawlerSourceName = crawled.SourceName,
            CrawlerSourceUrl  = crawled.SourceUrl,
            CrawlerAttribution = attribution,
            CrawledAt         = DateTime.UtcNow,
            ContactEmail      = crawled.ContactEmail,
            ContactPhone      = crawled.ContactPhone,
            ContactUrl        = crawled.ContactUrl,
        };

        return await CreateEventAsync(evt, cancellationToken);
    }
}
