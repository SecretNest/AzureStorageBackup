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
    /// The name for the row of the one item being **produced or unpacked** — compression on the backup side,
    /// extraction on the restore and check side. A pack holds hundreds of files with no room to list them, and its
    /// id is content-addressed, so what identifies it to a person is where its files come from.
    /// <para>
    /// The two biggest contributing directories, then the rest as a count. Ordered by how many files each
    /// contributed so the two named are the two that describe the pack; ties broken by name, because without that
    /// the same pack renders differently between two refreshes and the row reads as having moved on.
    /// </para>
    /// <para>
    /// **It describes the batch it is given, never a pack's manifest** — the same rule the member count in
    /// <see cref="For"/> already follows, and on the restore side it is the whole of what keeps a selective restore
    /// from naming folders the user did not ask for. Hand it the filtered set; there is deliberately no scope
    /// argument here to get wrong.
    /// </para>
    /// <para>
    /// Paths arrive relative to the local root with <c>/</c> separators, which is why the split is on that
    /// character rather than through <c>Path.GetDirectoryName</c> — that one follows the host's separator and would
    /// find no directory at all in <c>photos/2024/a.jpg</c> on Windows. They are printed with a leading slash, the
    /// way this project already writes a root-relative path everywhere the user types one (see the rule lists).
    /// </para>
    /// </summary>
    public static string Folders(IEnumerable<string> paths)
    {
        var byFolder = paths
            .GroupBy(FolderOf, StringComparer.Ordinal)
            .Select(g => (Folder: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Folder, StringComparer.Ordinal)
            .ToList();
        if (byFolder.Count == 0)
            return "";

        var files = byFolder.Sum(g => g.Count);
        var named = string.Join(", ", byFolder.Take(2).Select(g => g.Folder));
        var rest = byFolder.Count - 2;
        return $"{named}{(rest > 0 ? $" (+{rest} more)" : "")} — {files} file{(files == 1 ? "" : "s")}";
    }

    /// <summary>The directory holding this entry, root-relative and leading-slashed; the bare separator for a file
    /// sitting directly in the local root.</summary>
    private static string FolderOf(string path)
    {
        var cut = path.LastIndexOf('/');
        return cut <= 0 ? "/" : "/" + path[..cut];
    }

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
