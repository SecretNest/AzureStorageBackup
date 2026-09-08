using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// A pass-through uploader that records the per-stream memory share (<see cref="UploadMemoryBudget"/>) every volume
/// upload arrived with, keyed by blob name — the proof that a task's cap actually travels to the uploader for
/// each of its volumes, not just that the setting exists. Index and info blobs arrive through the share-less
/// overloads and are recorded as null.
/// </summary>
internal sealed class ShareRecordingUploader(IBlobUploader inner) : IBlobUploader
{
    private readonly List<(string Blob, long? Share)> _seen = [];

    /// <summary>The shares recorded for volume blobs (data/ and packs/), in upload order.</summary>
    public List<long?> VolumeShares
    {
        get { lock (_seen) return [.. _seen.Where(s => s.Blob.StartsWith("data/", StringComparison.Ordinal) || s.Blob.StartsWith("packs/", StringComparison.Ordinal)).Select(s => s.Share)]; }
    }

    private void Note(string blob, long? share) { lock (_seen) _seen.Add((blob, share)); }

    public Task DeleteIfExistsAsync(Account account, string container, string blobName, CancellationToken ct = default)
        => inner.DeleteIfExistsAsync(account, container, blobName, ct);

    public Task<bool> UploadIfMissingAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        Note(blobName, null);
        return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
    }

    public Task<bool> UploadIfMissingAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry, CancellationToken ct,
        IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
    {
        Note(blobName, null);
        return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress);
    }

    public Task<bool> UploadIfMissingAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry, CancellationToken ct,
        IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress, long? inMemoryLimitBytes)
    {
        Note(blobName, inMemoryLimitBytes);
        return inner.UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress, inMemoryLimitBytes);
    }

    public Task UploadOverwriteAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        Note(blobName, null);
        return inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
    }

    public Task UploadOverwriteAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry, CancellationToken ct,
        IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
    {
        Note(blobName, null);
        return inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress);
    }

    public Task UploadOverwriteAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry, CancellationToken ct,
        IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress, long? inMemoryLimitBytes)
    {
        Note(blobName, inMemoryLimitBytes);
        return inner.UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata, progress, inMemoryLimitBytes);
    }
}
