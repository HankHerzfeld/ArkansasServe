namespace ArkansasServe.Functions.Models;

/// <summary>
/// Intermediate DTO produced by every source adapter in <see cref="Services.CrawlerService"/>.
/// The crawler maps each source's response into this common shape before writing
/// an <see cref="Event"/> document to Cosmos DB.
/// </summary>
public sealed record CrawledEvent
{
    /// <summary>
    /// Globally-unique deduplication key, formatted as "{sourceName}:{externalId}".
    /// Used to skip re-importing an event that already exists in the database.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>Human-readable source platform name, e.g. "GivePulse".</summary>
    public required string SourceName { get; init; }

    /// <summary>Direct URL to the original listing page on the source platform.</summary>
    public required string SourceUrl { get; init; }

    public required string Title { get; init; }
    public string? Description { get; init; }
    public string Location { get; init; } = string.Empty;
    public DateTime StartDateTime { get; init; }
    public DateTime EndDateTime { get; init; }

    /// <summary>
    /// Estimated volunteer hours. If the source does not publish hours, defaults to 0
    /// and the admin sets the value when reviewing the draft.
    /// </summary>
    public double HoursEstimate { get; init; }

    public string OrganizationName { get; init; } = string.Empty;

    /// <summary>Contact email for the hosting organization, if published by the source.</summary>
    public string? ContactEmail { get; init; }

    /// <summary>Contact phone for the hosting organization, if published by the source.</summary>
    public string? ContactPhone { get; init; }

    /// <summary>
    /// Website or profile URL for the hosting organization
    /// (org website, Facebook page, GivePulse profile, etc.).
    /// </summary>
    public string? ContactUrl { get; init; }

    /// <summary>Raw JSON from the source API, preserved for debugging and future re-parsing.</summary>
    public string? RawJson { get; init; }
}
