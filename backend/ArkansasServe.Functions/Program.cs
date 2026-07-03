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

        services.AddSingleton(_ => new AuthConfig
        {
            TenantId = config["Entra__TenantId"] ?? config["Entra:TenantId"] ?? throw new InvalidOperationException("Entra__TenantId is not set."),
            ClientId = config["Entra__ClientId"] ?? config["Entra:ClientId"] ?? throw new InvalidOperationException("Entra__ClientId is not set."),
            Audience = config["Entra__Audience"] ?? config["Entra:Audience"] ?? throw new InvalidOperationException("Entra__Audience is not set."),
            // Optional bootstrap: emails on this domain are elevated to PlatformAdmin.
            // Set it only while seeding the first admin, then clear it (see AuthMiddleware).
            PlatformAdminEmailDomain = config["Entra__PlatformAdminEmailDomain"] ?? config["Entra:PlatformAdminEmailDomain"]
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
}
