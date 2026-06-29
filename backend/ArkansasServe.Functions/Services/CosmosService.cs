using ArkansasServe.Functions.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using User = ArkansasServe.Functions.Models.User;

namespace ArkansasServe.Functions.Services;

/// <summary>
/// Single service that owns all Cosmos DB reads and writes.
/// Functions never touch CosmosClient directly — they call this service.
/// </summary>
public partial class CosmosService
{
    private readonly CosmosClient _client;
    private readonly string _databaseName;
    private readonly ILogger<CosmosService> _logger;

    // Container references
    private Container Tenants           => _client.GetContainer(_databaseName, "Tenants");
    private Container Users             => _client.GetContainer(_databaseName, "Users");
    private Container Events            => _client.GetContainer(_databaseName, "Events");
    private Container Registrations     => _client.GetContainer(_databaseName, "EventRegistrations");
    private Container ServiceLogs       => _client.GetContainer(_databaseName, "ServiceLogs");
    private Container PendingApprovals  => _client.GetContainer(_databaseName, "PendingApprovals");
    private Container Notifications     => _client.GetContainer(_databaseName, "Notifications");

    public CosmosService(CosmosClient client, IConfiguration config, ILogger<CosmosService> logger)
    {
        _client = client;
        _databaseName = config["CosmosDb__DatabaseName"]
            ?? config["CosmosDb:DatabaseName"]
            ?? throw new InvalidOperationException("CosmosDb__DatabaseName is not set.");
        _logger = logger;
    }

    // ── Users ──────────────────────────────────────────────────────────────────

    public async Task<User?> GetUserByExternalIdAsync(string externalId, string tenantId, CancellationToken cancellationToken = default)
    {
        var query = Users.GetItemLinqQueryable<User>()
            .Where(u => u.ExternalId == externalId && u.TenantId == tenantId)
            .ToFeedIterator();

        while (query.HasMoreResults)
        {
            var response = await query.ReadNextAsync(cancellationToken);
            var user = response.FirstOrDefault();
            if (user != null) return user;
        }
        return null;
    }

