using System.Text;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Reads a second-level version index (§3.2) from a <see cref="Stream"/> one entry at a time, via the shared
/// <see cref="IndexEncoding"/> primitives and without materializing the whole <see cref="VersionIndex"/> — the
/// SQLite catalog wants to insert each entry as it is read rather than hold every entry of a possibly million-file
/// index in memory at once.
/// </summary>
public sealed class IndexStreamReader : IDisposable
{
    private readonly BinaryReader _r;
    private int _entriesRead;
    private bool _entriesExhausted;
    private bool _emptyDirsRead;

    public int Format { get; }
    public int Version { get; }
    public int EntryCount { get; }

    /// <summary>Reads the header eagerly (format/version/entryCount) so callers can size buffers before pulling entries.</summary>
    public IndexStreamReader(Stream input)
    {
        _r = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);

        Format = _r.ReadByte();
        if (Format > IndexStreamWriter.IndexFormat)
            throw new NotSupportedException($"Index format {Format} is newer than supported {IndexStreamWriter.IndexFormat}.");

        Version = _r.ReadInt32();
        EntryCount = _r.ReadInt32();
    }

    /// <summary>Yields exactly <see cref="EntryCount"/> entries, one per <c>MoveNext</c>. Call exactly once, and fully enumerate it
    /// before reading empty dirs / unrecoverable paths — the stream position after the last entry is where those start.</summary>
    public IEnumerable<IndexEntry> Entries()
    {
        for (var i = 0; i < EntryCount; i++)
        {
            var path = _r.ReadString();
            var kind = _r.ReadByte() == 1 ? "symlink" : "file";
            var length = _r.ReadInt64();
            var mtime = IndexEncoding.ReadDto(_r);
            var permissions = _r.ReadString();
            var headHash = IndexEncoding.ReadHash(_r);
            var tailHash = Format >= 2 ? IndexEncoding.ReadHash(_r) : null;
            var fullHash = IndexEncoding.ReadHash(_r);
            var target = IndexEncoding.ReadNullableString(_r);
            var unreadableAt = Format >= 4 ? IndexEncoding.ReadNullableDto(_r) : null;

            StorageRef? storage = null;
            if (_r.ReadBoolean())
            {
                storage = new StorageRef
                {
                    Kind = _r.ReadByte() == 1 ? "pack" : "blob",
                    Ref = _r.ReadString(),
                    EntryName = IndexEncoding.ReadNullableString(_r),
                    Volumes = _r.ReadInt32(),
                    Raw = _r.ReadBoolean(),
                    VolumeSizes = Format >= 3 ? IndexEncoding.ReadLongs(_r) : [],
                };
            }

            _entriesRead++;
            yield return new IndexEntry
            {
                Path = path,
                Kind = kind,
                Length = length,
                Mtime = mtime,
                Permissions = permissions,
                HeadHash = headHash,
                TailHash = tailHash,
                FullHash = fullHash,
                Target = target,
                UnreadableAt = unreadableAt,
                Storage = storage,
            };
        }

        _entriesExhausted = true;
    }

    /// <summary>Must be called after <see cref="Entries"/> has been fully enumerated; the empty-dirs section immediately follows the last entry in the stream.</summary>
    public IReadOnlyList<string> ReadEmptyDirs()
    {
        if (!_entriesExhausted)
            throw new InvalidOperationException($"Entries() was not fully enumerated ({_entriesRead}/{EntryCount} read).");

        var dirCount = _r.ReadInt32();
        var dirs = new List<string>(dirCount);
        for (var i = 0; i < dirCount; i++)
            dirs.Add(_r.ReadString());

        _emptyDirsRead = true;
        return dirs;
    }

    /// <summary>Must be called after <see cref="ReadEmptyDirs"/>. Returns an empty list without reading when the stream predates format 3 (no unrecoverable-paths section was ever written).</summary>
    public IReadOnlyList<string> ReadUnrecoverable()
    {
        if (!_emptyDirsRead)
            throw new InvalidOperationException("ReadEmptyDirs() must be called before ReadUnrecoverable().");

        if (Format < 3)
            return [];

        var count = _r.ReadInt32();
        var paths = new List<string>(count);
        for (var i = 0; i < count; i++)
            paths.Add(_r.ReadString());
        return paths;
    }

    public void Dispose() => _r.Dispose();
}
