using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace ArkansasServe.Functions.Middleware;

/// <summary>
/// Validates the Bearer token on every HTTP request.
/// Attaches a ClaimsIdentity to the request context so functions can
/// call req.GetUserContext() to get userId, tenantId, and role.
/// </summary>
public static class AuthMiddleware
{
    public static async Task<(UserContext? Context, HttpResponseData? ErrorResponse)> ValidateRequest(
        HttpRequestData req,
        AuthConfig config,
        ILogger logger,
        params string[] requiredRoles)
    {
        // Extract Bearer token
        if (!req.Headers.TryGetValues("Authorization", out var authHeaders))
            return (null, await WriteUnauthorized(req, "Missing Authorization header"));

        var token = authHeaders.FirstOrDefault()?.Replace("Bearer ", "").Trim();
        if (string.IsNullOrEmpty(token))
            return (null, await WriteUnauthorized(req, "Empty token"));

        try
        {
            var signingKeys = await GetSigningKeys(config.TenantId, logger);
            if (signingKeys.Count == 0)
                return (null, await WriteUnauthorized(req, "Invalid token"));

            // Fetch OIDC signing keys from Entra External ID tenant
            var handler = new JwtSecurityTokenHandler();
            var validationParams = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers =
                [
                    $"https://{config.TenantId}.ciamlogin.com/{config.TenantId}/",
                    $"https://{config.TenantId}.ciamlogin.com/{config.TenantId}/v2.0",
                    $"https://{config.TenantId}.ciamlogin.com/{config.TenantId}/v2.0/"
                ],
                ValidateAudience = true,
                ValidAudiences = BuildValidAudiences(config),
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromMinutes(2),
                IssuerSigningKeyResolver = (_, _, _, _) => signingKeys
            };

            var principal = handler.ValidateToken(token, validationParams, out _);

            string? Claim(string type) => principal.Claims.FirstOrDefault(c => c.Type == type)?.Value;
            var roleFromRolesClaim = principal.Claims.Where(c => c.Type == "roles").Select(c => c.Value).FirstOrDefault();

            var userContext = new UserContext
            {
                UserId      = Claim("oid") ?? Claim("sub") ?? string.Empty,
                TenantId    = Claim("extension_OrganizationId") ?? Claim("extension_SchoolId") ?? Claim("extension_TenantId") ?? Claim("tid") ?? string.Empty,
                Role        = Claim("extension_Role") ?? roleFromRolesClaim ?? "Student",
                Email       = Claim("email") ?? Claim("preferred_username") ?? string.Empty,
                DisplayName = Claim("name") ?? string.Empty
            };

            if (userContext.Email.EndsWith("@arkansasserve.com", StringComparison.OrdinalIgnoreCase))
            {
                userContext.Role = "PlatformAdmin";
                if (string.IsNullOrWhiteSpace(userContext.TenantId))
                    userContext.TenantId = "arkansas-serve-root";
            }

            // Some Entra-issued access tokens in this SWA + external Function App
            // topology do not include azp/appid consistently. Only reject when the
            // client claim is present and clearly mismatched.
            var callingClientId = Claim("azp") ?? Claim("appid");
            if (!string.IsNullOrWhiteSpace(config.ClientId)
                && !string.IsNullOrWhiteSpace(callingClientId)
                && !string.Equals(callingClientId, config.ClientId, StringComparison.OrdinalIgnoreCase))
                return (null, await WriteUnauthorized(req, "Invalid token"));

            if (string.IsNullOrWhiteSpace(userContext.UserId))
                return (null, await WriteUnauthorized(req, "Invalid token"));

            // Role check — return 403 Forbidden so callers can distinguish
            // "not authenticated" (401) from "authenticated but wrong role" (403)
            if (requiredRoles.Length > 0 &&
                !requiredRoles.Contains(userContext.Role, StringComparer.OrdinalIgnoreCase))
                return (null, await WriteForbidden(req, "Forbidden"));

            return (userContext, null);
        }
        catch (SecurityTokenException ex)
        {
            logger.LogWarning("Token validation failed: {Message}", ex.Message);
            return (null, await WriteUnauthorized(req, "Invalid or expired token"));
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

    private static readonly HttpClient _httpClient = new();
    private static readonly ConcurrentDictionary<string, (List<SecurityKey> Keys, DateTime Expiry)> _keyCache = new();

    private static async Task<List<SecurityKey>> GetSigningKeys(string tenantId, ILogger logger)
    {
        // Cache keys for 1 hour (they rarely change)
        if (_keyCache.TryGetValue(tenantId, out var cached) && DateTime.UtcNow < cached.Expiry)
            return cached.Keys;

        try
        {
            var url = $"https://{tenantId}.ciamlogin.com/{tenantId}/discovery/v2.0/keys";
            var json = await _httpClient.GetStringAsync(url);
            var doc = JsonDocument.Parse(json);
            var keys = new List<SecurityKey>();

            foreach (var key in doc.RootElement.GetProperty("keys").EnumerateArray())
            {
                var n = key.GetProperty("n").GetString();
                var e = key.GetProperty("e").GetString();
                if (n != null && e != null)
                {
                    var rsa = new System.Security.Cryptography.RSACryptoServiceProvider();
                    rsa.ImportParameters(new System.Security.Cryptography.RSAParameters
                    {
                        Modulus = Base64UrlDecode(n),
                        Exponent = Base64UrlDecode(e)
                    });
                    keys.Add(new RsaSecurityKey(rsa));
                }
            }

            _keyCache[tenantId] = (keys, DateTime.UtcNow.AddHours(1));
            return keys;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch Entra signing keys");
            return [];
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "=";  break;
        }
        return Convert.FromBase64String(s);
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
    public string Role        { get; set; } = string.Empty;
    public string Email       { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    public bool IsStudent      => Role == "Student";
    public bool IsOrgStaff     => Role == "OrgStaff";
    public bool IsSchoolAdmin  => Role == "SchoolAdmin";
    public bool IsPlatformAdmin => Role == "PlatformAdmin";
    public bool IsAdminOrAbove => IsSchoolAdmin || IsPlatformAdmin;
}
