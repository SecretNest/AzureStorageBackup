using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>连通测试结果。</summary>
public record ConnectionResult(bool Success, string? Error);

/// <summary>按账户配置（凭据、分区、代理）构造 BlobServiceClient，并提供连通测试。</summary>
public interface IBlobClientFactory
{
    BlobServiceClient CreateServiceClient(Account account);

    Task<ConnectionResult> TestConnectionAsync(Account account, CancellationToken ct = default);
}
