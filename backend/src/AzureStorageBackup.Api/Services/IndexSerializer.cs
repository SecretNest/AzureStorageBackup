using System.Text;
using System.Text.Json.Nodes;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 信息记录文件与第二级索引的紧凑二进制序列化（M4 §13.4 收敛：为减小体积改用二进制）。
/// hash 存成 32 字节裸字节而非 72 字节 "sha256:"+hex 文本；枚举/时间/长度定宽编码。
/// 压缩/加密（7z）由独立编解码层负责。公开 API 与之前一致。
/// </summary>
public static class IndexSerializer
{
    public const int CurrentSchemaVersion = 1;
    // format 2：为分卷完整性核验增加分卷数（PackInfo.Volumes / StorageRef.Volumes，§7）。读取兼容 format 1（缺省 1 卷）。
    private const byte InfoFormat = 2;
    private const byte IndexFormat = 2;

    // ---- 信息记录文件 ----

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

        w.Write(info.Versions.Count);
        foreach (var v in info.Versions)
        {
            w.Write(v.Version);
            WriteDto(w, v.CreatedAt);
            w.Write(v.IndexBlob);
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
            w.Write(pack.Volumes); // format 2
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
        };

        var versionCount = r.ReadInt32();
        var versions = new List<BackupVersion>(versionCount);
        for (var i = 0; i < versionCount; i++)
        {
            versions.Add(new BackupVersion
            {
                Version = r.ReadInt32(),
                CreatedAt = ReadDto(r),
                IndexBlob = r.ReadString(),
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
                Volumes = format >= 2 ? r.ReadInt32() : 1,
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

    // ---- 第二级索引 ----

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
            WriteHash(w, e.FullHash);
            WriteNullableString(w, e.Target);

            if (e.Storage is { } s)
            {
                w.Write(true);
                w.Write((byte)(s.Kind == "pack" ? 1 : 0));
                w.Write(s.Ref);
                WriteNullableString(w, s.EntryName);
                w.Write(s.Volumes); // format 2
            }
            else
            {
                w.Write(false);
            }
        }

        w.Write(index.EmptyDirs.Count);
        foreach (var dir in index.EmptyDirs)
            w.Write(dir);

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
            var fullHash = ReadHash(r);
            var target = ReadNullableString(r);

            StorageRef? storage = null;
            if (r.ReadBoolean())
            {
                storage = new StorageRef
                {
                    Kind = r.ReadByte() == 1 ? "pack" : "blob",
                    Ref = r.ReadString(),
                    EntryName = ReadNullableString(r),
                    Volumes = format >= 2 ? r.ReadInt32() : 1,
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
                FullHash = fullHash,
                Target = target,
                Storage = storage,
            });
        }

        var dirCount = r.ReadInt32();
        var emptyDirs = new List<string>(dirCount);
        for (var i = 0; i < dirCount; i++)
            emptyDirs.Add(r.ReadString());

        return new VersionIndex { Version = version, Entries = entries, EmptyDirs = emptyDirs };
    }

    // ---- 编码原语 ----

    private static void WriteNullableString(BinaryWriter w, string? value)
    {
        w.Write(value is not null);
        if (value is not null)
            w.Write(value);
    }

    private static string? ReadNullableString(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;

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

    // hash 编码：0=null；1=xxh128 的 16 字节裸字节；2=任意字符串（兜底）。
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
                catch (FormatException) { /* 非 hex，走兜底 */ }
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
