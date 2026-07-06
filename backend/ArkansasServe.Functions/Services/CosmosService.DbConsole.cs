using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace ArkansasServe.Functions.Services;

// SuperAdmin-only manual database console. Read path only: the Cosmos SQL API
// cannot INSERT/UPDATE/DELETE, so a query here can never mutate data. Container
// access is allow-listed to the seven known containers.
public partial class CosmosService
{
    // Logical container names a SuperAdmin may query from the DB console.
    public IReadOnlyList<string> QueryableContainers =>
    [
        "Tenants", "Users", "Events", "Registrations",
        "ServiceLogs", "PendingApprovals", "Notifications",
    ];

    public async Task<List<JsonElement>> RunReadQueryAsync(string container, string queryText, int maxItems, CancellationToken cancellationToken = default)
    {
        var target = ResolveQueryableContainer(container);
        var cap = Math.Clamp(maxItems, 1, 200);

        var results = new List<JsonElement>();
        using var iterator = target.GetItemQueryStreamIterator(
            new QueryDefinition(queryText),
            requestOptions: new QueryRequestOptions { MaxItemCount = cap });

        while (iterator.HasMoreResults && results.Count < cap)
        {
            using var response = await iterator.ReadNextAsync(cancellationToken);
            response.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken);
            if (doc.RootElement.TryGetProperty("Documents", out var documents))
            {
                foreach (var item in documents.EnumerateArray())
                {
                    results.Add(item.Clone());
                    if (results.Count >= cap) break;
                }
            }
        }

        return results;
    }

    private Container ResolveQueryableContainer(string logicalName)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Tenants"] = _tenantsContainerName,
            ["Users"] = _usersContainerName,
            ["Events"] = _eventsContainerName,
            ["Registrations"] = _registrationsContainerName,
            ["ServiceLogs"] = _serviceLogsContainerName,
            ["PendingApprovals"] = _pendingApprovalsContainerName,
            ["Notifications"] = _notificationsContainerName,
        };

        if (!map.TryGetValue(logicalName, out var actual))
            throw new ArgumentException($"Unknown or non-queryable container '{logicalName}'.");

        return _client.GetContainer(_databaseName, actual);
    }
}
