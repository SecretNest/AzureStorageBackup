using System.Text;
using System.Text.Json.Nodes;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Compact binary serialization of the info record file (M4 §13.4 convergence: switched to binary to shrink the size).
/// The second-level index no longer passes through here: it is written and read one entry at a time by
/// <see cref="IndexStreamWriter"/>/<see cref="IndexStreamReader"/>, which never hold a whole index in memory.
/// Hashes are stored as 32 raw bytes rather than 72 bytes of "sha256:"+hex text; enums/timestamps/lengths use fixed-width encoding.
/// Compression/encryption (7z) is the job of a separate codec layer. The public API is unchanged.
/// </summary>
public static class IndexSerializer
{
    public const int CurrentSchemaVersion = 1;
    // Not in production yet, so the format is free to evolve: includes the volume count (§7) + the key derivation salt for encrypted backups (keyed addressing). Always reads and writes the current fields.
    private const byte InfoFormat = 5;  // format 2: PackInfo.VolumeSizes (volume sizes, for the exists+size check); format 3: BackupVersion.StartedAt; format 4: PackInfo.StoreOnly; format 5: BackupVersion.IndexVolumes

    // ---- Info record file ----

    public static byte[] SerializeInfoFile(BackupInfoFile info)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        w.Write(InfoFormat);
        w.Write(info.SchemaVersion);

        var b = info.Backup;
        w.Write(b.Name);
        IndexEncoding.WriteNullableString(w, b.Description);
        IndexEncoding.WriteNullableString(w, b.SourceRootHint);
        w.Write(b.Encrypted);
        IndexEncoding.WriteDto(w, b.CreatedAt);
        IndexEncoding.WriteNullableString(w, b.Settings?.ToJsonString());
        WriteNullableBytes(w, b.KdfSalt);

        w.Write(info.Versions.Count);
        foreach (var v in info.Versions)
        {
            w.Write(v.Version);
            IndexEncoding.WriteDto(w, v.CreatedAt);
            IndexEncoding.WriteNullableDto(w, v.StartedAt); // info format 3
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
                IndexEncoding.WriteHash(w, m);
            w.Write(pack.OriginalBytes);
            w.Write(pack.DeadBytes);
            w.Write(pack.Volumes);
            IndexEncoding.WriteLongs(w, pack.VolumeSizes); // info format 2
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
            Description = IndexEncoding.ReadNullableString(r),
            SourceRootHint = IndexEncoding.ReadNullableString(r),
            Encrypted = r.ReadBoolean(),
            CreatedAt = IndexEncoding.ReadDto(r),
            Settings = IndexEncoding.ReadNullableString(r) is { } s ? JsonNode.Parse(s)!.AsObject() : null,
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
                CreatedAt = IndexEncoding.ReadDto(r),
                StartedAt = format >= 3 ? IndexEncoding.ReadNullableDto(r) : null, // format 3+
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
                members.Add(IndexEncoding.ReadHash(r)!);
            var originalBytes = r.ReadInt64();
            var deadBytes = r.ReadInt64();
            packs[id] = new PackInfo
            {
                Blob = blob,
                Members = members,
                OriginalBytes = originalBytes,
                DeadBytes = deadBytes,
                Volumes = r.ReadInt32(),
                VolumeSizes = format >= 2 ? IndexEncoding.ReadLongs(r) : [],
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

    // ---- Encoding primitives ----
    // Shared with IndexStreamWriter/IndexStreamReader via IndexEncoding (Services/IndexStreamWriter.cs) so there is
    // one copy of the wire format, not two that could drift apart. KdfSalt is info-file-only, so its
    // WriteNullableBytes/ReadNullableBytes pair lives here rather than there.

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
}
