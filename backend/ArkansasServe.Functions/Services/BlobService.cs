using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ArkansasServe.Functions.Services;

public class BlobService
{
    private readonly BlobServiceClient? _client;
    private readonly string _accountName;
    private readonly string _accountKey;
    private readonly ILogger<BlobService> _logger;

    public BlobService(IConfiguration config, ILogger<BlobService> logger)
    {
        _logger = logger;

        // Parse account name and key from connection string for SAS generation
        var connStr = config["BlobStorage__ConnectionString"] ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(connStr))
            _client = new BlobServiceClient(connStr);

        _accountName = ParseConnectionStringPart(connStr, "AccountName");
        _accountKey  = ParseConnectionStringPart(connStr, "AccountKey");

        if (_client is null)
            _logger.LogWarning("BlobStorage__ConnectionString is not configured. Blob upload/read endpoints will be unavailable.");
    }

    /// <summary>
    /// Generates a short-lived SAS URL that the frontend can use to upload
    /// a file directly to Blob Storage (never going through the Functions API).
    /// </summary>
    public string GenerateUploadSasToken(string containerName, string blobName, int expiryMinutes = 15)
    {
        EnsureConfigured();
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
        var blobClient = _client!.GetBlobContainerClient(containerName).GetBlobClient(blobName);
        return $"{blobClient.Uri}?{sasQueryParams}";
    }

    /// <summary>
    /// Generates a short-lived SAS URL for reading a private blob
    /// (used for verification documents that should not be publicly accessible).
    /// </summary>
    public string GenerateReadSasToken(string containerName, string blobName, int expiryMinutes = 60)
    {
        EnsureConfigured();
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
        var blobClient = _client!.GetBlobContainerClient(containerName).GetBlobClient(blobName);
        return $"{blobClient.Uri}?{sasQueryParams}";
    }

    /// <summary>
    /// Returns the permanent public URL for a blob. Only valid for a container with an
    /// effective anonymous-read policy. NOTE: the Arkansas Serve storage account sets
    /// allowBlobPublicAccess=false, so no container is public — do not use this for
    /// event-photos/org-logos/verification-docs; use <see cref="GenerateReadSasToken"/>.
    /// Retained only for a future genuinely-public container.
    /// </summary>
    public string GetPublicBlobUrl(string containerName, string blobName)
    {
        EnsureConfigured();
        return _client!.GetBlobContainerClient(containerName)
                      .GetBlobClient(blobName).Uri.ToString();
    }

    /// <summary>
    /// Resolves a display URL for an optionally-private asset (event photo, org logo):
    /// prefers a read SAS for <paramref name="blobName"/>; else re-signs a legacy bare
    /// own-account <paramref name="storedUrl"/>; else returns the stored URL unchanged
    /// (an external URL) or null. Never throws — a signing failure falls back to the stored
    /// value so a read is never broken by blob issues.
    /// </summary>
    public string? ResolveDisplayUrl(string containerName, string? blobName, string? storedUrl, int expiryMinutes = 60)
    {
        try
        {
            var name = !string.IsNullOrWhiteSpace(blobName)
                ? blobName
                : TryGetOwnedBlobName(containerName, storedUrl);

            return string.IsNullOrWhiteSpace(name)
                ? storedUrl
                : GenerateReadSasToken(containerName, name, expiryMinutes);
        }
        catch
        {
            return storedUrl;
        }
    }

    /// <summary>
    /// If <paramref name="url"/> is a bare (unsigned) blob URL that lives under this
    /// storage account's <paramref name="containerName"/>, returns the blob name so it
    /// can be re-signed; otherwise null. Lets read paths sign legacy photos that stored a
    /// public URL, and leaves external URLs (e.g. crawled event images on other hosts)
    /// untouched. Returns null when storage is unconfigured rather than throwing.
    /// </summary>
    public string? TryGetOwnedBlobName(string containerName, string? url)
    {
        if (_client is null || string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Host, _client.Uri.Host, StringComparison.OrdinalIgnoreCase)) return null;

        var prefix = $"/{containerName}/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;

        var blobName = Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..]);
        return string.IsNullOrWhiteSpace(blobName) ? null : blobName;
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

    private void EnsureConfigured()
    {
        if (_client is null || string.IsNullOrWhiteSpace(_accountName) || string.IsNullOrWhiteSpace(_accountKey))
            throw new InvalidOperationException("Blob storage is not configured. Set BlobStorage__ConnectionString in app settings.");
    }
}
