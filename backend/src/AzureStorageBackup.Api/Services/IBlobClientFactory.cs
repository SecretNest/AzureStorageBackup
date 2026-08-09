using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>The result of a connectivity test.</summary>
public record ConnectionResult(bool Success, string? Error);

/// <summary>Builds a BlobServiceClient from an account's configuration (credentials, region, proxy) and offers a connectivity test.</summary>
public interface IBlobClientFactory
{
    BlobServiceClient CreateServiceClient(Account account);

    Task<ConnectionResult> TestConnectionAsync(Account account, CancellationToken ct = default);
}
