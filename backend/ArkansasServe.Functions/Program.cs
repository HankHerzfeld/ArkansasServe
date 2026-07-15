using ArkansasServe.Functions.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;

        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Cosmos DB - singleton client for connection reuse.
        services.AddSingleton(_ =>
        {
            var connectionString =
                config["CosmosDb__ConnectionString"]
                ?? config["CosmosDb:ConnectionString"]
                ?? throw new InvalidOperationException("CosmosDb__ConnectionString is not set.");
            return new CosmosClient(connectionString, new CosmosClientOptions
            {
                SerializerOptions = new CosmosSerializationOptions
                {
                    PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
                }
            });
        });

        services.AddSingleton<CosmosService>();
        services.AddSingleton<BlobService>();
        services.AddSingleton<EmailService>();
        services.AddSingleton<CrawlerService>();

        services.AddSingleton(_ => new AuthConfig
        {
            TenantId = config["Entra__TenantId"] ?? config["Entra:TenantId"] ?? throw new InvalidOperationException("Entra__TenantId is not set."),
            ClientId = config["Entra__ClientId"] ?? config["Entra:ClientId"] ?? throw new InvalidOperationException("Entra__ClientId is not set."),
            Audience = config["Entra__Audience"] ?? config["Entra:Audience"] ?? throw new InvalidOperationException("Entra__Audience is not set."),
            // Optional bootstrap: emails on this domain are elevated to PlatformAdmin.
            // Set it only while seeding the first admin, then clear it (see AuthMiddleware).
            PlatformAdminEmailDomain = config["Entra__PlatformAdminEmailDomain"] ?? config["Entra:PlatformAdminEmailDomain"],
            // Optional. Enables the M2M header on the crawl route only. Unset = no such path.
            CrawlerSharedSecret = config["Crawler__SharedSecret"] ?? config["Crawler:SharedSecret"]
        });

        services.AddHttpClient();
    })
    .Build();


host.Run();

// Simple config record passed via DI
public record AuthConfig
{
    public required string TenantId { get; init; }
    public required string ClientId { get; init; }
    public required string Audience  { get; init; }

    /// <summary>
    /// Optional. When set (e.g. "arkansasserve.com"), signed-in users whose email
    /// ends with @thisdomain are elevated to PlatformAdmin. Leave unset/empty in
    /// normal operation — use only to seed the first admin account.
    /// </summary>
    public string? PlatformAdminEmailDomain { get; init; }

    /// <summary>
    /// Optional. A high-entropy shared secret that lets an unattended caller (the daily
    /// crawler workflow) authenticate to POST /manage/events/crawl WITHOUT an Entra JWT,
    /// by sending it in the X-Crawler-Secret header.
    ///
    /// This exists because Entra access tokens expire in ~1h, so a static token in a
    /// GitHub secret can never drive a scheduled job. It is deliberately a weaker,
    /// second auth path and is therefore scoped to that ONE route: the crawl queue,
    /// publish and dismiss routes remain JWT + SuperAdmin only.
    ///
    /// Leave unset and the header path does not exist at all — an absent or blank
    /// setting disables it rather than accepting a blank header (fail closed).
    /// Generate with: openssl rand -base64 48
    /// </summary>
    public string? CrawlerSharedSecret { get; init; }
}
