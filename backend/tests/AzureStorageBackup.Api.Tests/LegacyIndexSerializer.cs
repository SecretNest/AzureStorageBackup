using System.Text;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// <c>LegacyIndexSerializer.SerializeIndex</c>/<c>DeserializeIndex</c>, verbatim, after the product retired them.
/// <para>
/// Production writes and reads a version index one entry at a time now (<see cref="IndexStreamWriter"/> /
/// <see cref="IndexStreamReader"/>) and never holds a whole one in memory, so the whole-index pair had no callers
/// left. Tests still want them for two jobs the streaming pair is the wrong shape for: building a fixture out of a
/// hand-written <see cref="VersionIndex"/> in one line, and standing as an independent oracle the streaming pair is
/// checked against — an oracle that shared the streaming implementation would agree with it about a bug.
/// </para>
/// <para>
/// It reads and writes the same bytes as <see cref="IndexStreamWriter.IndexFormat"/> 4, over the same
/// <c>IndexEncoding</c> primitives the product still owns, so this copy cannot drift into a private dialect: a
/// change to the wire format that forgot this file shows up as a failing round-trip, not as a test that quietly
/// keeps passing against a format nothing else speaks.
/// </para>
/// </summary>
internal static class LegacyIndexSerializer
{
    private const byte IndexFormat = 4;  // format 2: TailHash; format 3: StorageRef.VolumeSizes + VersionIndex.UnrecoverablePaths; format 4: IndexEntry.UnreadableAt

    public static byte[] SerializeIndex(VersionIndex index)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        w.Write(IndexFormat);
        w.Write(index.Version);

        w.Write(index.Entries.Count);
        foreach (var e in index.Entries)
        {
            w.Write(e.Path);
            w.Write((byte)(e.Kind == "symlink" ? 1 : 0));
            w.Write(e.Length);
            IndexEncoding.WriteDto(w, e.Mtime);
            w.Write(e.Permissions);
            IndexEncoding.WriteHash(w, e.HeadHash);
            IndexEncoding.WriteHash(w, e.TailHash); // format 2
            IndexEncoding.WriteHash(w, e.FullHash);
            IndexEncoding.WriteNullableString(w, e.Target);
            IndexEncoding.WriteNullableDto(w, e.UnreadableAt); // index format 4

            if (e.Storage is { } s)
            {
                w.Write(true);
                w.Write((byte)(s.Kind == "pack" ? 1 : 0));
                w.Write(s.Ref);
                IndexEncoding.WriteNullableString(w, s.EntryName);
                w.Write(s.Volumes);
                w.Write(s.Raw);
                IndexEncoding.WriteLongs(w, s.VolumeSizes); // index format 3
            }
            else
            {
                w.Write(false);
            }
        }

        w.Write(index.EmptyDirs.Count);
        foreach (var dir in index.EmptyDirs)
            w.Write(dir);

        w.Write(index.UnrecoverablePaths.Count); // index format 3
        foreach (var p in index.UnrecoverablePaths)
            w.Write(p);

        w.Flush();
        return ms.ToArray();
    }

    public static VersionIndex DeserializeIndex(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var r = new BinaryReader(ms, Encoding.UTF8);

        var format = r.ReadByte();
        if (format > IndexFormat)
            throw new NotSupportedException($"Index format {format} is newer than supported {IndexFormat}.");

        var version = r.ReadInt32();

        var entryCount = r.ReadInt32();
        var entries = new List<IndexEntry>(entryCount);
        for (var i = 0; i < entryCount; i++)
        {
            var path = r.ReadString();
            var kind = r.ReadByte() == 1 ? "symlink" : "file";
            var length = r.ReadInt64();
            var mtime = IndexEncoding.ReadDto(r);
            var permissions = r.ReadString();
            var headHash = IndexEncoding.ReadHash(r);
            var tailHash = format >= 2 ? IndexEncoding.ReadHash(r) : null; // format 2+
            var fullHash = IndexEncoding.ReadHash(r);
            var target = IndexEncoding.ReadNullableString(r);
            var unreadableAt = format >= 4 ? IndexEncoding.ReadNullableDto(r) : null; // format 4+

            StorageRef? storage = null;
            if (r.ReadBoolean())
            {
                storage = new StorageRef
                {
                    Kind = r.ReadByte() == 1 ? "pack" : "blob",
                    Ref = r.ReadString(),
                    EntryName = IndexEncoding.ReadNullableString(r),
                    Volumes = r.ReadInt32(),
                    Raw = r.ReadBoolean(),
                    VolumeSizes = format >= 3 ? IndexEncoding.ReadLongs(r) : [],
                };
            }

            entries.Add(new IndexEntry
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
            });
        }

        var dirCount = r.ReadInt32();
        var emptyDirs = new List<string>(dirCount);
        for (var i = 0; i < dirCount; i++)
            emptyDirs.Add(r.ReadString());

        var unrecoverable = new List<string>();
        if (format >= 3)
        {
            var uCount = r.ReadInt32();
            for (var i = 0; i < uCount; i++)
                unrecoverable.Add(r.ReadString());
        }

        return new VersionIndex
        {
            Version = version,
            Entries = entries,
            EmptyDirs = emptyDirs,
            UnrecoverablePaths = unrecoverable,
        };
    }
}
