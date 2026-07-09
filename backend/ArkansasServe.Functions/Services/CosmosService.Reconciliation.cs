using ArkansasServe.Functions.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Services;

// Self-healing for the pending-approval queue.
//
// PendingApproval pointers are created/deleted as inline side effects of the service-log
// submit/review flow (ServiceLogFunctions). Those side effects can fail (they are caught and
// logged), which would otherwise leave the admin review queue out of sync with reality:
//   • a lost CREATE  → a submitted (Pending) log never appears for review;
//   • a lost DELETE  → a reviewed log lingers in the queue.
// Because a PendingApproval is fully derivable from its ServiceLog, we reconcile the queue on
// read: recreate any missing pointer and drop any stale one. This makes the side effects
// recoverable without a background worker or extra infrastructure.
public partial class CosmosService
{
    /// <summary>Deterministic pointer id so an inline create and a reconcile create for the
    /// same log collapse to one document (a duplicate create 409s and is ignored).</summary>
    public static string PendingApprovalIdFor(string serviceLogId) => $"pa-{serviceLogId}";

    /// <summary>All Pending service logs for a school (cross-partition — the container is
    /// partitioned by studentId). Bounded to an admin action, so the scan cost is acceptable.</summary>
    public async Task<List<ServiceLog>> GetPendingServiceLogsBySchoolAsync(string schoolId, CancellationToken cancellationToken = default)
    {
        var query = ServiceLogs.GetItemLinqQueryable<ServiceLog>()
            .Where(l => l.SchoolId == schoolId && l.Status == "Pending")
            .ToFeedIterator();

        var results = new List<ServiceLog>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(cancellationToken));
        return results;
    }

    /// <summary>
    /// Returns the school's pending-approval queue, first reconciling it against the source of
    /// truth (Pending service logs): missing pointers are recreated and stale pointers removed.
    /// Reconciliation failures are swallowed (logged) — a self-heal must never break the read.
    /// </summary>
    public async Task<List<PendingApproval>> GetPendingApprovalsBySchoolReconciledAsync(string schoolId, CancellationToken cancellationToken = default)
    {
        var pointers = await GetPendingApprovalsBySchoolAsync(schoolId, cancellationToken);
        List<ServiceLog> pendingLogs;
        try
        {
            pendingLogs = await GetPendingServiceLogsBySchoolAsync(schoolId, cancellationToken);
        }
        catch (Exception ex)
        {
            // If we can't read the source of truth, return the queue as-is rather than failing.
            _logger.LogWarning(ex, "Pending-approval reconciliation skipped for school {SchoolId}", schoolId);
            return pointers;
        }

        var (toCreate, toDelete) = PlanReconciliation(pointers, pendingLogs);

        // Recreate pointers for Pending logs that have none (a lost inline create).
        foreach (var log in toCreate)
        {
            try
            {
                var created = await CreatePendingApprovalAsync(PendingApprovalFromLog(log), cancellationToken);
                pointers.Add(created);
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // Raced with the inline create — the pointer now exists; nothing to do.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconcile could not recreate PendingApproval for log {LogId}", log.Id);
            }
        }

        // Drop pointers whose log is no longer Pending (a lost inline delete on review).
        foreach (var stale in toDelete)
        {
            try
            {
                await DeletePendingApprovalByLogIdAsync(stale.ServiceLogId, schoolId, cancellationToken);
                pointers.Remove(stale);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconcile could not remove stale PendingApproval {PointerId}", stale.Id);
            }
        }

        return pointers.OrderByDescending(p => p.ServiceDate).ToList();
    }

    /// <summary>
    /// Pure diff between the existing queue pointers and the Pending service logs:
    /// logs with no pointer must be created; pointers whose log is no longer Pending must be
    /// deleted. Extracted for unit testing — no Cosmos access.
    /// </summary>
    public static (List<ServiceLog> ToCreate, List<PendingApproval> ToDelete) PlanReconciliation(
        IReadOnlyList<PendingApproval> pointers, IReadOnlyList<ServiceLog> pendingLogs)
    {
        var pointerLogIds = pointers.Select(p => p.ServiceLogId).ToHashSet(StringComparer.Ordinal);
        var pendingLogIds = pendingLogs.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);

        var toCreate = pendingLogs.Where(l => !pointerLogIds.Contains(l.Id)).ToList();
        var toDelete = pointers.Where(p => !pendingLogIds.Contains(p.ServiceLogId)).ToList();
        return (toCreate, toDelete);
    }

    /// <summary>Builds a PendingApproval pointer from a service log, with a deterministic id.</summary>
    public static PendingApproval PendingApprovalFromLog(ServiceLog log) => new()
    {
        Id = PendingApprovalIdFor(log.Id),
        SchoolId = log.SchoolId,
        ServiceLogId = log.Id,
        StudentId = log.StudentId,
        StudentName = log.StudentName,
        OrganizationName = log.OrganizationName,
        EventTitle = log.EventTitle,
        HoursLogged = log.HoursLogged,
        ServiceDate = log.ServiceDate,
    };
}
