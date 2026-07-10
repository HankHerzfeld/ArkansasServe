using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using ArkansasServe.Functions.Functions;
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
                TenantId    = Claim("extension_OrganizationId") ?? Claim("extension_SchoolId") ?? Claim("extension_TenantId") ?? Claim("tid") ?? string.Empty,
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
                    userContext.TenantId = "arkansas-serve-root";
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
    public string AdminLevel  { get; set; } = AdminLevels.Student;
    public string Email       { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string GivenName   { get; set; } = string.Empty;
    public string FamilyName  { get; set; } = string.Empty;

    public bool IsSuperAdmin   => string.Equals(AdminLevel, AdminLevels.SuperAdmin, StringComparison.OrdinalIgnoreCase);
    public bool IsStudentLevel => AdminLevels.RankOf(AdminLevel) == 0;
}
