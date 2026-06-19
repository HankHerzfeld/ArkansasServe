using System.Net;
using System.Text.Json;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Models;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

using User = ArkansasServe.Functions.Models.User;

namespace ArkansasServe.Functions.Functions;

// ═══════════════════════════════════════════════════════════════════════════════
// Shared helpers
// ═══════════════════════════════════════════════════════════════════════════════

file static class HttpHelper
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static async Task<HttpResponseData> OkJson(HttpRequestData req, object data)
    {
        var res = req.CreateResponse(HttpStatusCode.OK);
        await res.WriteStringAsync(JsonSerializer.Serialize(data, JsonOpts));
        res.Headers.Add("Content-Type", "application/json");
        return res;
    }

    public static async Task<HttpResponseData> CreatedJson(HttpRequestData req, object data)
    {
        var res = req.CreateResponse(HttpStatusCode.Created);
        await res.WriteStringAsync(JsonSerializer.Serialize(data, JsonOpts));
        res.Headers.Add("Content-Type", "application/json");
        return res;
    }

    public static async Task<HttpResponseData> Error(HttpRequestData req, HttpStatusCode code, string message)
    {
        var res = req.CreateResponse(code);
        await res.WriteStringAsync(JsonSerializer.Serialize(new { error = message }, JsonOpts));
        res.Headers.Add("Content-Type", "application/json");
        return res;
    }

    public static async Task<T?> ReadBody<T>(HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            return string.IsNullOrEmpty(body) ? default : JsonSerializer.Deserialize<T>(body, JsonOpts);
        }
        catch { return default; }
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// USER FUNCTIONS
// GET  /api/users/me          → return or upsert caller's profile
// PUT  /api/users/me          → update caller's profile
// ═══════════════════════════════════════════════════════════════════════════════

public class UserFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<UserFunctions> logger)
{
    [Function("GetOrCreateCurrentUser")]
    public async Task<HttpResponseData> GetMe(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "users/me")] HttpRequestData req)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
        if (ctx == null) return authError!;

        var user = await cosmos.GetUserByExternalIdAsync(ctx.UserId, ctx.TenantId);
        if (user == null)
        {
            // First login — create the profile
            user = new User
            {
                ExternalId  = ctx.UserId,
                TenantId    = ctx.TenantId,
                Role        = ctx.Role,
                DisplayName = ctx.DisplayName,
                Email       = ctx.Email
            };
            user = await cosmos.UpsertUserAsync(user);
        }

        return await HttpHelper.OkJson(req, user);
    }

    [Function("UpdateCurrentUser")]
    public async Task<HttpResponseData> UpdateMe(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "users/me")] HttpRequestData req)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
        if (ctx == null) return authError!;

        var body = await HttpHelper.ReadBody<User>(req);
        if (body == null) return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Invalid request body");

        var user = await cosmos.GetUserByExternalIdAsync(ctx.UserId, ctx.TenantId);
        if (user == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "User not found");

        // Only allow updating safe fields — never let caller change role or tenantId
        user.DisplayName = body.DisplayName;
        user.Phone       = body.Phone;
        user.Grade       = body.Grade;

        var updated = await cosmos.UpsertUserAsync(user);
        return await HttpHelper.OkJson(req, updated);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// EVENT FUNCTIONS
// GET  /api/events                     → list upcoming events (all roles)
// GET  /api/events/{id}                → single event detail
// POST /api/events                     → create event (OrgStaff, Admin)
// PUT  /api/events/{id}                → update event  (OrgStaff, Admin)
// GET  /api/events/{id}/registrations  → roster       (OrgStaff, Admin)
// POST /api/events/upload-token        → SAS token for photo upload
// ═══════════════════════════════════════════════════════════════════════════════

