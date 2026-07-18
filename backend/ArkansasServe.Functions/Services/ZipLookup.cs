using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArkansasServe.Functions.Services;

/// <summary>
/// Resolves an Arkansas ZIP code to its city, county, and coordinates from a bundled static
/// dataset (#16). No network dependency and no external API — the table is an embedded
/// resource (see Data/ar-zipcodes.json, GeoNames CC BY 4.0), loaded once as a singleton.
///
/// AR-only on purpose: this platform serves Arkansas schools and juvenile-service orgs, so a
/// nationwide table would be dead weight. A ZIP outside the set simply misses — callers treat
/// a miss as "we couldn't auto-fill" and let the user type city/county by hand.
/// </summary>
public class ZipLookup
{
	private readonly IReadOnlyDictionary<string, ZipInfo> _byZip;

	public ZipLookup()
	{
		var asm = Assembly.GetExecutingAssembly();
		// Match by suffix so the logical resource name (namespace + folder) can change without
		// breaking the load — there is exactly one such resource.
		var resourceName = asm.GetManifestResourceNames()
			.FirstOrDefault(n => n.EndsWith("ar-zipcodes.json", StringComparison.OrdinalIgnoreCase))
			?? throw new InvalidOperationException("Embedded resource ar-zipcodes.json was not found.");

		using var stream = asm.GetManifestResourceStream(resourceName)
			?? throw new InvalidOperationException($"Could not open embedded resource {resourceName}.");

		_byZip = JsonSerializer.Deserialize<Dictionary<string, ZipInfo>>(stream)
			?? new Dictionary<string, ZipInfo>();
	}

	/// <summary>Number of ZIPs in the table — used by the health/self-check.</summary>
	public int Count => _byZip.Count;

	/// <summary>
	/// Looks up a ZIP. Only the 5-digit prefix is considered, so "72201-1234" resolves like
	/// "72201". Returns null on any miss (unknown or non-Arkansas ZIP).
	/// </summary>
	public ZipInfo? Lookup(string? zip)
	{
		if (string.IsNullOrWhiteSpace(zip)) return null;
		var key = zip.Trim();
		if (key.Length > 5) key = key[..5];
		return _byZip.TryGetValue(key, out var info) ? info : null;
	}
}

/// <summary>One ZIP's resolved place. Shape mirrors the bundled JSON entries.</summary>
public sealed record ZipInfo(
	[property: JsonPropertyName("city")]   string City,
	[property: JsonPropertyName("county")] string County,
	[property: JsonPropertyName("lat")]    double Lat,
	[property: JsonPropertyName("lng")]    double Lng);
