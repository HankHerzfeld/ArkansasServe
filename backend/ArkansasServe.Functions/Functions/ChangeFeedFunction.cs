using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

public class ChangeFeedFunction(CosmosService cosmos, ILogger<ChangeFeedFunction> logger)
{
	[Function("ProcessServiceLogChangeFeed")]
	public async Task Run(
		[CosmosDBTrigger(
			databaseName: "%CosmosDb__DatabaseName%",
			containerName: "serviceLogs",
			Connection = "CosmosDb__ConnectionString",
			LeaseContainerName = "changeFeedLeases",
			CreateLeaseContainerIfNotExists = true)]
		IReadOnlyList<ServiceLog> changedDocuments)
	{
		if (changedDocuments is null || changedDocuments.Count == 0)
			return;

		foreach (var log in changedDocuments)
		{
			try
			{
				if (log.Status == "Pending")
				{
					// Prevent duplicate pointers when replay/retry occurs.
					await cosmos.DeletePendingApprovalByLogIdAsync(log.Id, log.SchoolId);

					await cosmos.CreatePendingApprovalAsync(new PendingApproval
					{
						SchoolId = log.SchoolId,
						ServiceLogId = log.Id,
						StudentId = log.StudentId,
						StudentName = log.StudentName,
						OrganizationName = log.OrganizationName,
						EventTitle = log.EventTitle,
						HoursLogged = log.HoursLogged,
						ServiceDate = log.ServiceDate
					});
				}
				else if (log.Status == "Approved" || log.Status == "Rejected")
				{
					await cosmos.DeletePendingApprovalByLogIdAsync(log.Id, log.SchoolId);

					var message = log.Status == "Approved"
						? $"Your {log.HoursLogged}h of service at {log.OrganizationName} ({log.EventTitle}) have been approved."
						: $"Your service log for {log.EventTitle} was not approved. Note: {log.ReviewNote ?? "No note provided."}";

					await cosmos.CreateNotificationAsync(new Notification
					{
						UserId = log.StudentId,
						Type = log.Status == "Approved" ? "HoursApproved" : "HoursRejected",
						Message = message,
						RelatedId = log.Id
					});
				}
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Failed processing service log change feed document {ServiceLogId}", log.Id);
			}
		}
	}
}