public class EventFunctions(CosmosService cosmos, BlobService blob, AuthConfig authConfig, ILogger<EventFunctions> logger)
{
    [Function("GetEvents")]
    public async Task<HttpResponseData> GetEvents(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "events")] HttpRequestData req)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
        if (ctx == null) return authError!;

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var schoolId = query["schoolId"];

        // Students only see events for their school; admins see all
        var effectiveSchoolId = ctx.IsStudent ? ctx.TenantId : schoolId;
        var events = await cosmos.GetUpcomingEventsAsync(effectiveSchoolId);
        return await HttpHelper.OkJson(req, events);
    }

    [Function("GetEventById")]
    public async Task<HttpResponseData> GetEvent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "events/{id}")] HttpRequestData req,
        string id)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
        if (ctx == null) return authError!;

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var orgId  = query["organizationId"] ?? string.Empty;
        var evt    = await cosmos.GetEventAsync(id, orgId);
        if (evt == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Event not found");
        return await HttpHelper.OkJson(req, evt);
    }

    [Function("CreateEvent")]
    public async Task<HttpResponseData> CreateEvent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events")] HttpRequestData req)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "OrgStaff", "PlatformAdmin");
        if (ctx == null) return authError!;

        var body = await HttpHelper.ReadBody<Event>(req);
        if (body == null) return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Invalid request body");
        if (string.IsNullOrWhiteSpace(body.Title) || body.StartDateTime == default)
            return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Title and StartDateTime are required");

        // Org staff can only create events for their own org
        body.OrganizationId    = ctx.IsPlatformAdmin ? body.OrganizationId : ctx.TenantId;
        body.CreatedByUserId   = ctx.UserId;
        body.Status            = "Open";
        body.CurrentSlots      = 0;

        var created = await cosmos.CreateEventAsync(body);
        return await HttpHelper.CreatedJson(req, created);
    }

    [Function("UpdateEvent")]
    public async Task<HttpResponseData> UpdateEvent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "events/{id}")] HttpRequestData req,
        string id)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "OrgStaff", "PlatformAdmin");
        if (ctx == null) return authError!;

        var body = await HttpHelper.ReadBody<Event>(req);
        if (body == null) return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "Invalid request body");

        var existing = await cosmos.GetEventAsync(id, body.OrganizationId);
        if (existing == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Event not found");

        // Org staff may only edit their own org's events
        if (ctx.IsOrgStaff && existing.OrganizationId != ctx.TenantId)
            return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Cannot edit another organization's event");

        existing.Title             = body.Title;
        existing.Description       = body.Description;
        existing.Location          = body.Location;
        existing.StartDateTime     = body.StartDateTime;
        existing.EndDateTime       = body.EndDateTime;
        existing.MaxSlots          = body.MaxSlots;
        existing.HoursValue        = body.HoursValue;
        existing.EligibleSchoolIds = body.EligibleSchoolIds;
        existing.PhotoUrl          = body.PhotoUrl;
        existing.Category          = body.Category;
        existing.Status            = body.Status;

        var updated = await cosmos.UpdateEventAsync(existing);
        return await HttpHelper.OkJson(req, updated);
    }

    [Function("GetEventRegistrations")]
    public async Task<HttpResponseData> GetRegistrations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "events/{id}/registrations")] HttpRequestData req,
        string id)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "OrgStaff", "SchoolAdmin", "PlatformAdmin");
        if (ctx == null) return authError!;

        var registrations = await cosmos.GetRegistrationsByEventAsync(id);
        return await HttpHelper.OkJson(req, registrations);
    }

    [Function("GetUploadToken")]
    public async Task<HttpResponseData> GetUploadToken(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "events/upload-token")] HttpRequestData req)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "OrgStaff", "PlatformAdmin");
        if (ctx == null) return authError!;

        var body = await HttpHelper.ReadBody<UploadTokenRequest>(req);
        if (body == null) return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "fileName is required");

        var blobName = BlobService.GenerateBlobName("events", body.FileName);
        var sasUrl   = blob.GenerateUploadSasToken("event-photos", blobName);
        var finalUrl = blob.GetPublicBlobUrl("event-photos", blobName);

        return await HttpHelper.OkJson(req, new { sasUrl, finalUrl, blobName });
    }

    [Function("GetOrgEvents")]
    public async Task<HttpResponseData> GetOrgEvents(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "org/events")] HttpRequestData req)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "OrgStaff", "PlatformAdmin");
        if (ctx == null) return authError!;

        var events = await cosmos.GetEventsByOrgAsync(ctx.TenantId);
        return await HttpHelper.OkJson(req, events);
    }
}

