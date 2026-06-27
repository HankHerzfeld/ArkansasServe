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
            var connectionString = config["CosmosDb__ConnectionString"]
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
            TenantId = config["Entra__TenantId"] ?? throw new InvalidOperationException("Entra__TenantId is not set."),
            ClientId = config["Entra__ClientId"] ?? throw new InvalidOperationException("Entra__ClientId is not set."),
            Audience = config["Entra__Audience"] ?? throw new InvalidOperationException("Entra__Audience is not set.")
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
