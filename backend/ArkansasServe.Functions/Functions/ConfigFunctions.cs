using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

/// <summary>
/// Client configuration the frontend needs at runtime but must not have hardcoded.
/// </summary>
public class ConfigFunctions(IConfiguration config, AuthConfig authConfig, ILogger<ConfigFunctions> logger)
{
	/// <summary>
	/// GET /api/config/maps → { enabled, apiKey }
	///
	/// Serves the Google Maps browser key from the `GoogleMaps__ApiKey` app setting.
	///
	/// A Maps browser key is PUBLIC BY DESIGN — it ships inside client JavaScript and cannot be
	/// hidden. The control is RESTRICTION, not secrecy: the key is HTTP-referrer restricted to
	/// arkansasserve.com and API-restricted to Maps JavaScript / Geocoding / Places. Do not treat
	/// its appearance in a bundle as a leak, and do not try to proxy Maps calls through the backend
	/// to "hide" it.
	///
	/// It is served from an app setting rather than hardcoded so it can be ROTATED without a code
	/// change and never has to enter this repository, which is public. Set it directly on the
	/// Function App — NOT via Bicep, which is non-authoritative and would clobber it (same rule as
	/// Crawler__SharedSecret).
	///
	/// Requires auth: it is not a secret, but there is no reason to hand it to anonymous callers.
	/// Absent setting → enabled:false rather than an error, so the client degrades to the bundled
	/// ZIP dataset (#16) instead of breaking the event form.
	/// </summary>
	[Function("GetMapsConfig")]
	public async Task<HttpResponseData> GetMapsConfig(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "config/maps")] HttpRequestData req)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var key = config["GoogleMaps__ApiKey"] ?? config["GoogleMaps:ApiKey"];
		if (string.IsNullOrWhiteSpace(key))
		{
			// Logged once per call at Debug rather than Warning: "not configured" is a valid
			// deployment state (the free ZIP path still works), not a fault.
			logger.LogDebug("[Config] GoogleMaps__ApiKey is not set; maps features are disabled");
			return await HttpHelper.OkJson(req, new { enabled = false, apiKey = (string?)null });
		}

		return await HttpHelper.OkJson(req, new { enabled = true, apiKey = key });
	}
}
