using System.Net;
using ArkansasServe.Functions.Middleware;
using ArkansasServe.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Functions;

/// <summary>
/// ZIP → city / county / coordinates lookup for the event create form (#16). Backed by the
/// bundled AR dataset (<see cref="ZipLookup"/>), so there is no external geocoding API and no
/// billing. Reference data only: any signed-in user may call it; nothing here is org-scoped.
/// </summary>
public class GeoFunctions(ZipLookup zips, AuthConfig authConfig, ILogger<GeoFunctions> logger)
{
	/// <summary>
	/// GET /api/geo/zip/{zip}
	/// Returns { zip, city, county, latitude, longitude } for a known Arkansas ZIP, or 404 for
	/// an unknown / out-of-state one so the client can fall back to manual entry.
	/// </summary>
	[Function("LookupZip")]
	public async Task<HttpResponseData> LookupZip(
		[HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "geo/zip/{zip}")] HttpRequestData req,
		string zip)
	{
		var (ctx, authError) = await AuthMiddleware.ValidateRequest(req, authConfig, logger);
		if (ctx == null) return authError!;

		var info = zips.Lookup(zip);
		if (info == null)
			return await HttpHelper.Error(req, HttpStatusCode.NotFound, "That ZIP code isn't in the Arkansas lookup. Enter the city and county by hand.");

		var normalized = zip.Trim();
		if (normalized.Length > 5) normalized = normalized[..5];

		return await HttpHelper.OkJson(req, new
		{
			zip = normalized,
			city = info.City,
			county = info.County,
			latitude = info.Lat,
			longitude = info.Lng,
		});
	}
}
