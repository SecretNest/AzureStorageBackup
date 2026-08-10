using System.Text;
using System.Text.Json.Nodes;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Compact binary serialization of the info record file and the second-level index (M4 §13.4 convergence: switched to binary to shrink the size).
/// Hashes are stored as 32 raw bytes rather than 72 bytes of "sha256:"+hex text; enums/timestamps/lengths use fixed-width encoding.
/// Compression/encryption (7z) is the job of a separate codec layer. The public API is unchanged.
/// </summary>
public static class IndexSerializer
{
    public const int CurrentSchemaVersion = 1;
    // Not in production yet, so the format is free to evolve: includes the volume count (§7) + the key derivation salt for encrypted backups (keyed addressing). Always reads and writes the current fields.
    private const byte InfoFormat = 5;  // format 2: PackInfo.VolumeSizes (volume sizes, for the exists+size check); format 3: BackupVersion.StartedAt; format 4: PackInfo.StoreOnly; format 5: BackupVersion.IndexVolumes
    private const byte IndexFormat = 4;  // format 2: TailHash; format 3: StorageRef.VolumeSizes + VersionIndex.UnrecoverablePaths; format 4: IndexEntry.UnreadableAt

    // ---- Info record file ----

    public static byte[] SerializeInfoFile(BackupInfoFile info)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        w.Write(InfoFormat);
        w.Write(info.SchemaVersion);

        var b = info.Backup;
        w.Write(b.Name);
        WriteNullableString(w, b.Description);
        WriteNullableString(w, b.SourceRootHint);
        w.Write(b.Encrypted);
        WriteDto(w, b.CreatedAt);
        WriteNullableString(w, b.Settings?.ToJsonString());
        WriteNullableBytes(w, b.KdfSalt);

        w.Write(info.Versions.Count);
        foreach (var v in info.Versions)
        {
            w.Write(v.Version);
            WriteDto(w, v.CreatedAt);
            WriteNullableDto(w, v.StartedAt); // info format 3
            w.Write(v.IndexBlob);
            w.Write(v.IndexVolumes); // info format 5
            w.Write(v.Stats.Files);
            w.Write(v.Stats.Bytes);
            w.Write(v.Stats.ChangedFiles);
            w.Write(v.Stats.ChangedBytes);
        }

        w.Write(info.Packs.Count);
        foreach (var (id, pack) in info.Packs)
        {
            w.Write(id);
            w.Write(pack.Blob);
            w.Write(pack.Members.Count);
            foreach (var m in pack.Members)
                WriteHash(w, m);
            w.Write(pack.OriginalBytes);
            w.Write(pack.DeadBytes);
            w.Write(pack.Volumes);
            WriteLongs(w, pack.VolumeSizes); // info format 2
            w.Write(pack.StoreOnly); // info format 4
        }

