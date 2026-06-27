using ArkansasServe.Functions.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Services;

public partial class CosmosService
{
    // Compatibility-safe upcoming-events query that tolerates legacy or malformed
    // eligibleSchoolIds values instead of failing the whole endpoint.
    public async Task<List<Event>> GetUpcomingEventsCompatAsync(string? schoolId = null, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var sql = schoolId == null
            ? "SELECT * FROM c WHERE IS_DEFINED(c.startDateTime) AND c.startDateTime >= @now AND c.status = 'Open'"
            : "SELECT * FROM c WHERE IS_DEFINED(c.startDateTime) AND c.startDateTime >= @now AND c.status = 'Open' AND (NOT IS_DEFINED(c.eligibleSchoolIds) OR NOT IS_ARRAY(c.eligibleSchoolIds) OR ARRAY_LENGTH(c.eligibleSchoolIds) = 0 OR ARRAY_CONTAINS(c.eligibleSchoolIds, @schoolId))";

        var queryDef = new QueryDefinition(sql)
            .WithParameter("@now", now.ToString("o"));

        if (schoolId != null)
            queryDef = queryDef.WithParameter("@schoolId", schoolId);

        try
        {
            var results = new List<Event>();
            var iterator = Events.GetItemQueryIterator<Event>(queryDef);
            while (iterator.HasMoreResults)
                results.AddRange(await iterator.ReadNextAsync(cancellationToken));

            return results.OrderBy(e => e.StartDateTime).ToList();
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Failed to query upcoming events for schoolId {SchoolId}", schoolId ?? "(none)");
            throw;
        }
    }
}
