using ArkansasServe.Functions.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Azure.Storage.Blobs;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;

        // Application Insights
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Cosmos DB — registered as singleton (connection pooling)
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var connectionString = cfg["CosmosDb__ConnectionString"]
                ?? throw new InvalidOperationException("CosmosDb__ConnectionString is not set.");
            return new CosmosClient(connectionString, new CosmosClientOptions
            {
                SerializerOptions = new CosmosSerializationOptions
                {
                    PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
                }
            });
        });

        // Blob Storage
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            var connectionString = cfg["BlobStorage__ConnectionString"]
                ?? throw new InvalidOperationException("BlobStorage__ConnectionString is not set.");
            return new BlobServiceClient(connectionString);
        });

        // Application services
        services.AddSingleton<CosmosService>();
        services.AddSingleton<BlobService>();

        // Auth config — resolved lazily so startup does not fail if settings are absent
        services.AddSingleton(sp =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            return new AuthConfig
            {
                TenantId = cfg["Entra__TenantId"] ?? throw new InvalidOperationException("Entra__TenantId is not set."),
                ClientId = cfg["Entra__ClientId"] ?? throw new InvalidOperationException("Entra__ClientId is not set."),
                Audience  = cfg["Entra__Audience"] ?? "api://16150d6e-7d28-4c6b-91b3-4ec839fff75f"
            };
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
}
