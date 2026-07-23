using ArkansasServe.Functions.Models;
using Microsoft.Azure.Cosmos;

namespace ArkansasServe.Functions.Services;

// Reset bookkeeping for demo ACTIVITY — events, registrations, service logs seeded under the demo
// tenants (see DemoData / AdminFunctions.ResetDemoUsers). Each is a cross-partition scan on the
// isDemo flag, then a delete by (id, partition key). Only ever runs on a SuperAdmin reset, so the
// scan is not a hot path. Recreation reuses the ordinary Create* methods.
public partial class CosmosService
{
	private static async Task<List<T>> ReadAllAsync<T>(FeedIterator<T> query, CancellationToken ct)
	{
		var results = new List<T>();
		while (query.HasMoreResults)
			results.AddRange(await query.ReadNextAsync(ct));
		return results;
	}

	// Events — partitioned by /organizationId.
	public async Task DeleteAllDemoEventsAsync(CancellationToken cancellationToken = default)
	{
		var events = await ReadAllAsync(
			Events.GetItemQueryIterator<Event>(new QueryDefinition("SELECT * FROM c WHERE c.isDemo = true")),
			cancellationToken);
		foreach (var e in events)
			await Events.DeleteItemAsync<Event>(e.Id, new PartitionKey(e.OrganizationId), cancellationToken: cancellationToken);
	}

	// Registrations — partitioned by /eventId.
	public async Task DeleteAllDemoRegistrationsAsync(CancellationToken cancellationToken = default)
	{
		var regs = await ReadAllAsync(
			Registrations.GetItemQueryIterator<EventRegistration>(new QueryDefinition("SELECT * FROM c WHERE c.isDemo = true")),
			cancellationToken);
		foreach (var r in regs)
			await Registrations.DeleteItemAsync<EventRegistration>(r.Id, new PartitionKey(r.EventId), cancellationToken: cancellationToken);
	}

	// Service logs — partitioned by /studentId.
	public async Task DeleteAllDemoServiceLogsAsync(CancellationToken cancellationToken = default)
	{
		var logs = await ReadAllAsync(
			ServiceLogs.GetItemQueryIterator<ServiceLog>(new QueryDefinition("SELECT * FROM c WHERE c.isDemo = true")),
			cancellationToken);
		foreach (var l in logs)
			await ServiceLogs.DeleteItemAsync<ServiceLog>(l.Id, new PartitionKey(l.StudentId), cancellationToken: cancellationToken);
	}
}
