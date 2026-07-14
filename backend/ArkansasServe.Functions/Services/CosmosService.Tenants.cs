using ArkansasServe.Functions.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using User = ArkansasServe.Functions.Models.User;

namespace ArkansasServe.Functions.Services;

public partial class CosmosService
{
	/// <summary>
	/// Every membership in a tenant's partition regardless of status. Distinct from
	/// GetUsersByTenantAsync, which is the "roster" query and filters to active members —
	/// correct for listing people, wrong for a teardown that must leave nothing behind.
	/// </summary>
	private async Task<List<User>> GetAllUsersByTenantIncludingInactiveAsync(string tenantId, CancellationToken cancellationToken = default)
	{
		var query = Users.GetItemLinqQueryable<User>(requestOptions: new QueryRequestOptions
			{ PartitionKey = new PartitionKey(tenantId) })
			.ToFeedIterator();

		var results = new List<User>();
		while (query.HasMoreResults)
			results.AddRange(await query.ReadNextAsync(cancellationToken));
		return results;
	}

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

		// Every membership in the partition, NOT GetUsersByTenantAsync — that filters to
		// status == "active", so a suspended/inactive member would survive the cascade and
		// be left pointing at a tenant that no longer exists. Such an orphan then renders
		// as a raw GUID wherever memberships are listed, and can never be cleaned up
		// through the UI because its org is gone.
		var members = await GetAllUsersByTenantIncludingInactiveAsync(tenantId, cancellationToken);
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
