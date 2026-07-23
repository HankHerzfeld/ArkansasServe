using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ArkansasServe.Functions.Functions;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

// Microsoft.IdentityModel.Protocols also defines an HttpRequestData type; pin the
// name to the Azure Functions worker type so it stays consistent with the callers.
using HttpRequestData = Microsoft.Azure.Functions.Worker.Http.HttpRequestData;

namespace ArkansasServe.Functions.Middleware;

/// <summary>
/// Validates the Bearer token on every HTTP request against the Entra External ID
/// (CIAM) tenant's OpenID Connect metadata.
///
/// Built on ConfigurationManager&lt;OpenIdConnectConfiguration&gt; +
/// JsonWebTokenHandler: signing keys and the issuer come from the tenant's
/// discovery document and refresh automatically (including key rollover);
/// nothing is hand-rolled.
///
/// Public contract is unchanged from the previous implementation — all
/// functions keep calling:
///   var (ctx, err) = await AuthMiddleware.ValidateRequest(req, config, logger, roles);
/// </summary>
public static class AuthMiddleware
{
    // One metadata manager per tenant; it caches the discovery document and
    // signing keys, refreshing on a schedule and on key-not-found.
    private static readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _configManagers = new();

    private static ConfigurationManager<OpenIdConnectConfiguration> GetConfigurationManager(string tenantId)
        => _configManagers.GetOrAdd(tenantId, tid => new ConfigurationManager<OpenIdConnectConfiguration>(
            $"https://{tid}.ciamlogin.com/{tid}/v2.0/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true }));

    public static async Task<(UserContext? Context, HttpResponseData? ErrorResponse)> ValidateRequest(
        HttpRequestData req,
        AuthConfig config,
        ILogger logger,
        string? minAdminLevel = null)
    {
        // ── Extract Bearer token ────────────────────────────────────────────
        if (!req.Headers.TryGetValues("Authorization", out var authHeaders))
            return (null, await WriteUnauthorized(req, "Missing Authorization header"));

        var header = authHeaders.FirstOrDefault() ?? string.Empty;
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : string.Empty;
        if (string.IsNullOrEmpty(token))
            return (null, await WriteUnauthorized(req, "Empty token"));

        try
        {
            // ── Validate ────────────────────────────────────────────────────
            // ConfigurationManager supplies the issuer and signing keys from
            // the tenant's metadata; explicit ValidIssuers are kept as a
            // defense-in-depth allowlist of the known-good issuer formats.
            var validationParams = new TokenValidationParameters
            {
                ConfigurationManager = GetConfigurationManager(config.TenantId),
                ValidateIssuer = true,
                ValidIssuers =
                [
                    $"https://{config.TenantId}.ciamlogin.com/{config.TenantId}/v2.0",
                    $"https://{config.TenantId}.ciamlogin.com/{config.TenantId}/v2.0/",
                    $"https://{config.TenantId}.ciamlogin.com/{config.TenantId}/"
                ],
                ValidateAudience = true,
                ValidAudiences = BuildValidAudiences(config),
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromMinutes(2)
            };

            var handler = new JsonWebTokenHandler();
            var result = await handler.ValidateTokenAsync(token, validationParams);

            if (!result.IsValid)
            {
                logger.LogWarning(result.Exception, "Token validation failed: {Message}", result.Exception?.Message);
                return (null, await WriteUnauthorized(req, "Invalid or expired token"));
            }

            var identity = result.ClaimsIdentity;
            string? Claim(string type) => identity.Claims.FirstOrDefault(c => c.Type == type)?.Value;
            var roleFromRolesClaim = identity.Claims.Where(c => c.Type == "roles").Select(c => c.Value).FirstOrDefault();

            var userContext = new UserContext
            {
                UserId      = Claim("oid") ?? Claim("sub") ?? string.Empty,
                // NOTE: deliberately does NOT fall back to the "tid" claim. `tid` is the Entra
                // DIRECTORY id (the same GUID hardcoded as TENANT_ID in frontend/js/auth.js) —
                // it identifies the identity provider, not an organization on this platform.
                // Using it as a TenantId bootstrapped every user whose token carried no org
                // claim into a pseudo-org that has no Tenant doc and never would, which then
                // rendered as a raw GUID wherever memberships are listed. Leaving this empty is
                // what lets ResolveTenantId report "no assigned or joined organization".
                TenantId    = Claim("extension_OrganizationId") ?? Claim("extension_SchoolId") ?? Claim("extension_TenantId") ?? string.Empty,
                // The token still carries the legacy 4-role claim; translate it once,
                // here, into the 5-level adminLevel used everywhere downstream.
                AdminLevel  = AdminLevels.FromLegacyRole(Claim("extension_Role") ?? roleFromRolesClaim),
                Email       = Claim("email") ?? Claim("preferred_username") ?? string.Empty,
                DisplayName = Claim("name") ?? string.Empty,
                GivenName   = Claim("given_name") ?? string.Empty,
                FamilyName  = Claim("family_name") ?? string.Empty
            };

            // ── Bootstrap elevation (config-gated; empty setting = disabled) ─
            // Replaces the previous hardcoded "@arkansasserve.com" backdoor.
            // Set Entra__PlatformAdminEmailDomain only while seeding the first
            // PlatformAdmin, then clear it; day-to-day roles come from the
            // users container (adminLevel) and token role claims.
            if (!string.IsNullOrWhiteSpace(config.PlatformAdminEmailDomain)
                && userContext.Email.EndsWith($"@{config.PlatformAdminEmailDomain.TrimStart('@')}", StringComparison.OrdinalIgnoreCase))
            {
                userContext.AdminLevel = AdminLevels.SuperAdmin;
                if (string.IsNullOrWhiteSpace(userContext.TenantId))
                    userContext.TenantId = TenantIds.Root;
            }

            // Reject only when the calling-client claim is present and clearly
            // mismatched (some CIAM access tokens omit azp/appid).
            var callingClientId = Claim("azp") ?? Claim("appid");
            if (!string.IsNullOrWhiteSpace(config.ClientId)
                && !string.IsNullOrWhiteSpace(callingClientId)
                && !string.Equals(callingClientId, config.ClientId, StringComparison.OrdinalIgnoreCase))
                return (null, await WriteUnauthorized(req, "Invalid token"));

            if (string.IsNullOrWhiteSpace(userContext.UserId))
                return (null, await WriteUnauthorized(req, "Invalid token"));

            // ── Impersonation (#26) ──────────────────────────────────────────
            // Default: acting as yourself.
            userContext.RealUserId = userContext.UserId;
            var impSid = req.Headers.TryGetValues("X-Impersonation-Session", out var impVals)
                ? impVals.FirstOrDefault()
                : null;
            if (!string.IsNullOrWhiteSpace(impSid))
            {
                // The impersonation control routes must always be reachable so the admin
                // can Exit / manage sessions even when the session itself is invalid. Match
                // them exactly (…/manage/impersonation or …/manage/impersonation/{sid}) — an
                // unanchored Contains would also exempt a future route like
                // "…/manage/impersonation-notes" from the read-only write block.
                var isControlRoute = IsImpersonationControlRoute(req.Url.AbsolutePath);
                var effective = await TryResolveImpersonationAsync(req, userContext, impSid!, logger);
                if (effective != null)
                {
                    // Read-only mode blocks writes while impersonating — except the control routes.
                    if (string.Equals(effective.ImpersonationMode, "read-only", StringComparison.OrdinalIgnoreCase)
                        && !IsSafeMethod(req.Method)
                        && !isControlRoute)
                        return (null, await WriteForbidden(req, "Read-only impersonation: writes are disabled while viewing as another user."));

                    userContext = effective;
                }
                else if (!isControlRoute)
                {
                    // Header present but the session is expired/revoked/not-owned. Do NOT
                    // silently fall back to the real SuperAdmin — otherwise a request the
                    // operator believes is a read-only demo view would run live as super.
                    // Signal the client to exit impersonation (409 + a distinct code).
                    return (null, await WriteImpersonationExpired(req));
                }
                // Control route with an invalid session: fall through as the real admin so
                // Exit/cleanup can proceed.
            }

            // Minimum-level check — 403 so callers can distinguish "not
            // authenticated" (401) from "authenticated but under-ranked" (403).
            if (minAdminLevel != null &&
                !AdminLevels.AtLeast(userContext.AdminLevel, minAdminLevel))
                return (null, await WriteForbidden(req, "Forbidden"));

            return (userContext, null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during token validation");
            return (null, await WriteUnauthorized(req, "Authentication error"));
        }
    }

    // ── Impersonation (#26) ───────────────────────────────────────────────────

    private static bool IsSafeMethod(string method) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase)
        || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
        || method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase);

    // Matches the impersonation control routes exactly: the collection route
    // (…/manage/impersonation) and the per-session route (…/manage/impersonation/{sid}),
    // but NOT a lookalike such as …/manage/impersonation-notes.
    private static bool IsImpersonationControlRoute(string absolutePath)
    {
        var path = absolutePath.TrimEnd('/');
        return path.EndsWith("/manage/impersonation", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/manage/impersonation/", StringComparison.OrdinalIgnoreCase);
    }

    // Builds the effective (target) context from an active session, or null if the
    // session is missing/expired/revoked/not owned by this caller, or the caller no
    // longer qualifies as a global super. CosmosService is resolved from the request's
    // DI scope so no call site has to change.
    //
    // Per-request kill guarantees: the session's active/revoked/expiry window and the
    // caller's CURRENT global-super status are both re-evaluated every request. That
    // catches a membership-based demotion (membership re-read below) and a
    // bootstrap-domain demotion (re-applied from config in ValidateRequest). The one
    // gap is a super asserted purely by a static token role claim, which — like all
    // token claims in this app — only changes on token refresh; use session revoke to
    // kill such a session immediately.
    private static async Task<UserContext?> TryResolveImpersonationAsync(
        HttpRequestData req, UserContext realCtx, string sid, ILogger logger)
    {
        try
        {
            if (req.FunctionContext.InstanceServices.GetService(typeof(CosmosService)) is not CosmosService cosmos)
                return null;

            var session = await cosmos.GetImpersonationSessionAsync(sid, realCtx.UserId);
            if (session == null
                || !session.IsActive(DateTime.UtcNow)
                || !string.Equals(session.AdminUserId, realCtx.UserId, StringComparison.Ordinal))
                return null;

            // Re-verify the caller is STILL a global super. Fast-path the token claim,
            // else re-read memberships so a membership-based demotion is caught mid-session.
            var isSuper = realCtx.IsSuperAdmin;
            if (!isSuper)
            {
                var memberships = await cosmos.GetMembershipsByExternalIdAsync(realCtx.UserId);
                isSuper = memberships.Any(m => string.Equals(m.AdminLevel, AdminLevels.SuperAdmin, StringComparison.OrdinalIgnoreCase));
            }
            if (!isSuper) return null;

            return new UserContext
            {
                UserId = session.TargetActingId,
                TenantId = session.TargetTenantId,
                AdminLevel = session.TargetAdminLevel,
                Email = session.TargetEmail,
                DisplayName = session.TargetName,
                RealUserId = realCtx.UserId,
                IsImpersonating = true,
                ImpersonationSessionId = session.Id,
                ImpersonationMode = session.Mode,
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Impersonation resolution failed for session {Sid}; proceeding as real user", sid);
            return null;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<HttpResponseData> WriteUnauthorized(HttpRequestData req, string message)
    {
        var res = req.CreateResponse(HttpStatusCode.Unauthorized);
        await res.WriteStringAsync(JsonSerializer.Serialize(new { error = message }));
        res.Headers.Add("Content-Type", "application/json");
        return res;
    }

    private static async Task<HttpResponseData> WriteForbidden(HttpRequestData req, string message)
    {
        var res = req.CreateResponse(HttpStatusCode.Forbidden);
        await res.WriteStringAsync(JsonSerializer.Serialize(new { error = message }));
        res.Headers.Add("Content-Type", "application/json");
        return res;
    }

    // 409 with a distinct code so the client can cleanly exit impersonation (and NOT
    // treat it as a normal session logout, which a 401 would trigger).
    private static async Task<HttpResponseData> WriteImpersonationExpired(HttpRequestData req)
    {
        var res = req.CreateResponse(HttpStatusCode.Conflict);
        await res.WriteStringAsync(JsonSerializer.Serialize(new
        {
            error = "Your impersonation session has ended.",
            code = "impersonation_expired",
        }));
        res.Headers.Add("Content-Type", "application/json");
        return res;
    }

    private static IEnumerable<string> BuildValidAudiences(AuthConfig config)
    {
        // Entra access tokens may emit aud as either api://<clientId> or <clientId>.
        var audiences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddAudience(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            var trimmed = value.Trim();
            audiences.Add(trimmed);

            const string apiPrefix = "api://";
            if (trimmed.StartsWith(apiPrefix, StringComparison.OrdinalIgnoreCase))
            {
                audiences.Add(trimmed[apiPrefix.Length..]);
            }
            else
            {
                audiences.Add($"{apiPrefix}{trimmed}");
            }
        }

        AddAudience(config.Audience);
        AddAudience(config.ClientId);

        return audiences;
    }
}

/// <summary>
/// Extracted, validated user information from the JWT token.
/// Available to all functions after auth passes.
/// </summary>
public class UserContext
{
    public string UserId      { get; set; } = string.Empty;
    public string TenantId    { get; set; } = string.Empty;
    // The caller's level as claimed by the token (already translated from the
    // legacy role claim). Per-org authorization still resolves the actual
    // membership level via CosmosService.ResolveActorInOrgAsync.
    public string AdminLevel  { get; set; } = AdminLevels.Member;
    public string Email       { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string GivenName   { get; set; } = string.Empty;
    public string FamilyName  { get; set; } = string.Empty;

    // ── Impersonation (#26) ──────────────────────────────────────────────────
    // When impersonating, UserId/TenantId/AdminLevel are the TARGET's (so all
    // downstream authz/data act as the target), while RealUserId stays the acting
    // SuperAdmin for audit and guardrails. Defaults: RealUserId == UserId, not impersonating.
    public string RealUserId   { get; set; } = string.Empty;
    public bool   IsImpersonating { get; set; }
    public string? ImpersonationSessionId { get; set; }
    public string  ImpersonationMode { get; set; } = string.Empty;

    public bool IsSuperAdmin  => string.Equals(AdminLevel, AdminLevels.SuperAdmin, StringComparison.OrdinalIgnoreCase);
    // Base admin level = no admin rights (rank 0). Named for the DO axis (AdminLevels.Member),
    // NOT the WHO axis — a person at this level need not be a PersonTypes.Student.
    public bool IsMemberLevel => AdminLevels.RankOf(AdminLevel) == 0;
}
