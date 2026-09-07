using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// <c>IBackupInfoStore.ReadIndexAsync</c>/<c>WriteIndexAsync</c> — the whole-index-in-memory pair the product
/// retired — re-expressed over the file-shaped pair that replaced them.
/// <para>
/// Production never materialises a whole version index any more: an index is serialized to a file one entry at a
/// time and uploaded off that file, and comes back down as a file for <see cref="IndexStreamReader"/> to walk. But
/// roughly a hundred and seventy test call sites say "put this hand-written <see cref="VersionIndex"/> in the
/// cloud" or "give me back what is in the cloud so I can assert on it", and at that size a fixture is exactly the
/// right shape — the index is a dozen entries, not a million. Rewriting each of them into a temp file, a stream
/// writer and a stream reader would bury what those tests are actually about under plumbing.
/// </para>
/// <para>
/// So the plumbing lives here, once, and goes through the **production** file pair rather than reimplementing the
/// upload: same blob names, same volume split, same verification, same progress reporting. A test that used to
/// exercise the byte-array path now exercises the file path, which is the one that ships.
/// </para>
/// </summary>
internal static class LegacyIndexStore
{
    /// <summary>Downloads the index (volumes included) and rebuilds it in memory via
    /// <see cref="LegacyIndexSerializer"/>.</summary>
    internal static async Task<VersionIndex> ReadIndexAsync(
        this IBackupInfoStore store, Account account, string container, string indexBlob, string? password,
        int volumes = 1, CancellationToken ct = default)
    {
        var temp = NewTempPath();
        try
        {
            await store.ReadIndexToFileAsync(account, container, indexBlob, password, volumes, temp, ct);
            return LegacyIndexSerializer.DeserializeIndex(await File.ReadAllBytesAsync(temp, ct));
        }
        finally
        {
            try { File.Delete(temp); } catch { /* temp space */ }
        }
    }

    /// <summary>Serializes the index via <see cref="LegacyIndexSerializer"/> and hands the file to
    /// <see cref="IBackupInfoStore.WriteIndexFileAsync"/>, returning its blob name and volume count unchanged.</summary>
    internal static async Task<(string Name, int Volumes)> WriteIndexAsync(
        this IBackupInfoStore store, Account account, string container, int version, VersionIndex index,
        string? password, AccessTier? tier = null, CancellationToken ct = default, StageTracker? progress = null)
    {
        var temp = NewTempPath();
        try
        {
            await File.WriteAllBytesAsync(temp, LegacyIndexSerializer.SerializeIndex(index), ct);
            return await store.WriteIndexFileAsync(account, container, version, temp, password, tier, ct, progress);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* temp space */ }
        }
    }

    private static string NewTempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "asb-legacy-index-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".idx");
    }
}