file record UploadTokenRequest(string FileName);

// ═══════════════════════════════════════════════════════════════════════════════
// REGISTRATION FUNCTIONS
// POST   /api/registrations        → sign up for event (Student)
// DELETE /api/registrations/{id}   → cancel sign-up   (Student, Admin)
// ═══════════════════════════════════════════════════════════════════════════════

public class RegistrationFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<RegistrationFunctions> logger)
{
    [Function("CreateRegistration")]
    public async Task<HttpResponseData> Register(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "registrations")] HttpRequestData req)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "Student", "PlatformAdmin");
        if (ctx == null) return authError!;

        var body = await HttpHelper.ReadBody<RegistrationRequest>(req);
        if (body == null || string.IsNullOrEmpty(body.EventId))
            return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "eventId is required");

        // Fetch event to validate
        var evt = await cosmos.GetEventAsync(body.EventId, body.OrganizationId ?? string.Empty);
        if (evt == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Event not found");
        if (evt.Status != "Open") return await HttpHelper.Error(req, HttpStatusCode.Conflict, "Event is not open for registration");
        if (evt.MaxSlots > 0 && evt.CurrentSlots >= evt.MaxSlots)
            return await HttpHelper.Error(req, HttpStatusCode.Conflict, "Event is full");

        // Duplicate check
        if (await cosmos.IsAlreadyRegisteredAsync(body.EventId, ctx.UserId))
            return await HttpHelper.Error(req, HttpStatusCode.Conflict, "Already registered for this event");

        var reg = new EventRegistration
        {
            EventId     = body.EventId,
            UserId      = ctx.UserId,
            StudentName = ctx.DisplayName,
            SchoolId    = ctx.TenantId,
            Status      = "Registered"
        };

        var created = await cosmos.CreateRegistrationAsync(reg);

        // Increment slot count with optimistic concurrency (ETag If-Match + retry)
        const int maxRetries = 5;
        bool slotUpdateSucceeded = false;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            var (freshEvt, etag) = await cosmos.GetEventWithETagAsync(body.EventId, body.OrganizationId ?? string.Empty);
            if (freshEvt == null) break;

            // Re-check capacity after re-read to prevent oversubscription
            if (freshEvt.MaxSlots > 0 && freshEvt.CurrentSlots >= freshEvt.MaxSlots)
            {
                // Event became full — rollback the registration
                reg.Status = "Cancelled";
                await cosmos.UpdateRegistrationAsync(reg);
                return await HttpHelper.Error(req, HttpStatusCode.Conflict, "Event is full");
            }

            freshEvt.CurrentSlots++;
            if (freshEvt.MaxSlots > 0 && freshEvt.CurrentSlots >= freshEvt.MaxSlots) freshEvt.Status = "Full";

            try
            {
                await cosmos.UpdateEventAsync(freshEvt, etag);
                slotUpdateSucceeded = true;
                break;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                if (attempt == maxRetries - 1)
                    logger.LogWarning("Failed to update slot count for event {EventId} after {Retries} retries due to concurrent modifications", body.EventId, maxRetries);
            }
        }

        if (!slotUpdateSucceeded)
        {
            // Rollback the registration if slot update failed
            reg.Status = "Cancelled";
            await cosmos.UpdateRegistrationAsync(reg);
            return await HttpHelper.Error(req, HttpStatusCode.ServiceUnavailable, "Unable to complete registration due to concurrent modifications. Please try again.");
        }

        return await HttpHelper.CreatedJson(req, created);
    }

    [Function("CancelRegistration")]
    public async Task<HttpResponseData> CancelRegistration(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "registrations/{id}")] HttpRequestData req,
        string id)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "Student", "SchoolAdmin", "PlatformAdmin");
        if (ctx == null) return authError!;

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var eventId = query["eventId"] ?? string.Empty;

        var reg = await cosmos.GetRegistrationAsync(id, eventId);
        if (reg == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Registration not found");

        // Students can only cancel their own registrations
        if (ctx.IsStudent && reg.UserId != ctx.UserId)
            return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Cannot cancel another user's registration");

        if (reg.Status == "Cancelled")
            return await HttpHelper.Error(req, HttpStatusCode.Conflict, "Registration is already cancelled");

        reg.Status = "Cancelled";
        await cosmos.UpdateRegistrationAsync(reg);

        // Decrement slot count with optimistic concurrency
        const int maxRetries = 5;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            var (freshEvt, etag) = await cosmos.GetEventWithETagAsync(reg.EventId, reg.SchoolId);
            if (freshEvt == null) break;

            if (freshEvt.CurrentSlots > 0) freshEvt.CurrentSlots--;
            if (freshEvt.Status == "Full" && freshEvt.CurrentSlots < freshEvt.MaxSlots) freshEvt.Status = "Open";

            try
            {
                await cosmos.UpdateEventAsync(freshEvt, etag);
                break;
            }
            catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            {
                if (attempt == maxRetries - 1)
                    logger.LogWarning("Failed to decrement slot count for event {EventId} after {Retries} retries", reg.EventId, maxRetries);
            }
        }

        return req.CreateResponse(HttpStatusCode.NoContent);
    }
}

