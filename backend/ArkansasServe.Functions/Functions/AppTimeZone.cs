namespace ArkansasServe.Functions.Functions;

/// <summary>
/// The platform's local zone. Arkansas is entirely US Central, and every "which day is this?"
/// question has to be answered locally: a UTC date rolls over at 6/7pm Central, so an evening
/// event would otherwise look like it spans two days (and an overnight one like it spans three).
///
/// IANA id, not the Windows one — .NET 8 ships ICU on Windows too, so "America/Chicago" resolves
/// on both, whereas "Central Standard Time" would not resolve on Linux. Same reasoning (and the
/// same id) as the recurrence expander's DST handling.
/// </summary>
public static class AppTimeZone
{
	public static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

	/// <summary>The local calendar day a UTC instant falls on.</summary>
	public static DateTime LocalDay(DateTime utc) =>
		TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Central).Date;
}
