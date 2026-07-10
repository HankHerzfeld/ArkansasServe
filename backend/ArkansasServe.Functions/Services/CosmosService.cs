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
    private readonly string _tenantsContainerName;
    private readonly string _usersContainerName;
    private readonly string _eventsContainerName;
    private readonly string _registrationsContainerName;
    private readonly string _serviceLogsContainerName;
    private readonly string _pendingApprovalsContainerName;
    private readonly string _notificationsContainerName;

    // Container references
    private Container Tenants           => _client.GetContainer(_databaseName, _tenantsContainerName);
    private Container Users             => _client.GetContainer(_databaseName, _usersContainerName);
    private Container Events            => _client.GetContainer(_databaseName, _eventsContainerName);
    private Container Registrations     => _client.GetContainer(_databaseName, _registrationsContainerName);
    private Container ServiceLogs       => _client.GetContainer(_databaseName, _serviceLogsContainerName);
    private Container PendingApprovals  => _client.GetContainer(_databaseName, _pendingApprovalsContainerName);
    private Container Notifications     => _client.GetContainer(_databaseName, _notificationsContainerName);

    public CosmosService(CosmosClient client, IConfiguration config, ILogger<CosmosService> logger)
    {
        _client = client;
        _databaseName = config["CosmosDb__DatabaseName"]
            ?? config["CosmosDb:DatabaseName"]
            ?? throw new InvalidOperationException("CosmosDb__DatabaseName is not set.");
        // Defaults MUST match the real (case-sensitive) Cosmos container names, or every
        // data operation targets a non-existent container (404s surfacing as 500s). The
        // CosmosDb__Containers__* app settings remain available as per-environment overrides.
        _tenantsContainerName = GetContainerName(config, "Tenants", "Tenants");
        _usersContainerName = GetContainerName(config, "Users", "Users");
        _eventsContainerName = GetContainerName(config, "Events", "Events");
        _registrationsContainerName = GetContainerName(config, "Registrations", "EventRegistrations");
        _serviceLogsContainerName = GetContainerName(config, "ServiceLogs", "ServiceLogs");
        _pendingApprovalsContainerName = GetContainerName(config, "PendingApprovals", "PendingApprovals");
        _notificationsContainerName = GetContainerName(config, "Notifications", "Notifications");
        _impersonationSessionsContainerName = GetContainerName(config, "ImpersonationSessions", "ImpersonationSessions");
        _auditEventsContainerName = GetContainerName(config, "AuditEvents", "AuditEvents");
        _logger = logger;
    }

    private static string GetContainerName(IConfiguration config, string logicalName, string defaultName)
    {
        return config[$"CosmosDb__Containers__{logicalName}"]
            ?? config[$"CosmosDb:Containers:{logicalName}"]
            ?? defaultName;
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

    // When a managed volunteer signs in and their record is adopted (externalId
    // set), their existing service logs still sit in the old studentId partition
    // (the managed doc's Id). Move them into the externalId partition so
    // GetServiceLogsByStudentAsync (which queries by externalId) finds them.
    // studentId is the partition key, so each log is recreated in the new
    // partition and the old copy deleted. Returns the number of logs migrated.
    public async Task<int> MigrateServiceLogsStudentIdAsync(string oldStudentId, string newExternalId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(oldStudentId)
            || string.IsNullOrWhiteSpace(newExternalId)
            || string.Equals(oldStudentId, newExternalId, StringComparison.Ordinal))
            return 0;

        var logs = await GetServiceLogsByStudentAsync(oldStudentId, cancellationToken);
        var migrated = 0;
        foreach (var log in logs)
        {
            log.StudentId = newExternalId;
            log.UpdatedAt = DateTime.UtcNow;

            // Write the copy in the new partition first, then delete the old one,
            // so a mid-migration failure never loses a log (at worst it leaves a
            // stale copy that a later adoption run reconciles).
            await ServiceLogs.UpsertItemAsync(log, new PartitionKey(newExternalId), cancellationToken: cancellationToken);
            await ServiceLogs.DeleteItemAsync<ServiceLog>(log.Id, new PartitionKey(oldStudentId), cancellationToken: cancellationToken);
            migrated++;
        }

        if (migrated > 0)
            _logger.LogInformation("Migrated {Count} service log(s) from studentId {Old} to {New}.", migrated, oldStudentId, newExternalId);
        return migrated;
    }

    // Public events across all orgs — so an admin can attach volunteers to a
    // shared event created by another organization.
    public async Task<List<Event>> GetPublicEventsAsync(CancellationToken cancellationToken = default)
    {
        var query = Events.GetItemLinqQueryable<Event>()
            .Where(e => e.Visibility == "public")
            .ToFeedIterator();

        var results = new List<Event>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(cancellationToken));
        return results;
    }

    // Cross-partition read: ServiceLogs is partitioned by studentId, so a school-wide
    // report (roster + hours) reads across partitions filtered on schoolId.
    public async Task<List<ServiceLog>> GetServiceLogsBySchoolAsync(string schoolId, CancellationToken cancellationToken = default)
    {
        var query = ServiceLogs.GetItemLinqQueryable<ServiceLog>()
            .Where(l => l.SchoolId == schoolId)
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

    // Marks a single notification read. Scoped to userId (the partition key), so a
    // caller can only ever mark their own. Returns null when it does not exist.
    public async Task<Notification?> MarkNotificationReadAsync(string id, string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await Notifications.ReadItemAsync<Notification>(id, new PartitionKey(userId), cancellationToken: cancellationToken);
            var notification = response.Resource;
            if (notification.IsRead) return notification;

            notification.IsRead = true;
            notification.UpdatedAt = DateTime.UtcNow;
            var updated = await Notifications.ReplaceItemAsync(notification, id, new PartitionKey(userId), cancellationToken: cancellationToken);
            return updated.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
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
