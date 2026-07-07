using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

public class ReportFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<ReportFunctions> logger)
{
	// Roster + service-hour report for a single school. Returns an aggregated
	// per-student summary (every active student, even with zero hours) plus the
	// approved line items in range, for compliance / court-documentation export.
	[Function("GetServiceHoursReport")]
	public async Task<HttpResponseData> GetServiceHours(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/reports/service-hours")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

		var schoolId = string.IsNullOrWhiteSpace(query["schoolId"]) ? ctx.TenantId : query["schoolId"]!;
		if (string.IsNullOrWhiteSpace(schoolId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "schoolId is required");

		// Per-org: the caller must be an OrganizationAdmin+ in the target school.
		var actor = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.Role, schoolId);
		if (actor == null || !AdminLevels.AtLeast(actor.AdminLevel, AdminLevels.OrganizationAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "You do not have reporting access in this organization");

		var from = ParseDate(query["from"], DateTime.MinValue);
		var toParsed = ParseDate(query["to"], DateTime.MaxValue);
		// 'to' is inclusive of the whole calendar day.
		var to = toParsed == DateTime.MaxValue ? DateTime.MaxValue : toParsed.Date.AddDays(1).AddTicks(-1);

		var roster = (await cosmos.GetUsersByTenantAsync(schoolId))
			.Where(u => string.Equals(u.Role, "Student", StringComparison.OrdinalIgnoreCase)
					 || string.Equals(u.AdminLevel, "Student", StringComparison.OrdinalIgnoreCase))
			.ToList();

		var inRange = (await cosmos.GetServiceLogsBySchoolAsync(schoolId))
			.Where(l => l.ServiceDate >= from && l.ServiceDate <= to)
			.ToList();

		var logsByStudent = inRange
			.GroupBy(l => l.StudentId)
			.ToDictionary(g => g.Key, g => g.ToList());

		// Every rostered student appears — a student with no hours still shows 0,
		// so the roster is complete for compliance reporting.
		var students = roster.Select(u =>
		{
			var studentLogs = logsByStudent.TryGetValue(u.ExternalId, out var found) ? found : [];
			return new
			{
				studentId = u.ExternalId,
				name = u.DisplayName,
				grade = u.Grade,
				email = u.Email,
				approvedHours = studentLogs.Where(l => l.Status == "Approved").Sum(l => l.HoursLogged),
				pendingHours = studentLogs.Where(l => l.Status == "Pending").Sum(l => l.HoursLogged),
				eventsAttended = studentLogs.Count(l => l.Status == "Approved")
			};
		})
		.OrderBy(s => s.name)
		.ToList();

		// Line items for the detailed export: approved logs only.
		var detail = inRange
			.Where(l => l.Status == "Approved")
			.OrderBy(l => l.StudentName).ThenBy(l => l.ServiceDate)
			.Select(l => new
			{
				studentName = l.StudentName,
				eventTitle = l.EventTitle,
				organizationName = l.OrganizationName,
				serviceDate = l.ServiceDate,
				hoursLogged = l.HoursLogged
			})
			.ToList();

		return await HttpHelper.OkJson(req, new
		{
			schoolId,
			from = from == DateTime.MinValue ? (DateTime?)null : from,
			to = to == DateTime.MaxValue ? (DateTime?)null : to,
			students,
			logs = detail
		});
	}

	private static DateTime ParseDate(string? value, DateTime fallback)
		=> DateTime.TryParse(value, out var parsed) ? parsed : fallback;
}
