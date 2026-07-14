using ArkansasServe.Functions.Functions;
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

    // Propagate a person's NAME to their other org memberships.
    //
    // A person is one User doc per org, and each doc carried its own name — set from
    // whatever that org's admin typed, or from the token claim (which can read "unknown").
    // Editing your profile only ever wrote your home-org doc, so the same person rendered
    // under different names depending on which org a page read. A person's name belongs to
    // the person, not the org, so it is synced across all of their memberships.
    //
    // Deliberately NAME-ONLY. AdminLevel, GroupIds, EventAdminEventIds, TotalApprovedHours,
    // BackgroundCheckStatus, Grade, Status and SelfJoined are genuinely per-org — copying
    // those across orgs would corrupt roles and hours, so they are left untouched.
    //
    // Best-effort and non-transactional: callers must not let a failure here fail the
    // profile save. Returns how many other memberships were updated.
    public async Task<int> SyncNameAcrossMembershipsAsync(
        string externalId,
        string? firstName,
        string? lastName,
        string displayName,
        string? skipTenantId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(displayName)) return 0;

        var memberships = await GetMembershipsByExternalIdAsync(externalId, cancellationToken);
        var updated = 0;

        foreach (var m in memberships)
        {
            // The caller has already written this one.
            if (!string.IsNullOrWhiteSpace(skipTenantId)
                && string.Equals(m.TenantId, skipTenantId, StringComparison.Ordinal))
                continue;

            // Skip no-op writes so a profile save doesn't churn every partition.
            if (string.Equals(m.DisplayName, displayName, StringComparison.Ordinal)
                && string.Equals(m.FirstName, firstName, StringComparison.Ordinal)
                && string.Equals(m.LastName, lastName, StringComparison.Ordinal))
                continue;

            m.FirstName = firstName;
            m.LastName = lastName;
            m.DisplayName = displayName;
            await UpsertUserWithPartitionFallbackAsync(m, cancellationToken);
            updated++;
        }

        return updated;
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

    // Recently self-joined volunteers in an org (for the admin notification pane).
    public async Task<List<User>> GetRecentSelfJoinsAsync(string tenantId, DateTime since, int max = 5, CancellationToken cancellationToken = default)
    {
        var query = Users.GetItemLinqQueryable<User>(requestOptions: new QueryRequestOptions
            { PartitionKey = new PartitionKey(tenantId) })
            .Where(u => u.SelfJoined && u.CreatedAt >= since)
            .ToFeedIterator();

        var results = new List<User>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(cancellationToken));

        return results.OrderByDescending(u => u.CreatedAt).Take(max).ToList();
    }

    // The acting user's membership within a specific org — their role and groups
    // THERE (multi-org: a person may be an admin in one org and a student in
    // another). Platform admins may act in any org even without a membership doc;
    // everyone else only where they actually hold a membership (else null).
    public async Task<User?> ResolveActorInOrgAsync(string externalId, string tokenAdminLevel, string orgId, CancellationToken cancellationToken = default)
    {
        // A global super — by token claim OR a SuperAdmin membership in ANY org — may act
        // as super in EVERY org, INCLUDING one where they happen to hold a lower membership
        // (e.g. a Student self-join). Two ways to get this wrong, both of which caused 403s:
        //   • trusting only the token claim misses membership-based supers (their token
        //     carries no super claim);
        //   • trusting only the per-org membership level caps a global super at whatever
        //     low role they hold in that specific org.
        var tokenSuper = string.Equals(tokenAdminLevel, "SuperAdmin", StringComparison.OrdinalIgnoreCase);

        // Fast path: a token super needs only the single-partition membership lookup.
        if (tokenSuper)
        {
            var m = await GetUserByExternalIdAsync(externalId, orgId, cancellationToken);
            if (m != null)
            {
                if (!string.Equals(m.AdminLevel, "SuperAdmin", StringComparison.OrdinalIgnoreCase))
                    m.AdminLevel = "SuperAdmin";
                return m;
            }
            return SyntheticSuperActor(externalId, orgId);
        }

        // Otherwise one fan-out by externalId yields BOTH this org's membership AND whether
        // the caller is a global super (a SuperAdmin membership anywhere).
        var memberships = await GetMembershipsByExternalIdAsync(externalId, cancellationToken);
        var here = memberships.FirstOrDefault(u => string.Equals(u.TenantId, orgId, StringComparison.OrdinalIgnoreCase));
        var isGlobalSuper = memberships.Any(u => string.Equals(u.AdminLevel, "SuperAdmin", StringComparison.OrdinalIgnoreCase));

        if (here != null)
        {
            if (isGlobalSuper && !string.Equals(here.AdminLevel, "SuperAdmin", StringComparison.OrdinalIgnoreCase))
                here.AdminLevel = "SuperAdmin";
            return here;
        }
        return isGlobalSuper ? SyntheticSuperActor(externalId, orgId) : null;
    }

    private static User SyntheticSuperActor(string externalId, string orgId) => new()
    {
        ExternalId = externalId,
        TenantId = orgId,
        OrganizationId = orgId,
        AdminLevel = "SuperAdmin",
        Status = "active",
    };

    // True if the caller holds at least `minLevel` in ANY org (token claim or any
    // membership). Used only where the request doesn't identify a specific org
    // (e.g. an upload token, or registrations addressed by event id) — a global super
    // is covered because their SuperAdmin membership clears any minLevel.
    public async Task<bool> IsAtLeastInAnyOrgAsync(string externalId, string tokenAdminLevel, string minLevel, CancellationToken cancellationToken = default)
    {
        if (AdminLevels.AtLeast(tokenAdminLevel, minLevel)) return true;
        var memberships = await GetMembershipsByExternalIdAsync(externalId, cancellationToken);
        return memberships.Any(m => AdminLevels.AtLeast(m.AdminLevel, minLevel));
    }

    // Every user document across all orgs — for the SuperAdmin role matrix.
    public async Task<List<User>> GetAllUsersAsync(CancellationToken cancellationToken = default)
    {
        var query = Users.GetItemLinqQueryable<User>().ToFeedIterator();
        var results = new List<User>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(cancellationToken));
        return results;
    }

    // One page of membership documents for the role matrix, filtered server-side
    // by org and/or a name/email search so the matrix scales past a full-container
    // scan. When organizationId is set the query is scoped to that partition;
    // otherwise it pages across partitions with a continuation token. Returns the
    // page plus the token to fetch the next page (null when the last page).
    public async Task<(List<User> Items, string? ContinuationToken)> QueryMembershipsPageAsync(
        string? organizationId,
        string? search,
        int pageSize,
        string? continuationToken,
        CancellationToken cancellationToken = default)
    {
        var hasSearch = !string.IsNullOrWhiteSpace(search);

        var queryText = "SELECT * FROM c";
        if (hasSearch)
            queryText += " WHERE CONTAINS(LOWER(c.displayName), @q) OR CONTAINS(LOWER(c.email), @q)";
        queryText += " ORDER BY c.displayName";

        var queryDef = new QueryDefinition(queryText);
        if (hasSearch)
            queryDef = queryDef.WithParameter("@q", search!.Trim().ToLowerInvariant());

        var requestOptions = new QueryRequestOptions { MaxItemCount = pageSize };
        if (!string.IsNullOrWhiteSpace(organizationId))
            requestOptions.PartitionKey = new PartitionKey(organizationId);

        using var iterator = Users.GetItemQueryIterator<User>(
            queryDef,
            continuationToken: string.IsNullOrWhiteSpace(continuationToken) ? null : continuationToken,
            requestOptions: requestOptions);

        if (!iterator.HasMoreResults)
            return (new List<User>(), null);

        var page = await iterator.ReadNextAsync(cancellationToken);
        return (page.ToList(), page.ContinuationToken);
    }

    // Removes a membership document, tolerating the /id-vs-/tenantId partition quirk.
    public async Task DeleteUserWithFallbackAsync(string userId, string tenantId, CancellationToken cancellationToken = default)
    {
        try
        {
            await Users.DeleteItemAsync<User>(userId, new PartitionKey(tenantId), cancellationToken: cancellationToken);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await Users.DeleteItemAsync<User>(userId, new PartitionKey(userId), cancellationToken: cancellationToken);
        }
    }

    // A person is a platform super if their token level is SuperAdmin (from the
    // role claim or the email-domain bootstrap) or any membership is SuperAdmin.
    public async Task<bool> IsGlobalSuperAsync(string externalId, string tokenAdminLevel, CancellationToken cancellationToken = default)
    {
        if (string.Equals(tokenAdminLevel, "SuperAdmin", StringComparison.OrdinalIgnoreCase)) return true;
        var memberships = await GetMembershipsByExternalIdAsync(externalId, cancellationToken);
        return memberships.Any(m => string.Equals(m.AdminLevel, "SuperAdmin", StringComparison.OrdinalIgnoreCase));
    }
}
