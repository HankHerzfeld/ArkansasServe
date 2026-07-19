using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

/// <summary>
/// The assigned admin's own view of #13 oversight: the volunteers assigned to them, their
/// per-volunteer notification prefs, and a direct message to those volunteers. WHO is assigned to
/// whom is set by an OrganizationAdmin via UpdateUserAccess; these endpoints are the assigned
/// admin acting for themselves, so they authorize as EventAdmin+ in the org and key off the
/// caller's own membership id.
/// </summary>
public class AssignmentFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<AssignmentFunctions> logger)
{
	private static string NameOf(User u) => string.IsNullOrWhiteSpace(u.DisplayName) ? u.Email : u.DisplayName;

	/// <summary>
	/// GET /api/manage/me/assigned-volunteers?organizationId={org}
	/// The volunteers assigned to the caller in that org, each with the caller's own prefs.
	/// </summary>
	[Function("GetMyAssignedVolunteers")]
	public async Task<HttpResponseData> GetMyAssignedVolunteers(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/me/assigned-volunteers")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var orgId = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["organizationId"] ?? string.Empty;
		if (string.IsNullOrWhiteSpace(orgId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "organizationId is required");

		var me = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, orgId);
		if (me == null || !AdminLevels.AtLeast(me.AdminLevel, AdminLevels.EventAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "EventAdmin or higher is required in this organization");

		var members = await cosmos.GetUsersByTenantAsync(orgId);
		var mine = members
			.Select(m => new { m, a = m.AssignedAdmins.FirstOrDefault(x => string.Equals(x.AdminId, me.Id, StringComparison.OrdinalIgnoreCase)) })
			.Where(x => x.a != null)
			.OrderBy(x => NameOf(x.m), StringComparer.OrdinalIgnoreCase)
			.Select(x => new
			{
				id = x.m.Id,
				name = NameOf(x.m),
				email = x.m.Email,
				adminLevel = x.m.AdminLevel,
				totalApprovedHours = x.m.TotalApprovedHours,
				notifyOnHours = x.a!.NotifyOnHours,
				notifyOnApproval = x.a!.NotifyOnApproval,
			})
			.ToList();

		return await HttpHelper.OkJson(req, mine);
	}

	/// <summary>
	/// GET /api/manage/me/overseers
	/// The mirror of GetMyAssignedVolunteers: who oversees ME, in each org I belong to.
	///
	/// #13 stored `AssignedAdmins` on the volunteer's own per-org doc but only ever read it from
	/// the ADMIN side, so a volunteer had no way to see who was responsible for their hours. The
	/// dashboard's per-org cards need exactly that.
	///
	/// Deliberately its own endpoint rather than extra fields on /manage/me/memberships: scope.js
	/// calls memberships on every scoped page, and this costs one read per assigned admin — a price
	/// worth paying on the dashboard alone, not on every page load.
	///
	/// No org parameter and no admin check: the caller only ever reads their OWN assignment rows.
	/// </summary>
	[Function("GetMyOverseers")]
	public async Task<HttpResponseData> GetMyOverseers(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "manage/me/overseers")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var memberships = await cosmos.GetMembershipsByExternalIdAsync(ctx.UserId);

		// One entry per org, so the dashboard can key straight off organizationId.
		var result = new List<object>();
		foreach (var m in memberships)
		{
			var orgId = string.IsNullOrWhiteSpace(m.OrganizationId) ? m.TenantId : m.OrganizationId!;
			if (string.IsNullOrWhiteSpace(orgId) || m.AssignedAdmins.Count == 0) continue;

			var admins = new List<object>();
			foreach (var a in m.AssignedAdmins)
			{
				if (string.IsNullOrWhiteSpace(a.AdminId)) continue;
				var admin = await cosmos.GetUserByIdAsync(a.AdminId, orgId);
				// An assignment pointing at a removed admin is stale, not an error — skip it
				// rather than surfacing a blank row the volunteer cannot act on.
				if (admin == null)
				{
					logger.LogWarning(
						"Stale assignment: user {UserId} in org {OrgId} is assigned to missing admin {AdminId}",
						m.Id, orgId, a.AdminId);
					continue;
				}
				admins.Add(new { id = admin.Id, name = NameOf(admin), email = admin.Email });
			}

			if (admins.Count > 0) result.Add(new { organizationId = orgId, admins });
		}

		return await HttpHelper.OkJson(req, result);
	}

