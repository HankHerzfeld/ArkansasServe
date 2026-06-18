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
    public static async Task<UserContext?> ValidateRequest(
        HttpRequestData req,
        AuthConfig config,
        ILogger logger,
        params string[] requiredRoles)
    {
        // Extract Bearer token
        if (!req.Headers.TryGetValues("Authorization", out var authHeaders))
        {
            await WriteUnauthorized(req, "Missing Authorization header");
            return null;
        }

        var token = authHeaders.FirstOrDefault()?.Replace("Bearer ", "").Trim();
        if (string.IsNullOrEmpty(token))
        {
            await WriteUnauthorized(req, "Empty token");
            return null;
        }

        try
        {
            // Fetch OIDC signing keys from Entra External ID tenant
            var handler = new JwtSecurityTokenHandler();
            var validationParams = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = $"https://{config.TenantId}.ciamlogin.com/{config.TenantId}/v2.0",
                ValidateAudience = true,
                ValidAudience = config.Audience,
                ValidateLifetime = true,
                IssuerSigningKeyResolver = (token, secToken, kid, validParams) =>
                    GetSigningKeys(config.TenantId, logger).GetAwaiter().GetResult()
            };

            var principal = handler.ValidateToken(token, validationParams, out _);
            var claims = principal.Claims.ToDictionary(c => c.Type, c => c.Value);

            var userContext = new UserContext
            {
                UserId     = claims.GetValueOrDefault("oid") ?? claims.GetValueOrDefault("sub") ?? string.Empty,
                TenantId   = claims.GetValueOrDefault("tid") ?? claims.GetValueOrDefault("tenantId") ?? string.Empty,
                Role       = claims.GetValueOrDefault("extension_Role") ?? claims.GetValueOrDefault("roles") ?? "Student",
                Email      = claims.GetValueOrDefault("email") ?? claims.GetValueOrDefault("preferred_username") ?? string.Empty,
                DisplayName = claims.GetValueOrDefault("name") ?? string.Empty
            };

            // Role check
            if (requiredRoles.Length > 0 && !requiredRoles.Contains(userContext.Role))
            {
                await WriteForbidden(req, $"Role '{userContext.Role}' is not permitted for this action.");
                return null;
            }

            return userContext;
        }
        catch (SecurityTokenException ex)
        {
            logger.LogWarning("Token validation failed: {Message}", ex.Message);
            await WriteUnauthorized(req, "Invalid or expired token");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error during token validation");
            await WriteUnauthorized(req, "Authentication error");
            return null;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task WriteUnauthorized(HttpRequestData req, string message)
    {
        // Note: in isolated worker model we write the response via the request object
        // The calling function must return early after receiving null from ValidateRequest
        // This logs the reason; callers should return a 401 HttpResponseData
        await Task.CompletedTask; // placeholder — callers write the response
        _ = message;              // callers use this for logging
    }

    private static async Task WriteForbidden(HttpRequestData req, string message)
    {
        await Task.CompletedTask;
        _ = message;
    }

    private static readonly Dictionary<string, List<SecurityKey>> _keyCache = new();
    private static DateTime _keyCacheExpiry = DateTime.MinValue;

    private static async Task<List<SecurityKey>> GetSigningKeys(string tenantId, ILogger logger)
    {
        // Cache keys for 1 hour (they rarely change)
        if (_keyCache.ContainsKey(tenantId) && DateTime.UtcNow < _keyCacheExpiry)
            return _keyCache[tenantId];

        try
        {
            using var http = new HttpClient();
            var url = $"https://{tenantId}.ciamlogin.com/{tenantId}/discovery/v2.0/keys";
            var json = await http.GetStringAsync(url);
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

            _keyCache[tenantId] = keys;
            _keyCacheExpiry = DateTime.UtcNow.AddHours(1);
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
