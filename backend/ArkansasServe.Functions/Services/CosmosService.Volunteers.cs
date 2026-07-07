using ArkansasServe.Functions.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using User = ArkansasServe.Functions.Models.User;

namespace ArkansasServe.Functions.Services;

// Membership + managed-volunteer data access. A person is represented by one
// User document per organization (a "membership"); the same person is unified by
// ExternalId (once signed in) or Email (for managed volunteers).
public partial class CosmosService
{
    // Every org membership for a person, across partitions. Empty externalId is
    // never matched (managed-only volunteers have no externalId yet).
    public async Task<List<User>> GetMembershipsByExternalIdAsync(string externalId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalId)) return [];

        var query = Users.GetItemLinqQueryable<User>()
            .Where(u => u.ExternalId == externalId)
            .ToFeedIterator();

        var results = new List<User>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(cancellationToken));
        return results;
    }

    // A membership found by email within one org (email is unique per org).
    public async Task<User?> GetMembershipByEmailAsync(string email, string tenantId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;

        var query = Users.GetItemLinqQueryable<User>()
            .Where(u => u.TenantId == tenantId && u.Email == email)
            .ToFeedIterator();

        while (query.HasMoreResults)
        {
            var page = await query.ReadNextAsync(cancellationToken);
            var found = page.FirstOrDefault();
            if (found != null) return found;
        }
        return null;
    }

    // Volunteers (Student level) in an org, optionally narrowed to one group.
    public async Task<List<User>> GetVolunteersByTenantAsync(string tenantId, string? groupId = null, CancellationToken cancellationToken = default)
    {
        var query = Users.GetItemLinqQueryable<User>(requestOptions: new QueryRequestOptions
            { PartitionKey = new PartitionKey(tenantId) })
            .Where(u => u.Status == "active" && u.AdminLevel == "Student")
            .ToFeedIterator();

        var results = new List<User>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(cancellationToken));

        if (!string.IsNullOrWhiteSpace(groupId))
            results = results.Where(u => u.GroupIds.Contains(groupId)).ToList();

        return results.OrderBy(u => u.DisplayName).ToList();
    }

    public async Task<User> CreateManagedVolunteerAsync(User volunteer, CancellationToken cancellationToken = default)
    {
        return await UpsertUserWithPartitionFallbackAsync(volunteer, cancellationToken);
    }

    // The acting user's membership within a specific org — their role and groups
    // THERE (multi-org: a person may be an admin in one org and a student in
    // another). Platform admins may act in any org even without a membership doc;
    // everyone else only where they actually hold a membership (else null).
    public async Task<User?> ResolveActorInOrgAsync(string externalId, string tokenRole, string orgId, CancellationToken cancellationToken = default)
    {
        var isSuper = string.Equals(tokenRole, "PlatformAdmin", StringComparison.OrdinalIgnoreCase);

        var membership = await GetUserByExternalIdAsync(externalId, orgId, cancellationToken);
        if (membership != null)
        {
            if (isSuper && !string.Equals(membership.AdminLevel, "SuperAdmin", StringComparison.OrdinalIgnoreCase))
                membership.AdminLevel = "SuperAdmin";
            return membership;
        }

        if (isSuper)
        {
            return new User
            {
                ExternalId = externalId,
                TenantId = orgId,
                OrganizationId = orgId,
                Role = "PlatformAdmin",
                AdminLevel = "SuperAdmin",
                Status = "active",
            };
        }

        return null;
    }
}
