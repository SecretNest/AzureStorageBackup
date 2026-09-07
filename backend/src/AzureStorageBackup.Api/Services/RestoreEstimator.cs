using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>The estimated download and extraction volume for a restore (§4.1b, requirement A).
/// DistinctObjects holds storage keys ("pack:{Ref}" / "blob:{Ref}") so the endpoint can HEAD each one's
/// first volume to determine its rehydration state (decision 5).</summary>
public sealed record RestoreEstimate(long DownloadBytes, long UncompressedBytes, int FileCount, IReadOnlyList<string> DistinctObjects);

/// <summary>
/// Estimating a restore (pure logic, no network): the selected entries, deduplicated by stored object (a shared pack
/// or a deduplicated blob counts once), summing the download size (volume sizes) and the extracted size (the files'
/// lengths).
/// <para>
/// It used to be handed the whole version index plus the selected paths and do the lookup itself, which meant an
/// estimate for three files read a million entries. The caller now looks the selection up in the catalog
/// (<see cref="VersionCatalog.EntriesAtAsync"/>) and passes only what it found; a path the version does not hold is
/// simply absent, exactly as it was silently absent from the old filter.
/// </para>
/// </summary>
public static class RestoreEstimator
{
    /// <param name="selected">The entries at the selected paths. Entries with no storage are dropped here rather than
    /// by the caller: they cost nothing to download and are not files a restore fetches, so counting them would
    /// inflate the file count the user is shown before consenting.</param>
    public static RestoreEstimate Compute(IReadOnlyList<IndexEntry> selected, BackupInfoFile info)
    {
        long uncompressed = 0;
        long download = 0;
        var files = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var e in selected)
        {
            if (e.Storage is not { } storage)
                continue;

            files++;
            uncompressed += e.Length;

            var key = StorageKey(storage);
            if (!seen.Add(key))
                continue;

            download += storage.Kind == "pack"
                ? (info.Packs.TryGetValue(storage.Ref, out var pack) ? pack.VolumeSizes.Sum() : 0)
                : storage.VolumeSizes.Sum();
        }

        return new RestoreEstimate(download, uncompressed, files, seen.ToList());
    }

    private static string StorageKey(StorageRef s) => s.Kind == "pack" ? "pack:" + s.Ref : "blob:" + s.Ref;
}