        w.Flush();
        return ms.ToArray();
    }

    public static BackupInfoFile DeserializeInfoFile(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var r = new BinaryReader(ms, Encoding.UTF8);

        var format = r.ReadByte();
        if (format > InfoFormat)
            throw new NotSupportedException($"Info file format {format} is newer than supported {InfoFormat}.");

        var schemaVersion = r.ReadInt32();
        if (schemaVersion > CurrentSchemaVersion)
            throw new NotSupportedException(
                $"Info file schemaVersion {schemaVersion} is newer than supported {CurrentSchemaVersion}.");

        var meta = new BackupMeta
        {
            Name = r.ReadString(),
            Description = ReadNullableString(r),
            SourceRootHint = ReadNullableString(r),
            Encrypted = r.ReadBoolean(),
            CreatedAt = ReadDto(r),
            Settings = ReadNullableString(r) is { } s ? JsonNode.Parse(s)!.AsObject() : null,
            KdfSalt = ReadNullableBytes(r),
        };

        var versionCount = r.ReadInt32();
        var versions = new List<BackupVersion>(versionCount);
        for (var i = 0; i < versionCount; i++)
        {
            versions.Add(new BackupVersion
            {
                // Initializers evaluate in written order = the field order in the stream, matching the write side one for one; don't reorder.
                Version = r.ReadInt32(),
                CreatedAt = ReadDto(r),
                StartedAt = format >= 3 ? ReadNullableDto(r) : null, // format 3+
                IndexBlob = r.ReadString(),
                // format 5+. Anything written earlier is a single blob by definition, so 1 is the historical
                // behaviour rather than a guess — an old info file keeps reading exactly as it always did.
                IndexVolumes = format >= 5 ? r.ReadInt32() : 1,
                Stats = new VersionStats(r.ReadInt64(), r.ReadInt64(), r.ReadInt64(), r.ReadInt64()),
            });
        }

        var packCount = r.ReadInt32();
        var packs = new Dictionary<string, PackInfo>(packCount);
        for (var i = 0; i < packCount; i++)
        {
            var id = r.ReadString();
            var blob = r.ReadString();
            var memberCount = r.ReadInt32();
            var members = new List<string>(memberCount);
            for (var m = 0; m < memberCount; m++)
                members.Add(ReadHash(r)!);
            var originalBytes = r.ReadInt64();
            var deadBytes = r.ReadInt64();
            packs[id] = new PackInfo
            {
                Blob = blob,
                Members = members,
                OriginalBytes = originalBytes,
                DeadBytes = deadBytes,
                Volumes = r.ReadInt32(),
                VolumeSizes = format >= 2 ? ReadLongs(r) : [],
                // format 4+. Packs in older info files were all compressed, so reading back false is exactly the historical behavior.
                StoreOnly = format >= 4 && r.ReadBoolean(),
            };
        }

        return new BackupInfoFile
        {
            SchemaVersion = schemaVersion,
            Backup = meta,
            Versions = versions,
            Packs = packs,
        };
    }

    // ---- Second-level index ----

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
            WriteDto(w, e.Mtime);
            w.Write(e.Permissions);
            WriteHash(w, e.HeadHash);
            WriteHash(w, e.TailHash); // format 2
            WriteHash(w, e.FullHash);
            WriteNullableString(w, e.Target);
            WriteNullableDto(w, e.UnreadableAt); // index format 4

            if (e.Storage is { } s)
            {
                w.Write(true);
                w.Write((byte)(s.Kind == "pack" ? 1 : 0));
                w.Write(s.Ref);
                WriteNullableString(w, s.EntryName);
                w.Write(s.Volumes);
                w.Write(s.Raw);
                WriteLongs(w, s.VolumeSizes); // index format 3
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
            var mtime = ReadDto(r);
            var permissions = r.ReadString();
            var headHash = ReadHash(r);
            var tailHash = format >= 2 ? ReadHash(r) : null; // format 2+
            var fullHash = ReadHash(r);
            var target = ReadNullableString(r);
            var unreadableAt = format >= 4 ? ReadNullableDto(r) : null; // format 4+

            StorageRef? storage = null;
            if (r.ReadBoolean())
            {
                storage = new StorageRef
                {
                    Kind = r.ReadByte() == 1 ? "pack" : "blob",
                    Ref = r.ReadString(),
                    EntryName = ReadNullableString(r),
                    Volumes = r.ReadInt32(),
                    Raw = r.ReadBoolean(),
                    VolumeSizes = format >= 3 ? ReadLongs(r) : [],
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

    // ---- Encoding primitives ----

    private static void WriteNullableString(BinaryWriter w, string? value)
    {
        w.Write(value is not null);
        if (value is not null)
            w.Write(value);
    }

    private static string? ReadNullableString(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;

    private static void WriteLongs(BinaryWriter w, IReadOnlyList<long> values)
    {
        w.Write(values.Count);
        foreach (var v in values)
            w.Write(v);
    }

    private static List<long> ReadLongs(BinaryReader r)
    {
        var count = r.ReadInt32();
        var list = new List<long>(count);
        for (var i = 0; i < count; i++)
            list.Add(r.ReadInt64());
        return list;
    }

    private static void WriteNullableBytes(BinaryWriter w, byte[]? value)
    {
        w.Write(value is not null);
        if (value is not null)
        {
            w.Write(value.Length);
            w.Write(value);
        }
    }

    private static byte[]? ReadNullableBytes(BinaryReader r) => r.ReadBoolean() ? r.ReadBytes(r.ReadInt32()) : null;

    private static void WriteDto(BinaryWriter w, DateTimeOffset value)
    {
        w.Write(value.UtcTicks);
        w.Write((short)value.Offset.TotalMinutes);
    }

    private static DateTimeOffset ReadDto(BinaryReader r)
    {
        var utcTicks = r.ReadInt64();
        var offset = TimeSpan.FromMinutes(r.ReadInt16());
        return new DateTimeOffset(utcTicks + offset.Ticks, offset);
    }

    private static void WriteNullableDto(BinaryWriter w, DateTimeOffset? value)
    {
        w.Write(value.HasValue);
        if (value.HasValue)
            WriteDto(w, value.Value);
    }

    private static DateTimeOffset? ReadNullableDto(BinaryReader r) => r.ReadBoolean() ? ReadDto(r) : null;

    // Hash encoding: 0 = null; 1 = the 16 raw bytes of an xxh128; 2 = an arbitrary string (fallback).
    private const string HashPrefix = "xxh128:";

    private static void WriteHash(BinaryWriter w, string? hash)
    {
        if (hash is null)
        {
            w.Write((byte)0);
            return;
        }

        byte[]? raw = null;
        if (hash.StartsWith(HashPrefix, StringComparison.Ordinal))
        {
            var hex = hash[HashPrefix.Length..];
            if (hex.Length == 32)
            {
                try { raw = Convert.FromHexString(hex); }
                catch (FormatException) { /* Not hex, take the fallback */ }
            }
        }

        if (raw is { Length: 16 })
        {
            w.Write((byte)1);
            w.Write(raw);
        }
        else
        {
            w.Write((byte)2);
            w.Write(hash);
        }
    }

    private static string? ReadHash(BinaryReader r) => r.ReadByte() switch
    {
        0 => null,
        1 => HashPrefix + Convert.ToHexString(r.ReadBytes(16)).ToLowerInvariant(),
        _ => r.ReadString(),
    };
}
