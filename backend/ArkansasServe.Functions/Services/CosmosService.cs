using ArkansasServe.Functions.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Services;

/// <summary>
/// Single service that owns all Cosmos DB reads and writes.
/// Functions never touch CosmosClient directly — they call this service.
/// </summary>
public class CosmosService
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
        _databaseName = config["CosmosDb__DatabaseName"] ?? "arkansas-serve-db";
        _logger = logger;
    }

    // ── Users ──────────────────────────────────────────────────────────────────

    public async Task<User?> GetUserByExternalIdAsync(string externalId, string tenantId)
    {
        var query = Users.GetItemLinqQueryable<User>()
            .Where(u => u.ExternalId == externalId && u.TenantId == tenantId)
            .ToFeedIterator();

        while (query.HasMoreResults)
        {
            var response = await query.ReadNextAsync();
            var user = response.FirstOrDefault();
            if (user != null) return user;
        }
        return null;
    }

    public async Task<User> UpsertUserAsync(User user)
    {
        user.UpdatedAt = DateTime.UtcNow;
        var response = await Users.UpsertItemAsync(user, new PartitionKey(user.TenantId));
        return response.Resource;
    }

    public async Task<List<User>> GetUsersByTenantAsync(string tenantId)
    {
        var query = Users.GetItemLinqQueryable<User>(requestOptions: new QueryRequestOptions
            { PartitionKey = new PartitionKey(tenantId) })
            .Where(u => u.Status == "active")
            .ToFeedIterator();

        var results = new List<User>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync());
        return results;
    }

    // ── Events ─────────────────────────────────────────────────────────────────

    public async Task<Event> CreateEventAsync(Event evt)
    {
        var response = await Events.CreateItemAsync(evt, new PartitionKey(evt.OrganizationId));
        return response.Resource;
    }

    public async Task<Event?> GetEventAsync(string eventId, string organizationId)
    {
        try
        {
            var response = await Events.ReadItemAsync<Event>(eventId, new PartitionKey(organizationId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<(Event? Event, string? ETag)> GetEventWithETagAsync(string eventId, string organizationId)
    {
        try
        {
            var response = await Events.ReadItemAsync<Event>(eventId, new PartitionKey(organizationId));
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
    public async Task<Event> UpdateEventAsync(Event evt, string? etag = null)
    {
        evt.UpdatedAt = DateTime.UtcNow;
        var options = etag != null ? new ItemRequestOptions { IfMatchEtag = etag } : null;
        var response = await Events.ReplaceItemAsync(evt, evt.Id, new PartitionKey(evt.OrganizationId), options);
        return response.Resource;
    }

    /// <summary>
    /// Returns upcoming open events visible to a given school.
    /// Cross-partition query — acceptable at current scale on 1000 RU/s.
    /// </summary>
    public async Task<List<Event>> GetUpcomingEventsAsync(string? schoolId = null)
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
            results.AddRange(await iterator.ReadNextAsync());

        return results.OrderBy(e => e.StartDateTime).ToList();
    }

    public async Task<List<Event>> GetEventsByOrgAsync(string organizationId)
    {
        var query = Events.GetItemLinqQueryable<Event>(requestOptions: new QueryRequestOptions
            { PartitionKey = new PartitionKey(organizationId) })
            .ToFeedIterator();

        var results = new List<Event>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync());
        return results.OrderByDescending(e => e.StartDateTime).ToList();
    }

    // ── EventRegistrations ────────────────────────────────────────────────────

    public async Task<EventRegistration> CreateRegistrationAsync(EventRegistration reg)
    {
        var response = await Registrations.CreateItemAsync(reg, new PartitionKey(reg.EventId));
        return response.Resource;
    }

    public async Task<bool> IsAlreadyRegisteredAsync(string eventId, string userId)
    {
        var query = Registrations.GetItemLinqQueryable<EventRegistration>(requestOptions:
            new QueryRequestOptions { PartitionKey = new PartitionKey(eventId) })
            .Where(r => r.UserId == userId && r.Status != "Cancelled")
            .ToFeedIterator();

        while (query.HasMoreResults)
        {
            var response = await query.ReadNextAsync();
            if (response.Any()) return true;
        }
        return false;
    }

    public async Task<List<EventRegistration>> GetRegistrationsByEventAsync(string eventId)
    {
        var query = Registrations.GetItemLinqQueryable<EventRegistration>(requestOptions:
            new QueryRequestOptions { PartitionKey = new PartitionKey(eventId) })
            .Where(r => r.Status != "Cancelled")
            .ToFeedIterator();

        var results = new List<EventRegistration>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync());
        return results;
    }

    public async Task<EventRegistration> UpdateRegistrationAsync(EventRegistration reg)
    {
        reg.UpdatedAt = DateTime.UtcNow;
        var response = await Registrations.ReplaceItemAsync(reg, reg.Id, new PartitionKey(reg.EventId));
        return response.Resource;
    }

    // ── ServiceLogs ───────────────────────────────────────────────────────────

    public async Task<ServiceLog> CreateServiceLogAsync(ServiceLog log)
    {
        var response = await ServiceLogs.CreateItemAsync(log, new PartitionKey(log.StudentId));
        return response.Resource;
    }

    public async Task<ServiceLog?> GetServiceLogAsync(string id, string studentId)
    {
        try
        {
            var response = await ServiceLogs.ReadItemAsync<ServiceLog>(id, new PartitionKey(studentId));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ServiceLog> UpdateServiceLogAsync(ServiceLog log)
    {
        log.UpdatedAt = DateTime.UtcNow;
        var response = await ServiceLogs.ReplaceItemAsync(log, log.Id, new PartitionKey(log.StudentId));
        return response.Resource;
    }

    public async Task<List<ServiceLog>> GetServiceLogsByStudentAsync(string studentId)
    {
        var query = ServiceLogs.GetItemLinqQueryable<ServiceLog>(requestOptions:
            new QueryRequestOptions { PartitionKey = new PartitionKey(studentId) })
            .ToFeedIterator();

        var results = new List<ServiceLog>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync());
        return results.OrderByDescending(l => l.ServiceDate).ToList();
    }

    // ── PendingApprovals ──────────────────────────────────────────────────────

    public async Task<PendingApproval> CreatePendingApprovalAsync(PendingApproval approval)
    {
        var response = await PendingApprovals.CreateItemAsync(approval, new PartitionKey(approval.SchoolId));
        return response.Resource;
    }

    public async Task DeletePendingApprovalByLogIdAsync(string serviceLogId, string schoolId)
    {
        var query = PendingApprovals.GetItemLinqQueryable<PendingApproval>(requestOptions:
            new QueryRequestOptions { PartitionKey = new PartitionKey(schoolId) })
            .Where(p => p.ServiceLogId == serviceLogId)
            .ToFeedIterator();

        while (query.HasMoreResults)
        {
            var response = await query.ReadNextAsync();
            foreach (var item in response)
                await PendingApprovals.DeleteItemAsync<PendingApproval>(item.Id, new PartitionKey(schoolId));
        }
    }

    public async Task<List<PendingApproval>> GetPendingApprovalsBySchoolAsync(string schoolId)
    {
        var query = PendingApprovals.GetItemLinqQueryable<PendingApproval>(requestOptions:
            new QueryRequestOptions { PartitionKey = new PartitionKey(schoolId) })
            .ToFeedIterator();

        var results = new List<PendingApproval>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync());
        return results.OrderByDescending(p => p.ServiceDate).ToList();
    }

    // ── Notifications ─────────────────────────────────────────────────────────

    public async Task CreateNotificationAsync(Notification notification)
    {
        await Notifications.CreateItemAsync(notification, new PartitionKey(notification.UserId));
    }

    public async Task<List<Notification>> GetNotificationsForUserAsync(string userId)
    {
        var query = Notifications.GetItemLinqQueryable<Notification>(requestOptions:
            new QueryRequestOptions { PartitionKey = new PartitionKey(userId) })
            .Where(n => !n.IsRead)
            .ToFeedIterator();

        var results = new List<Notification>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync());
        return results;
    }

    // ── Tenants ───────────────────────────────────────────────────────────────

    public async Task<Tenant> CreateTenantAsync(Tenant tenant)
    {
        var response = await Tenants.CreateItemAsync(tenant, new PartitionKey(tenant.Id));
        return response.Resource;
    }

    public async Task<List<Tenant>> GetAllTenantsAsync()
    {
        var query = Tenants.GetItemLinqQueryable<Tenant>().ToFeedIterator();
        var results = new List<Tenant>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync());
        return results.OrderBy(t => t.Name).ToList();
    }

    public async Task<Tenant?> GetTenantAsync(string id)
    {
        try
        {
            var response = await Tenants.ReadItemAsync<Tenant>(id, new PartitionKey(id));
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
