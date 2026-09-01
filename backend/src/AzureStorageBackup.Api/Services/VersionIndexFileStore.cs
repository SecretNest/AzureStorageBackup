using System.Buffers.Binary;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// On-disk home of the local version-index cache: <c>{root}/{accountId}/{container}/{version}.idx</c>.
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
/// Layout of one file: a 24-byte header, then <see cref="IndexSerializer.SerializeIndex"/>'s output verbatim.
/// The header is what makes a stale entry cheap to reject — identity is checked by reading 24 bytes rather than by
/// loading a 100 MB blob first, which is what the row-based version had to do on every single read.
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
    /// The cached bytes, or null for a miss. Absent, written by an older format, belonging to a different backup
    /// identity, or truncated all count as a miss — this is a cache, and the caller's answer to a miss is to fetch
    /// from the cloud, which is always correct, merely slower.
    /// </summary>
    public async Task<byte[]?> ReadAsync(
        int accountId, string container, int version, long identityTicks, CancellationToken ct = default)
    {
        var path = PathFor(accountId, container, version);
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);

            var header = new byte[HeaderBytes];
            if (!await ReadExactlyAsync(stream, header, ct))
                return null;

            if (!header.AsSpan(0, 4).SequenceEqual(Magic)
                || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4)) != Format
                || BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8, 8)) != identityTicks)
                return null;

            // The recorded length against the real one: a file cut short by a power failure between the write and the
            // rename would otherwise deserialize into a plausible-looking, wrong index rather than a clean miss.
            var bodyLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16, 8));
            if (bodyLength < 0 || bodyLength > int.MaxValue || stream.Length - HeaderBytes != bodyLength)
                return null;

            var body = new byte[bodyLength];
            return await ReadExactlyAsync(stream, body, ct) ? body : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Replace this version's entry, atomically. Written to a temporary name in the same directory and renamed over
    /// the target: a rename within one filesystem either happens or does not, so a reader never meets a half-written
    /// index and an interrupted write leaves the previous entry intact.
    /// <para>
    /// Failures are **not** swallowed. This is a cache, so in principle a failed write costs only a slower next read —
    /// but a data directory that cannot be written to is worth hearing about at the moment it happens, and the row
    /// this replaced propagated its failures too. Changing that silently is not this change's business.
    /// </para>
    /// </summary>
    public async Task WriteAsync(
        int accountId, string container, int version, long identityTicks, byte[] body, CancellationToken ct = default)
    {
        var path = PathFor(accountId, container, version);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // Same directory as the target, so the rename cannot cross a filesystem boundary and degrade into copy+delete.
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var stream = new FileStream(
                temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true))
            {
                var header = new byte[HeaderBytes];
                Magic.CopyTo(header.AsSpan(0, 4));
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), Format);
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8, 8), identityTicks);
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16, 8), body.LongLength);

                await stream.WriteAsync(header, ct);
                await stream.WriteAsync(body, ct);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Leaving the temporary file behind would accumulate one per failed write, forever, in a directory nothing
            // else ever prunes.
            try { File.Delete(temp); } catch { /* nothing further to try */ }
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
