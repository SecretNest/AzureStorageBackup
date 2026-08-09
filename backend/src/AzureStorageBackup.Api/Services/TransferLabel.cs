using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The name and size a transfer stream shows in the UI.
/// <para>
/// Blobs are content-addressed — under encryption they are HMAC gibberish — so <c>data/9f2a3b7c…001</c> means
/// nothing to the person staring at the screen. The upload side switched to source file paths long ago; this
/// gives the download side (restore/verify) the **same shape**, so both halves read as the same thing.
/// </para>
/// </summary>
public static class TransferLabel
{
    /// <param name="members">
    /// The entries this batch is currently handling. A pack reports the member count of **this batch**, not of the
    /// whole pack: a selective restore only pulls a few of them, and reporting the full count would clash with the
    /// numbers shown elsewhere in the UI. A pack holds hundreds of files — no room to list them — so report a count.
    /// </param>
    public static string For(StorageRef storage, IReadOnlyList<IndexEntry> members) =>
        storage.Kind == "pack"
            ? $"pack {storage.Ref} ({members.Count} file{(members.Count == 1 ? "" : "s")})"
            : members.Count > 0 ? members[0].Path : storage.Ref;

    /// <summary>
    /// How many bytes this storage object has to pull down (compressed, all volumes included). The index recorded
    /// each volume's size at backup time, so there is no need to ask the cloud first.
    /// <para>
    /// A pack's volume sizes live in the info file rather than on the entries — dead-weight compaction rewrites the
    /// whole pack, changing volume count and sizes, so entry-level copies would all go stale on every compaction
    /// (the same note is on <c>StorageRef.VolumeSizes</c>).
    /// </para>
    /// <para>0 = cannot be answered (old indexes do not have it). The caller decides from that whether to report a size.</para>
    /// </summary>
    public static long DownloadBytesOf(StorageRef storage, BackupInfoFile info) =>
        storage.Kind == "pack"
            ? info.Packs.TryGetValue(storage.Ref, out var pack) ? pack.VolumeSizes.Sum() : 0
            : storage.VolumeSizes.Sum();
}
