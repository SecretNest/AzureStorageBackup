using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>The Archive rehydration helper: starts rehydration for an archive including all of its volumes. Shared by check and restore so that only the first volume is never rehydrated alone.</summary>
public static class BlobRehydration
{
    /// <summary>From a snapshot of (volume name, AccessTier, ArchiveStatus), pick the volumes needing rehydration: still Archive and not already rehydrating.</summary>
    public static IReadOnlyList<string> SelectToBegin(
        IEnumerable<(string Name, string? AccessTier, string? ArchiveStatus)> volumes) =>
        volumes.Where(v => v.AccessTier == "Archive" && string.IsNullOrEmpty(v.ArchiveStatus))
               .Select(v => v.Name).ToList();

    /// <summary>Enumerate every volume under the baseRef prefix and call SetAccessTier on those needing it (best effort).</summary>
    public static async Task BeginAsync(BlobContainerClient container, string baseRef, AccessTier tier, CancellationToken ct)
    {
        // AccessTier and ArchiveStatus already come back with List Blobs, so read them from the listing rather than issuing a GetProperties per volume.
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
