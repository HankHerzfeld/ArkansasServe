using ArkansasServe.Functions.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Services;

public partial class CosmosService
{
    /// <summary>
    /// Flips every still-Open event whose end time has passed to "Archived" (stamping ArchivedAt).
    /// Cross-partition by design — it runs from a daily timer, not a request path — and idempotent:
    /// it only touches status='Open', so re-running never re-archives or disturbs Cancelled events.
    /// An event with no endDateTime is left alone (nothing to compare against).
    /// Returns the number archived.
    /// </summary>
    public async Task<int> ArchivePastEventsAsync(DateTime now, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT * FROM c WHERE c.status = 'Open' AND IS_DEFINED(c.endDateTime) AND c.endDateTime < @now";
        var queryDef = new QueryDefinition(sql).WithParameter("@now", now.ToString("o"));

        var due = new List<Event>();
        var iterator = Events.GetItemQueryIterator<Event>(queryDef);
        while (iterator.HasMoreResults)
            due.AddRange(await iterator.ReadNextAsync(cancellationToken));

        var archived = 0;
        foreach (var e in due)
        {
            e.Status = "Archived";
            e.ArchivedAt = now;
            try
            {
                // One at a time so a single bad document can't fail the whole sweep — the next
                // daily run picks up anything that errored here.
                await UpdateEventAsync(e, cancellationToken: cancellationToken);
                archived++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Archival] Could not archive event {EventId} (org {OrgId})", e.Id, e.OrganizationId);
            }
        }
        return archived;
    }
}
