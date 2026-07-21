using ArkansasServe.Functions.Functions;
using ArkansasServe.Functions.Models;
using Microsoft.Azure.Cosmos;

namespace ArkansasServe.Functions.Services;

// Guardian data access (#20). Guardians live in the Users CONTAINER, in the reserved
// `guardians` partition — containers are Bicep-defined and the deploy does not run Bicep, so a
// new one cannot be added without infra work. Every read here filters on docType as well as
// the partition, so isolation from member records never rests on the partition name alone.
public partial class CosmosService
{
    private static readonly PartitionKey GuardianPartition = new(TenantIds.Guardians);

    /// <summary>
    /// The guardian for this email, or null. Email is the natural key and is matched
    /// LOWERCASED — a parent typing "J.Doe@example.com" on a second school's form must resolve
    /// to the same record, not a duplicate with a separate inbox and separate consents.
    /// </summary>
    public async Task<Guardian?> GetGuardianByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;

        var query = Users.GetItemQueryIterator<Guardian>(
            new QueryDefinition(
                "SELECT * FROM c WHERE c.docType = @t AND c.email = @e")
                .WithParameter("@t", GuardianDocType.Value)
                .WithParameter("@e", email.Trim().ToLowerInvariant()),
            requestOptions: new QueryRequestOptions { PartitionKey = GuardianPartition });

        while (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync(cancellationToken);
            var hit = page.FirstOrDefault();
            if (hit != null) return hit;
        }
        return null;
    }

    public async Task<Guardian?> GetGuardianByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try
        {
            var res = await Users.ReadItemAsync<Guardian>(id, GuardianPartition, cancellationToken: cancellationToken);
            // A member id read from the guardians partition cannot happen, but a stale id can —
            // and a document without the discriminator is not a guardian whatever else it is.
            return string.Equals(res.Resource?.DocType, GuardianDocType.Value, StringComparison.Ordinal)
                ? res.Resource : null;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds a guardian by the HASH of a presented token. The raw token is never stored, so the
    /// caller hashes what it was given and looks that up — a lookup that fails is
    /// indistinguishable from a token that never existed, which is the point.
    /// </summary>
    public async Task<Guardian?> GetGuardianByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tokenHash)) return null;

        var query = Users.GetItemQueryIterator<Guardian>(
            new QueryDefinition(
                "SELECT * FROM c WHERE c.docType = @t AND c.magicLink.tokenHash = @h")
                .WithParameter("@t", GuardianDocType.Value)
                .WithParameter("@h", tokenHash),
            requestOptions: new QueryRequestOptions { PartitionKey = GuardianPartition });

        while (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync(cancellationToken);
            var hit = page.FirstOrDefault();
            if (hit != null) return hit;
        }
        return null;
    }

    public async Task<Guardian> UpsertGuardianAsync(Guardian guardian, CancellationToken cancellationToken = default)
    {
        // Defended rather than assumed: a guardian written into the wrong partition, or without
        // the discriminator, becomes an invisible record that member queries may then pick up.
        guardian.TenantId = TenantIds.Guardians;
        guardian.DocType = GuardianDocType.Value;
        guardian.Email = (guardian.Email ?? string.Empty).Trim().ToLowerInvariant();
        guardian.UpdatedAt = DateTime.UtcNow;

        var res = await Users.UpsertItemAsync(guardian, GuardianPartition, cancellationToken: cancellationToken);
        return res.Resource;
    }

    /// <summary>Every guardian linked to a given minor, in a given org. Usually one, sometimes two.</summary>
    public async Task<List<Guardian>> GetGuardiansForMinorAsync(string minorUserId, string organizationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(minorUserId)) return [];

        var query = Users.GetItemQueryIterator<Guardian>(
            new QueryDefinition(
                "SELECT * FROM c WHERE c.docType = @t AND EXISTS("
                + "SELECT VALUE l FROM l IN c.links WHERE l.minorUserId = @m AND l.organizationId = @o)")
                .WithParameter("@t", GuardianDocType.Value)
                .WithParameter("@m", minorUserId)
                .WithParameter("@o", organizationId),
            requestOptions: new QueryRequestOptions { PartitionKey = GuardianPartition });

        var results = new List<Guardian>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(cancellationToken));
        return results;
    }
}
