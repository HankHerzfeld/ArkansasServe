using System.Net;
using ArkansasServe.Functions.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using User = ArkansasServe.Functions.Models.User;

namespace ArkansasServe.Functions.Services;

public partial class CosmosService
{
    // Compatibility shim for environments where the users container was created with /id
    // instead of /tenantId as partition key.
    public async Task<User> UpsertUserWithPartitionFallbackAsync(User user, CancellationToken cancellationToken = default)
    {
        user.UpdatedAt = DateTime.UtcNow;

        try
        {
            var response = await Users.UpsertItemAsync(user, new PartitionKey(user.TenantId), cancellationToken: cancellationToken);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            if (!LooksLikePartitionKeyMismatch(ex)) throw;

            _logger.LogWarning(
                "Users upsert retried with /id partition key for user {UserId}; tenant key write failed.",
                user.Id);

            var fallbackResponse = await Users.UpsertItemAsync(user, new PartitionKey(user.Id), cancellationToken: cancellationToken);
            return fallbackResponse.Resource;
        }
    }

    private static bool LooksLikePartitionKeyMismatch(CosmosException ex)
    {
        return ex.Message.Contains("PartitionKey extracted from document", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("partition key path", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("doesn't match", StringComparison.OrdinalIgnoreCase);
    }
}