file record RegistrationRequest(string EventId, string? OrganizationId);

// ═══════════════════════════════════════════════════════════════════════════════
// SERVICE LOG FUNCTIONS
// POST  /api/servicelogs       → log hours after event  (OrgStaff, Admin)
// PATCH /api/servicelogs/{id}  → approve or reject      (SchoolAdmin, Admin)
// GET   /api/students/me/servicelogs → student's own history
// ═══════════════════════════════════════════════════════════════════════════════

public class ServiceLogFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<ServiceLogFunctions> logger)
{
    [Function("CreateServiceLog")]
    public async Task<HttpResponseData> CreateLog(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "servicelogs")] HttpRequestData req)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "OrgStaff", "PlatformAdmin");
        if (ctx == null) return authError!;

        var body = await HttpHelper.ReadBody<ServiceLog>(req);
        if (body == null || string.IsNullOrEmpty(body.StudentId) || body.HoursLogged <= 0)
            return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "studentId and hoursLogged > 0 are required");

        body.OrganizationId    = ctx.IsPlatformAdmin ? body.OrganizationId : ctx.TenantId;
        body.SubmittedByUserId = ctx.UserId;
        body.Status            = "Pending";

        var created = await cosmos.CreateServiceLogAsync(body);
        await TryCreatePendingApprovalAsync(created);

        return await HttpHelper.CreatedJson(req, created);
    }

    [Function("ReviewServiceLog")]
    public async Task<HttpResponseData> ReviewLog(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "servicelogs/{id}")] HttpRequestData req,
        string id)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "SchoolAdmin", "PlatformAdmin");
        if (ctx == null) return authError!;

        var body = await HttpHelper.ReadBody<ReviewRequest>(req);
        if (body == null || (body.Status != "Approved" && body.Status != "Rejected"))
            return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "status must be 'Approved' or 'Rejected'");

        var log = await cosmos.GetServiceLogAsync(id, body.StudentId);
        if (log == null) return await HttpHelper.Error(req, HttpStatusCode.NotFound, "Service log not found");

        // School admins can only approve logs for their own school
        if (ctx.IsSchoolAdmin && log.SchoolId != ctx.TenantId)
            return await HttpHelper.Error(req, HttpStatusCode.Forbidden, "Cannot review logs for another school");

        log.Status           = body.Status;
        log.ReviewNote       = body.ReviewNote;
        log.ReviewedByUserId = ctx.UserId;
        log.ReviewedAt       = DateTime.UtcNow;

        var updated = await cosmos.UpdateServiceLogAsync(log);
        await TryFinalizeReviewedLogAsync(updated);

        return await HttpHelper.OkJson(req, updated);
    }

    [Function("GetMyServiceLogs")]
    public async Task<HttpResponseData> GetMyLogs(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "students/me/servicelogs")] HttpRequestData req)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "Student", "PlatformAdmin");
        if (ctx == null) return authError!;

        var logs  = await cosmos.GetServiceLogsByStudentAsync(ctx.UserId);
        var total = logs.Where(l => l.Status == "Approved").Sum(l => l.HoursLogged);
        return await HttpHelper.OkJson(req, new { logs, totalApprovedHours = total });
    }

    private async Task TryCreatePendingApprovalAsync(ServiceLog log)
    {
        try
        {
            await cosmos.CreatePendingApprovalAsync(new PendingApproval
            {
                SchoolId         = log.SchoolId,
                ServiceLogId     = log.Id,
                StudentId        = log.StudentId,
                StudentName      = log.StudentName,
                OrganizationName = log.OrganizationName,
                EventTitle       = log.EventTitle,
                HoursLogged      = log.HoursLogged,
                ServiceDate      = log.ServiceDate
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create pending approval for service log {ServiceLogId}", log.Id);
        }
    }

    private async Task TryFinalizeReviewedLogAsync(ServiceLog log)
    {
        try
        {
            await cosmos.DeletePendingApprovalByLogIdAsync(log.Id, log.SchoolId);

            var message = log.Status == "Approved"
                ? $"Your {log.HoursLogged}h of service at {log.OrganizationName} ({log.EventTitle}) have been approved."
                : $"Your service log for {log.EventTitle} was not approved. Note: {log.ReviewNote ?? "No note provided."}";

            await cosmos.CreateNotificationAsync(new Notification
            {
                UserId    = log.StudentId,
                Type      = log.Status == "Approved" ? "HoursApproved" : "HoursRejected",
                Message   = message,
                RelatedId = log.Id
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to finalize reviewed service log {ServiceLogId}", log.Id);
        }
    }
}

file record ReviewRequest(string StudentId, string Status, string? ReviewNote);

// ═══════════════════════════════════════════════════════════════════════════════
// APPROVAL FUNCTIONS
// GET /api/approvals   → pending approvals for school admin
// ═══════════════════════════════════════════════════════════════════════════════

public class ApprovalFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<ApprovalFunctions> logger)
{
    [Function("GetPendingApprovals")]
    public async Task<HttpResponseData> GetApprovals(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "approvals")] HttpRequestData req)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "SchoolAdmin", "PlatformAdmin");
        if (ctx == null) return authError!;

        var query  = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var schoolId = query["schoolId"];

        if (ctx.IsPlatformAdmin && string.IsNullOrEmpty(schoolId))
        {
            // Platform admin without schoolId filter → return all pending approvals
            var approvals = await cosmos.GetAllPendingApprovalsAsync();
            return await HttpHelper.OkJson(req, approvals);
        }

        var effectiveSchoolId = ctx.IsPlatformAdmin ? schoolId! : ctx.TenantId;
        var result = await cosmos.GetPendingApprovalsBySchoolAsync(effectiveSchoolId);
        return await HttpHelper.OkJson(req, result);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// ADMIN FUNCTIONS  (PlatformAdmin only)
// GET  /api/admin/tenants      → list all schools and orgs
// POST /api/admin/tenants      → create a new school or org
// ═══════════════════════════════════════════════════════════════════════════════

public class AdminFunctions(CosmosService cosmos, AuthConfig authConfig, ILogger<AdminFunctions> logger)
{
    [Function("GetTenants")]
    public async Task<HttpResponseData> GetTenants(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "admin/tenants")] HttpRequestData req)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "PlatformAdmin");
        if (ctx == null) return authError!;

        var tenants = await cosmos.GetAllTenantsAsync();
        return await HttpHelper.OkJson(req, tenants);
    }

    [Function("CreateTenant")]
    public async Task<HttpResponseData> CreateTenant(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "admin/tenants")] HttpRequestData req)
    {
        var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger, "PlatformAdmin");
        if (ctx == null) return authError!;

        var body = await HttpHelper.ReadBody<Tenant>(req);
        if (body == null || string.IsNullOrWhiteSpace(body.Name) || string.IsNullOrWhiteSpace(body.Type))
            return await HttpHelper.Error(req, HttpStatusCode.BadRequest, "name and type are required");

        var created = await cosmos.CreateTenantAsync(body);
        return await HttpHelper.CreatedJson(req, created);
    }
}