    public async Task<User?> GetUserByIdAsync(string userId, string tenantId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Users.ReadItemAsync<User>(userId, new PartitionKey(tenantId), cancellationToken: cancellationToken);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<User> UpsertUserAsync(User user, CancellationToken cancellationToken = default)
    {
        user.UpdatedAt = DateTime.UtcNow;
        var response = await Users.UpsertItemAsync(user, new PartitionKey(user.TenantId), cancellationToken: cancellationToken);
        return response.Resource;
    }

    public async Task<List<User>> GetUsersByTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var query = Users.GetItemLinqQueryable<User>(requestOptions: new QueryRequestOptions
            { PartitionKey = new PartitionKey(tenantId) })
            .Where(u => u.Status == "active")
            .ToFeedIterator();

        var results = new List<User>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(cancellationToken));
        return results;
    }

    public async Task<List<User>> GetUsersForAdminScopeAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var query = Users.GetItemLinqQueryable<User>(requestOptions: new QueryRequestOptions
            { PartitionKey = new PartitionKey(tenantId) })
            .Where(u => u.Status == "active")
            .ToFeedIterator();

        var results = new List<User>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(cancellationToken));

        return results
            .OrderBy(u => u.DisplayName)
            .ToList();
    }

    public async Task<List<User>> GetDemoUsersByTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var query = Users.GetItemLinqQueryable<User>(requestOptions: new QueryRequestOptions
            { PartitionKey = new PartitionKey(tenantId) })
            .Where(u => u.IsDemoUser)
            .ToFeedIterator();

        var results = new List<User>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(cancellationToken));

        return results
            .OrderBy(u => u.AdminLevel)
            .ThenBy(u => u.DisplayName)
            .ToList();
    }

    public async Task DeleteDemoUsersByTenantAsync(string tenantId, CancellationToken cancellationToken = default)
    {
        var demoUsers = await GetDemoUsersByTenantAsync(tenantId, cancellationToken);
        foreach (var demoUser in demoUsers)
            await Users.DeleteItemAsync<User>(demoUser.Id, new PartitionKey(tenantId), cancellationToken: cancellationToken);
    }

    public async Task<List<User>> UpsertDemoUsersAsync(string tenantId, IEnumerable<User> demoUsers, CancellationToken cancellationToken = default)
    {
        var created = new List<User>();
        foreach (var demoUser in demoUsers)
        {
            demoUser.TenantId = tenantId;
            demoUser.IsDemoUser = true;
            demoUser.Status = "active";
            created.Add(await UpsertUserAsync(demoUser, cancellationToken));
        }

        return created;
    }

    // ── Events ─────────────────────────────────────────────────────────────────

    public async Task<Event> CreateEventAsync(Event evt, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Events.CreateItemAsync(evt, new PartitionKey(evt.OrganizationId), cancellationToken: cancellationToken);
            return response.Resource;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Failed to create event {EventId} for organization {OrganizationId}", evt.Id, evt.OrganizationId);
            throw;
        }
    }

    public async Task<Event?> GetEventAsync(string eventId, string organizationId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Events.ReadItemAsync<Event>(eventId, new PartitionKey(organizationId), cancellationToken: cancellationToken);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<(Event? Event, string? ETag)> GetEventWithETagAsync(string eventId, string organizationId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Events.ReadItemAsync<Event>(eventId, new PartitionKey(organizationId), cancellationToken: cancellationToken);
            return (response.Resource, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Replaces an event document. When <paramref name="etag"/> is supplied the request uses
    /// an <c>If-Match</c> condition; if another writer has modified the document in the
    /// meantime Cosmos DB throws <see cref="CosmosException"/> with
    /// <see cref="System.Net.HttpStatusCode.PreconditionFailed"/> (412), which callers
    /// should catch and retry.
    /// </summary>
    public async Task<Event> UpdateEventAsync(Event evt, string? etag = null, CancellationToken cancellationToken = default)
    {
        evt.UpdatedAt = DateTime.UtcNow;
        var options = etag != null ? new ItemRequestOptions { IfMatchEtag = etag } : null;
        try
        {
            var response = await Events.ReplaceItemAsync(evt, evt.Id, new PartitionKey(evt.OrganizationId), options, cancellationToken);
            return response.Resource;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Failed to update event {EventId} for organization {OrganizationId}", evt.Id, evt.OrganizationId);
            throw;
        }
    }

    /// <summary>
    /// Returns upcoming open events visible to a given school.
    /// Cross-partition query — acceptable at current scale on 1000 RU/s.
    /// </summary>
    public async Task<List<Event>> GetUpcomingEventsAsync(string? schoolId = null, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        // Build query string — LINQ cross-partition doesn't support .Contains well
        var sql = schoolId == null
            ? "SELECT * FROM c WHERE c.startDateTime >= @now AND c.status = 'Open'"
            : "SELECT * FROM c WHERE c.startDateTime >= @now AND c.status = 'Open' AND (ARRAY_LENGTH(c.eligibleSchoolIds) = 0 OR ARRAY_CONTAINS(c.eligibleSchoolIds, @schoolId))";

        var queryDef = new QueryDefinition(sql)
            .WithParameter("@now", now.ToString("o"));

        if (schoolId != null)
            queryDef = queryDef.WithParameter("@schoolId", schoolId);

        var results = new List<Event>();
        var iterator = Events.GetItemQueryIterator<Event>(queryDef);
        while (iterator.HasMoreResults)
            results.AddRange(await iterator.ReadNextAsync(cancellationToken));

        return results.OrderBy(e => e.StartDateTime).ToList();
    }

    public async Task<List<Event>> GetEventsByOrgAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        var query = Events.GetItemLinqQueryable<Event>(requestOptions: new QueryRequestOptions
            { PartitionKey = new PartitionKey(organizationId) })
            .ToFeedIterator();

        var results = new List<Event>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(cancellationToken));
        return results.OrderByDescending(e => e.StartDateTime).ToList();
    }

    // ── EventRegistrations ────────────────────────────────────────────────────

    public async Task<EventRegistration> CreateRegistrationAsync(EventRegistration reg, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Registrations.CreateItemAsync(reg, new PartitionKey(reg.EventId), cancellationToken: cancellationToken);
            return response.Resource;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Failed to create registration {RegistrationId} for event {EventId}", reg.Id, reg.EventId);
            throw;
        }
    }

    public async Task<bool> IsAlreadyRegisteredAsync(string eventId, string userId, CancellationToken cancellationToken = default)
    {
        var query = Registrations.GetItemLinqQueryable<EventRegistration>(requestOptions:
            new QueryRequestOptions { PartitionKey = new PartitionKey(eventId) })
            .Where(r => r.UserId == userId && r.Status != "Cancelled")
            .ToFeedIterator();

        while (query.HasMoreResults)
        {
            var response = await query.ReadNextAsync(cancellationToken);
            if (response.Any()) return true;
        }
        return false;
    }

    public async Task<List<EventRegistration>> GetRegistrationsByEventAsync(string eventId, CancellationToken cancellationToken = default)
    {
        var query = Registrations.GetItemLinqQueryable<EventRegistration>(requestOptions:
            new QueryRequestOptions { PartitionKey = new PartitionKey(eventId) })
            .Where(r => r.Status != "Cancelled")
            .ToFeedIterator();

        var results = new List<EventRegistration>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(cancellationToken));
        return results;
    }

    public async Task<EventRegistration> UpdateRegistrationAsync(EventRegistration reg, CancellationToken cancellationToken = default)
    {
        reg.UpdatedAt = DateTime.UtcNow;
        try
        {
            var response = await Registrations.ReplaceItemAsync(reg, reg.Id, new PartitionKey(reg.EventId), cancellationToken: cancellationToken);
            return response.Resource;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Failed to update registration {RegistrationId} for event {EventId}", reg.Id, reg.EventId);
            throw;
        }
    }

    public async Task<EventRegistration?> GetRegistrationAsync(string id, string eventId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Registrations.ReadItemAsync<EventRegistration>(id, new PartitionKey(eventId), cancellationToken: cancellationToken);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // ── ServiceLogs ───────────────────────────────────────────────────────────

    public async Task<ServiceLog> CreateServiceLogAsync(ServiceLog log, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await ServiceLogs.CreateItemAsync(log, new PartitionKey(log.StudentId), cancellationToken: cancellationToken);
            return response.Resource;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Failed to create service log {ServiceLogId} for student {StudentId}", log.Id, log.StudentId);
            throw;
        }
    }

    public async Task<ServiceLog?> GetServiceLogAsync(string id, string studentId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await ServiceLogs.ReadItemAsync<ServiceLog>(id, new PartitionKey(studentId), cancellationToken: cancellationToken);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ServiceLog> UpdateServiceLogAsync(ServiceLog log, CancellationToken cancellationToken = default)
    {
        log.UpdatedAt = DateTime.UtcNow;
        try
        {
            var response = await ServiceLogs.ReplaceItemAsync(log, log.Id, new PartitionKey(log.StudentId), cancellationToken: cancellationToken);
            return response.Resource;
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Failed to update service log {ServiceLogId} for student {StudentId}", log.Id, log.StudentId);
            throw;
        }
    }

    public async Task<List<ServiceLog>> GetServiceLogsByStudentAsync(string studentId, CancellationToken cancellationToken = default)
    {
        var query = ServiceLogs.GetItemLinqQueryable<ServiceLog>(requestOptions:
            new QueryRequestOptions { PartitionKey = new PartitionKey(studentId) })
            .ToFeedIterator();

        var results = new List<ServiceLog>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(cancellationToken));
        return results.OrderByDescending(l => l.ServiceDate).ToList();
    }

    // ── PendingApprovals ──────────────────────────────────────────────────────

    public async Task<PendingApproval> CreatePendingApprovalAsync(PendingApproval approval, CancellationToken cancellationToken = default)
    {
        var response = await PendingApprovals.CreateItemAsync(approval, new PartitionKey(approval.SchoolId), cancellationToken: cancellationToken);
        return response.Resource;
    }

    public async Task DeletePendingApprovalByLogIdAsync(string serviceLogId, string schoolId, CancellationToken cancellationToken = default)
    {
        var query = PendingApprovals.GetItemLinqQueryable<PendingApproval>(requestOptions:
            new QueryRequestOptions { PartitionKey = new PartitionKey(schoolId) })
            .Where(p => p.ServiceLogId == serviceLogId)
            .ToFeedIterator();

        while (query.HasMoreResults)
        {
            var response = await query.ReadNextAsync(cancellationToken);
            foreach (var item in response)
                await PendingApprovals.DeleteItemAsync<PendingApproval>(item.Id, new PartitionKey(schoolId), cancellationToken: cancellationToken);
        }
    }

    public async Task<List<PendingApproval>> GetPendingApprovalsBySchoolAsync(string schoolId, CancellationToken cancellationToken = default)
    {
        var query = PendingApprovals.GetItemLinqQueryable<PendingApproval>(requestOptions:
            new QueryRequestOptions { PartitionKey = new PartitionKey(schoolId) })
            .ToFeedIterator();

        var results = new List<PendingApproval>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(cancellationToken));
        return results.OrderByDescending(p => p.ServiceDate).ToList();
    }

    public async Task<List<PendingApproval>> GetAllPendingApprovalsAsync(CancellationToken cancellationToken = default)
    {
        var query = PendingApprovals.GetItemLinqQueryable<PendingApproval>()
            .ToFeedIterator();

        var results = new List<PendingApproval>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(cancellationToken));
        return results.OrderByDescending(p => p.ServiceDate).ToList();
    }

    // ── Notifications ─────────────────────────────────────────────────────────

    public async Task CreateNotificationAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        await Notifications.CreateItemAsync(notification, new PartitionKey(notification.UserId), cancellationToken: cancellationToken);
    }

    public async Task<List<Notification>> GetNotificationsForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        var query = Notifications.GetItemLinqQueryable<Notification>(requestOptions:
            new QueryRequestOptions { PartitionKey = new PartitionKey(userId) })
            .Where(n => !n.IsRead)
            .ToFeedIterator();

        var results = new List<Notification>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(cancellationToken));
        return results;
    }

    // ── Tenants ───────────────────────────────────────────────────────────────

    public async Task<Tenant> CreateTenantAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        var response = await Tenants.CreateItemAsync(tenant, new PartitionKey(tenant.Id), cancellationToken: cancellationToken);
        return response.Resource;
    }

    public async Task<List<Tenant>> GetAllTenantsAsync(CancellationToken cancellationToken = default)
    {
        var query = Tenants.GetItemLinqQueryable<Tenant>().ToFeedIterator();
        var results = new List<Tenant>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(cancellationToken));
        return results.OrderBy(t => t.Name).ToList();
    }

    public async Task<Tenant?> GetTenantAsync(string id, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Tenants.ReadItemAsync<Tenant>(id, new PartitionKey(id), cancellationToken: cancellationToken);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Tenant> UpdateTenantAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        tenant.UpdatedAt = DateTime.UtcNow;
        var response = await Tenants.ReplaceItemAsync(tenant, tenant.Id, new PartitionKey(tenant.Id), cancellationToken: cancellationToken);
        return response.Resource;
    }
}
