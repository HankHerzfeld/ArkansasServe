using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Services;

public class BlobService
{
    private readonly BlobServiceClient _client;
    private readonly string _accountName;
    private readonly string _accountKey;
    private readonly ILogger<BlobService> _logger;

    public BlobService(BlobServiceClient client, IConfiguration config, ILogger<BlobService> logger)
    {
        _client = client;
        _logger = logger;

        // Parse account name and key from connection string for SAS generation
        var connStr = config["BlobStorage__ConnectionString"] ?? string.Empty;
        _accountName = ParseConnectionStringPart(connStr, "AccountName");
        _accountKey  = ParseConnectionStringPart(connStr, "AccountKey");

        if (string.IsNullOrWhiteSpace(_accountName) || string.IsNullOrWhiteSpace(_accountKey))
            throw new InvalidOperationException("BlobStorage__ConnectionString must include AccountName and AccountKey for SAS generation.");
    }

    /// <summary>
    /// Generates a short-lived SAS URL that the frontend can use to upload
    /// a file directly to Blob Storage (never going through the Functions API).
    /// </summary>
    public string GenerateUploadSasToken(string containerName, string blobName, int expiryMinutes = 15)
    {
        if (string.IsNullOrWhiteSpace(containerName))
            throw new ArgumentException("Container name is required.", nameof(containerName));
        if (string.IsNullOrWhiteSpace(blobName))
            throw new ArgumentException("Blob name is required.", nameof(blobName));

        var boundedExpiry = Math.Clamp(expiryMinutes, 1, 60);
        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            BlobName          = blobName,
            Resource          = "b",
            StartsOn          = DateTimeOffset.UtcNow.AddMinutes(-2),
            ExpiresOn         = DateTimeOffset.UtcNow.AddMinutes(boundedExpiry)
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Write | BlobSasPermissions.Create);

        var credential = new StorageSharedKeyCredential(_accountName, _accountKey);
        var sasQueryParams = sasBuilder.ToSasQueryParameters(credential);
        var blobClient = _client.GetBlobContainerClient(containerName).GetBlobClient(blobName);
        return $"{blobClient.Uri}?{sasQueryParams}";
    }

    /// <summary>
    /// Generates a short-lived SAS URL for reading a private blob
    /// (used for verification documents that should not be publicly accessible).
    /// </summary>
    public string GenerateReadSasToken(string containerName, string blobName, int expiryMinutes = 60)
    {
        if (string.IsNullOrWhiteSpace(containerName))
            throw new ArgumentException("Container name is required.", nameof(containerName));
        if (string.IsNullOrWhiteSpace(blobName))
            throw new ArgumentException("Blob name is required.", nameof(blobName));

        var boundedExpiry = Math.Clamp(expiryMinutes, 1, 240);
        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            BlobName          = blobName,
            Resource          = "b",
            StartsOn          = DateTimeOffset.UtcNow.AddMinutes(-2),
            ExpiresOn         = DateTimeOffset.UtcNow.AddMinutes(boundedExpiry)
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        var credential = new StorageSharedKeyCredential(_accountName, _accountKey);
        var sasQueryParams = sasBuilder.ToSasQueryParameters(credential);
        var blobClient = _client.GetBlobContainerClient(containerName).GetBlobClient(blobName);
        return $"{blobClient.Uri}?{sasQueryParams}";
    }

    /// <summary>
    /// Returns the permanent public URL for a blob in a public-read container
    /// (e.g. event-photos, org-logos — set via container access policy).
    /// </summary>
    public string GetPublicBlobUrl(string containerName, string blobName)
    {
        return _client.GetBlobContainerClient(containerName)
                      .GetBlobClient(blobName).Uri.ToString();
    }

    /// <summary>
    /// Generates a unique blob name for a file upload.
    /// Format: {prefix}/{year}/{month}/{guid}{extension}
    /// </summary>
    public static string GenerateBlobName(string prefix, string originalFileName)
    {
        var ext = Path.GetExtension(originalFileName).ToLowerInvariant();
        var now = DateTime.UtcNow;
        return $"{prefix}/{now.Year}/{now.Month:D2}/{Guid.NewGuid()}{ext}";
    }

    private static string ParseConnectionStringPart(string connectionString, string key)
    {
        foreach (var part in connectionString.Split(';'))
        {
            var idx = part.IndexOf('=');
            if (idx > 0 && part[..idx].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return part[(idx + 1)..].Trim();
        }
        return string.Empty;
    }
}
