using ArkansasServe.Functions.Models;
using Microsoft.Azure.Cosmos;
using User = ArkansasServe.Functions.Models.User;

namespace ArkansasServe.Functions.Services;

public partial class CosmosService
{
	/// <summary>
	/// Deletes a tenant and cascades to the data anchored to it: its events (partitioned
	/// by organizationId == tenantId) and its user memberships (partitioned by tenantId).
	/// Registrations and service logs live in other partitions (by eventId / studentId)
	/// and are left as harmless orphans — acceptable for tearing down test orgs. Returns
	/// the counts removed. Callers must gate this on SuperAdmin + an explicit confirmation.
	/// </summary>
	public async Task<(int Events, int Members)> DeleteTenantCascadeAsync(string tenantId, CancellationToken cancellationToken = default)
	{
		var events = await GetEventsByOrgAsync(tenantId, cancellationToken);
		foreach (var e in events)
		{
			try { await Events.DeleteItemAsync<Event>(e.Id, new PartitionKey(tenantId), cancellationToken: cancellationToken); }
			catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }
		}

		var members = await GetUsersByTenantAsync(tenantId, cancellationToken);
		foreach (var m in members)
		{
			try { await Users.DeleteItemAsync<User>(m.Id, new PartitionKey(tenantId), cancellationToken: cancellationToken); }
			catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }
		}

		try { await Tenants.DeleteItemAsync<Tenant>(tenantId, new PartitionKey(tenantId), cancellationToken: cancellationToken); }
		catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }

		return (events.Count, members.Count);
	}
}
