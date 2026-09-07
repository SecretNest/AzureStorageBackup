using System.Buffers.Binary;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// On-disk home of the retired local version-index cache: <c>{root}/{accountId}/{container}/{version}.idx</c>.
///
/// <para>
/// These bytes used to be a column in SQLite, one row holding a whole serialized index — on the order of 100 MB for a
/// backup of half a million files. SQLite permits exactly one writer at a time (WAL changes nothing about that; it only
/// stops readers and the writer from shutting each other out), so committing an index took the database's single write
/// lock for as long as the write took, which on a loaded disk is tens of seconds. Everything else that wanted to write
/// waited: the scheduler's log trim, and a config edit whose Save button sat greyed out until the command timed out.
/// A file has no such lock, and nothing ever joined against this data — it is a cache of opaque bytes keyed by
/// (account, container, version), which is a filename, not a table.
/// </para>
/// <para>
/// It lives next to the database file rather than under <c>Backup:TempPath</c>, for the same reason the journal does:
/// <c>/temp</c> is the directory the deployment instructions call safe to discard, and discarding this one means
/// re-downloading every index from the cloud. Following the database puts it on a volume that was already being
/// persisted, with no second environment variable to get right.
/// </para>
/// <para>
/// Layout of one file: a 24-byte header, then a serialized index in <see cref="IndexStreamWriter"/> format
/// verbatim. The header is what makes a stale entry cheap to reject — identity is checked by reading 24 bytes
/// rather than by loading a 100 MB blob first, which is what the row-based version had to do on every single read.
/// </para>
/// <para>
/// Nothing writes these files any more: version indexes live in the SQLite catalog, and this class survives only so
/// that a file an older build left behind is still read once — by <see cref="VersionCatalogs"/>'s lazy migration,
/// which deletes it on the way past — and so that retiring a version or a container takes its file with it.
/// </para>
/// </summary>
public sealed class VersionIndexFileStore(string rootDir)
{
    private static readonly byte[] Magic = "ASBI"u8.ToArray();
    private const int Format = 1;

    /// <summary>magic(4) + format(4) + identityTicks(8) + bodyLength(8).</summary>
    private const int HeaderBytes = 24;

    /// <summary>
    /// The same flattening the journal store applies, and for the same reason: container names are not supposed to
    /// contain separators, but that is upstream's promise, not ours, and <see cref="RemoveForContainer"/> is a
    /// recursive delete — one <c>..</c> that survives into a path segment deletes a sibling container's cache.
    /// </summary>
    private static string Safe(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0 || chars[i] is '/' or '\\')
                chars[i] = '_';
        var flat = new string(chars);
        return flat.Length > 0 && flat.All(c => c == '.') ? new string('_', flat.Length) : flat;
    }

    private string DirFor(int accountId, string container)
        => Path.Combine(rootDir, accountId.ToString(), Safe(container));

    public string PathFor(int accountId, string container, int version)
        => Path.Combine(DirFor(accountId, container), version + ".idx");

    /// <summary>
    /// The one way in: validates the 24-byte header (magic, format, identity, body length matching the file's real
    /// length) and returns the open file positioned right after it — what <see cref="VersionCatalogs"/> hands an
    /// <see cref="IndexStreamReader"/> without holding a possibly-hundreds-of-MB index in memory just to move it into
    /// the catalog. Null on any mismatch or a missing file: this is a cache, and a caller's answer to a miss is to
    /// fetch from the cloud, which is always correct, merely slower. The stream is disposed before returning null so
    /// a caller never has to guess whether it owns a handle.
    /// </summary>
    public async Task<Stream?> OpenBodyAsync(
        int accountId, string container, int version, long identityTicks, CancellationToken ct = default)
    {
        var path = PathFor(accountId, container, version);
        FileStream stream;
        try
        {
            stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        try
        {
            var header = new byte[HeaderBytes];
            if (!await ReadExactlyAsync(stream, header, ct))
            {
                await stream.DisposeAsync();
                return null;
            }

            if (!header.AsSpan(0, 4).SequenceEqual(Magic)
                || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4)) != Format
                || BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8, 8)) != identityTicks)
            {
                await stream.DisposeAsync();
                return null;
            }

            var bodyLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16, 8));
            if (bodyLength < 0 || bodyLength > int.MaxValue || stream.Length - HeaderBytes != bodyLength)
            {
                await stream.DisposeAsync();
                return null;
            }

            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    /// <summary>Drop one version's entry (the retention policy retiring it). Absent is the desired outcome, not an error.</summary>
    public void Remove(int accountId, string container, int version)
    {
        try { File.Delete(PathFor(accountId, container, version)); }
        catch (DirectoryNotFoundException) { /* already gone */ }
    }

    /// <summary>
    /// Drop every entry for one backup (its config being deleted). Rebuilding a backup on the same account+container
    /// must not find a cached index from the old identity sitting there.
    /// </summary>
    public void RemoveForContainer(int accountId, string container)
    {
        try { Directory.Delete(DirFor(accountId, container), recursive: true); }
        catch (DirectoryNotFoundException) { /* already gone */ }
    }

    /// <summary>Fills the buffer completely, or reports that the file was shorter than it claimed to be.</summary>
    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0)
                return false;
            offset += read;
        }
        return true;
    }
}
