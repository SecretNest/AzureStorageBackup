using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>The estimated download and extraction volume for a restore (§4.1b, requirement A).
/// DistinctObjects holds storage keys ("pack:{Ref}" / "blob:{Ref}") so the endpoint can HEAD each one's
/// first volume to determine its rehydration state (decision 5).</summary>
public sealed record RestoreEstimate(long DownloadBytes, long UncompressedBytes, int FileCount, IReadOnlyList<string> DistinctObjects);

/// <summary>
/// Estimating a restore (pure logic, no network): selected paths → index entries → deduplicated by stored
/// object (a shared pack or a deduplicated blob counts once), summing the download size (volume sizes) and
/// the extracted size (the files' lengths).
/// </summary>
public static class RestoreEstimator
{
    public static RestoreEstimate Compute(VersionIndex index, BackupInfoFile info, IReadOnlyCollection<string> paths)
    {
        var pathSet = new HashSet<string>(paths, StringComparer.Ordinal);
        var selected = index.Entries.Where(e => pathSet.Contains(e.Path) && e.Storage is not null).ToList();

        long uncompressed = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long download = 0;

        foreach (var e in selected)
        {
            uncompressed += e.Length;

            var storage = e.Storage!;
            var key = StorageKey(storage);
            if (!seen.Add(key))
                continue;

            download += storage.Kind == "pack"
                ? (info.Packs.TryGetValue(storage.Ref, out var pack) ? pack.VolumeSizes.Sum() : 0)
                : storage.VolumeSizes.Sum();
        }

        return new RestoreEstimate(download, uncompressed, selected.Count, seen.ToList());
    }

    private static string StorageKey(StorageRef s) => s.Kind == "pack" ? "pack:" + s.Ref : "blob:" + s.Ref;
}
