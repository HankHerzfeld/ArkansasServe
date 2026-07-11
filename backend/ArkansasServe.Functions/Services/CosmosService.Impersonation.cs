using ArkansasServe.Functions.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;

namespace ArkansasServe.Functions.Services;

// Impersonation sessions + audit trail (Phase F #26). Both containers are partitioned
// by adminUserId, so every read is a point read scoped to the acting admin.
public partial class CosmosService
{
	private readonly string _impersonationSessionsContainerName;
	private readonly string _auditEventsContainerName;

	private Container ImpersonationSessions => _client.GetContainer(_databaseName, _impersonationSessionsContainerName);
	private Container AuditEvents => _client.GetContainer(_databaseName, _auditEventsContainerName);

	public async Task<ImpersonationSession> CreateImpersonationSessionAsync(ImpersonationSession session, CancellationToken cancellationToken = default)
	{
		var response = await ImpersonationSessions.CreateItemAsync(session, new PartitionKey(session.AdminUserId), cancellationToken: cancellationToken);
		return response.Resource;
	}

	public async Task<ImpersonationSession?> GetImpersonationSessionAsync(string id, string adminUserId, CancellationToken cancellationToken = default)
	{
		try
		{
			var response = await ImpersonationSessions.ReadItemAsync<ImpersonationSession>(id, new PartitionKey(adminUserId), cancellationToken: cancellationToken);
			return response.Resource;
		}
		catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
		{
			return null;
		}
	}

	/// <summary>Marks a session ended (idempotent). Returns null if it doesn't exist for this admin.</summary>
	public async Task<ImpersonationSession?> EndImpersonationSessionAsync(string id, string adminUserId, CancellationToken cancellationToken = default)
	{
		var session = await GetImpersonationSessionAsync(id, adminUserId, cancellationToken);
		if (session == null) return null;
		if (session.EndedAt == null)
		{
			session.EndedAt = DateTime.UtcNow;
			var response = await ImpersonationSessions.ReplaceItemAsync(session, id, new PartitionKey(adminUserId), cancellationToken: cancellationToken);
			return response.Resource;
		}
		return session;
	}

	public async Task DeleteImpersonationSessionAsync(string id, string adminUserId, CancellationToken cancellationToken = default)
	{
		try
		{
			await ImpersonationSessions.DeleteItemAsync<ImpersonationSession>(id, new PartitionKey(adminUserId), cancellationToken: cancellationToken);
		}
		catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
		{
			// already gone
		}
	}

	public async Task<List<ImpersonationSession>> GetImpersonationSessionsByAdminAsync(string adminUserId, CancellationToken cancellationToken = default)
	{
		var query = ImpersonationSessions.GetItemLinqQueryable<ImpersonationSession>(
				requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(adminUserId) })
			.ToFeedIterator();

		var results = new List<ImpersonationSession>();
		while (query.HasMoreResults)
			results.AddRange(await query.ReadNextAsync(cancellationToken));
		return results.OrderByDescending(s => s.StartedAt).ToList();
	}

	public async Task AppendAuditEventAsync(AuditEvent evt, CancellationToken cancellationToken = default)
	{
		await AuditEvents.CreateItemAsync(evt, new PartitionKey(evt.AdminUserId), cancellationToken: cancellationToken);
	}
}
