using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>Archive 活化助手：对某归档（含全部分卷）发起活化。checker 与 restore 共用，避免只活化首卷。</summary>
public static class BlobRehydration
{
    /// <summary>从（卷名, AccessTier, ArchiveStatus）快照中选出需发起活化的卷：仍是 Archive 且尚未在活化中。</summary>
    public static IReadOnlyList<string> SelectToBegin(
        IEnumerable<(string Name, string? AccessTier, string? ArchiveStatus)> volumes) =>
        volumes.Where(v => v.AccessTier == "Archive" && string.IsNullOrEmpty(v.ArchiveStatus))
               .Select(v => v.Name).ToList();

    /// <summary>枚举 baseRef 前缀全部分卷，对需活化者发起 SetAccessTier（best effort）。</summary>
    public static async Task BeginAsync(BlobContainerClient container, string baseRef, AccessTier tier, CancellationToken ct)
    {
        // AccessTier/ArchiveStatus 已随 List Blobs 返回，直接读列表项，免每卷再发一次 GetProperties。
        var snapshot = new List<(string, string?, string?)>();
        await foreach (var b in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, baseRef, ct))
            snapshot.Add((b.Name, b.Properties.AccessTier?.ToString(), b.Properties.ArchiveStatus?.ToString()));
        foreach (var name in SelectToBegin(snapshot))
        {
            try { await container.GetBlobClient(name).SetAccessTierAsync(tier, cancellationToken: ct); }
            catch { /* best effort */ }
        }
    }
}