	/// <summary>
	/// PATCH /api/manage/me/assignments/{volunteerId}?organizationId={org}
	/// body: { notifyOnHours?, notifyOnApproval? } — the caller edits THEIR OWN prefs on one of
	/// their assigned volunteers (not another admin's, and not the assignment's existence).
	/// </summary>
	[Function("SetMyAssignmentPrefs")]
	public async Task<HttpResponseData> SetMyAssignmentPrefs(
		[HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "manage/me/assignments/{volunteerId}")] HttpRequestData req,
		string volunteerId)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var orgId = System.Web.HttpUtility.ParseQueryString(req.Url.Query)["organizationId"] ?? string.Empty;
		if (string.IsNullOrWhiteSpace(orgId))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "organizationId is required");

		var me = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, orgId);
		if (me == null || !AdminLevels.AtLeast(me.AdminLevel, AdminLevels.EventAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "EventAdmin or higher is required in this organization");

		var body = await HttpHelper.ReadBody<PrefsRequest>(req);
		if (body == null) return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "A prefs body is required");

		var volunteer = await cosmos.GetUserByIdAsync(volunteerId, orgId);
		if (volunteer == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Volunteer not found");

		var assignment = volunteer.AssignedAdmins.FirstOrDefault(a => string.Equals(a.AdminId, me.Id, StringComparison.OrdinalIgnoreCase));
		if (assignment == null) return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "You are not assigned to this volunteer");

		if (body.NotifyOnHours.HasValue) assignment.NotifyOnHours = body.NotifyOnHours.Value;
		if (body.NotifyOnApproval.HasValue) assignment.NotifyOnApproval = body.NotifyOnApproval.Value;
		await cosmos.UpsertUserAsync(volunteer);

		return await HttpHelper.OkJson(req, new { volunteerId, assignment.NotifyOnHours, assignment.NotifyOnApproval });
	}

	/// <summary>
	/// POST /api/manage/me/assigned-volunteers/notify  body: { organizationId, message }
	/// Sends a direct in-app notification to every volunteer assigned to the caller in that org.
	/// </summary>
	[Function("NotifyMyAssignedVolunteers")]
	public async Task<HttpResponseData> NotifyMyAssignedVolunteers(
		[HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "manage/me/assigned-volunteers/notify")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var body = await HttpHelper.ReadBody<NotifyRequest>(req);
		if (body == null || string.IsNullOrWhiteSpace(body.OrganizationId) || string.IsNullOrWhiteSpace(body.Message))
			return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "organizationId and message are required");

		var me = await cosmos.ResolveActorInOrgAsync(ctx.UserId, ctx.AdminLevel, body.OrganizationId);
		if (me == null || !AdminLevels.AtLeast(me.AdminLevel, AdminLevels.EventAdmin))
			return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "EventAdmin or higher is required in this organization");

		var text = body.Message.Trim();
		if (text.Length > 500) text = text[..500];

		var members = await cosmos.GetUsersByTenantAsync(body.OrganizationId);
		var mine = members.Where(m => m.AssignedAdmins.Any(a => string.Equals(a.AdminId, me.Id, StringComparison.OrdinalIgnoreCase)));

		var sent = 0;
		foreach (var v in mine)
		{
			// A managed volunteer who has never signed in has no login id to receive it.
			if (string.IsNullOrWhiteSpace(v.ExternalId)) continue;
			await cosmos.CreateNotificationAsync(new Notification
			{
				UserId = v.ExternalId,
				Type = "DirectMessage",
				Message = $"{NameOf(me)}: {text}",
			});
			sent++;
		}

		logger.LogInformation("[Assignment] {Actor} sent a direct message to {Count} assigned volunteer(s) in org {OrgId}", ctx.UserId, sent, body.OrganizationId);
		return await HttpHelper.OkJson(req, new { sent });
	}

	private sealed record PrefsRequest(bool? NotifyOnHours, bool? NotifyOnApproval);
	private sealed record NotifyRequest(string? OrganizationId, string? Message);
}
