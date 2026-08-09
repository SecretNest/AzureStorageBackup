using System.Text.Json;
using System.Text.Json.Nodes;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class IndexSerializerTests
{
    private static BackupInfoFile SampleInfo() => new()
    {
        Backup = new BackupMeta
        {
            Name = "photos",
            Description = "family photos",
            SourceRootHint = "/data/photos",
            Encrypted = true,
            CreatedAt = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero),
            Settings = new JsonObject { ["maxVersions"] = 100 },
        },
        Versions =
        [
            new BackupVersion
            {
                Version = 1,
                CreatedAt = new DateTimeOffset(2026, 7, 16, 12, 5, 0, TimeSpan.Zero),
                IndexBlob = "indexes/v1.bin.enc",
                Stats = new VersionStats(1200, 3_400_000_000, 12, 50_000_000),
            },
        ],
        Packs = new Dictionary<string, PackInfo>
        {
            ["p0001"] = new PackInfo
            {
                Blob = "packs/p0001.7z",
                Members = ["sha256:" + new string('a', 64), "sha256:" + new string('b', 64)],
                OriginalBytes = 900_000,
                DeadBytes = 0,
            },
        },
    };

    private static VersionIndex SampleIndex() => new()
    {
        Version = 1,
        Entries =
        [
            new IndexEntry
            {
                Path = "sub/a.txt", Kind = "file", Length = 123,
                Mtime = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero),
                Permissions = "0644",
                HeadHash = "sha256:" + new string('1', 64), FullHash = "sha256:" + new string('2', 64),
                Storage = new StorageRef { Kind = "blob", Ref = "data/sha256:" + new string('2', 64) },
            },
            new IndexEntry
            {
                Path = "sub/small.txt", Kind = "file", Length = 40,
                Mtime = new DateTimeOffset(2026, 7, 16, 10, 1, 0, TimeSpan.Zero),
                Permissions = "0644",
                HeadHash = "sha256:" + new string('3', 64), FullHash = "sha256:" + new string('4', 64),
                Storage = new StorageRef { Kind = "pack", Ref = "p0001", EntryName = "sub/small.txt" },
            },
        ],
        EmptyDirs = ["sub/empty1", "sub/empty2"],
    };

    [Fact]
    public void InfoFile_RoundTrips()
    {
        var back = IndexSerializer.DeserializeInfoFile(IndexSerializer.SerializeInfoFile(SampleInfo()));

        Assert.Equal(1, back.SchemaVersion);
        Assert.Equal("photos", back.Backup.Name);
        Assert.Equal("family photos", back.Backup.Description);
        Assert.Equal("/data/photos", back.Backup.SourceRootHint);
        Assert.True(back.Backup.Encrypted);
        Assert.Equal(SampleInfo().Backup.CreatedAt, back.Backup.CreatedAt);
        Assert.Equal(100, (int)back.Backup.Settings!["maxVersions"]!);

        var v = Assert.Single(back.Versions);
        Assert.Equal("indexes/v1.bin.enc", v.IndexBlob);
        Assert.Equal(3_400_000_000, v.Stats.Bytes);

        var pack = back.Packs["p0001"];
        Assert.Equal("packs/p0001.7z", pack.Blob);
        Assert.Equal("sha256:" + new string('a', 64), pack.Members[0]);
    }

    [Fact]
    public void Index_RoundTrips_With_Pack_And_Blob_Storage()
    {
        var back = IndexSerializer.DeserializeIndex(IndexSerializer.SerializeIndex(SampleIndex()));

        Assert.Equal(1, back.Version);
        Assert.Equal(["sub/empty1", "sub/empty2"], back.EmptyDirs);

        var a = back.Entries.Single(e => e.Path == "sub/a.txt");
        Assert.Equal("file", a.Kind);
        Assert.Equal(123, a.Length);
        Assert.Equal("0644", a.Permissions);
        Assert.Equal("sha256:" + new string('1', 64), a.HeadHash);
        Assert.Equal("blob", a.Storage!.Kind);
        Assert.Null(a.Storage.EntryName);

        var small = back.Entries.Single(e => e.Path == "sub/small.txt");
        Assert.Equal("pack", small.Storage!.Kind);
        Assert.Equal("p0001", small.Storage.Ref);
        Assert.Equal("sub/small.txt", small.Storage.EntryName);
    }

    [Fact]
    public void Mtime_And_Offset_RoundTrip_Exactly()
    {
        var index = SampleIndex() with
        {
            Entries = [SampleIndex().Entries[0] with { Mtime = new DateTimeOffset(2026, 3, 1, 8, 30, 15, TimeSpan.FromHours(-5)) }],
        };

        var back = IndexSerializer.DeserializeIndex(IndexSerializer.SerializeIndex(index));

        Assert.Equal(index.Entries[0].Mtime, back.Entries[0].Mtime);
    }

    [Fact]
    public void Null_Optional_Fields_RoundTrip_As_Null()
    {
        var index = new VersionIndex
        {
            Version = 1,
            Entries =
            [
                new IndexEntry
                {
                    Path = "a.txt", Kind = "file", Length = 1,
                    Mtime = DateTimeOffset.UnixEpoch, Permissions = "0644",
                    HeadHash = "sha256:" + new string('a', 64), FullHash = "sha256:" + new string('b', 64),
                    Storage = new StorageRef { Kind = "blob", Ref = "data/x" }, // EntryName null, Target null
                },
            ],
        };

        var e = Assert.Single(IndexSerializer.DeserializeIndex(IndexSerializer.SerializeIndex(index)).Entries);
        Assert.Null(e.Target);
        Assert.Null(e.Storage!.EntryName);
    }

    [Fact]
    public void Binary_Index_Is_More_Compact_Than_Json()
    {
        // Build an index with a fair number of entries and real 64-hex hashes.
        var entries = Enumerable.Range(0, 50).Select(i => new IndexEntry
        {
            Path = $"dir/file{i}.txt", Kind = "file", Length = i,
            Mtime = DateTimeOffset.UnixEpoch, Permissions = "0644",
            HeadHash = "xxh128:" + i.ToString("x2") + new string('0', 30),
            FullHash = "xxh128:" + i.ToString("x2") + new string('f', 30),
            Storage = new StorageRef { Kind = "blob", Ref = "data/xxh128:" + i.ToString("x2") + new string('f', 30) },
        }).ToList();
        var index = new VersionIndex { Version = 1, Entries = entries };

        var binary = IndexSerializer.SerializeIndex(index);
        var json = JsonSerializer.SerializeToUtf8Bytes(index);

        Assert.True(binary.Length < json.Length, $"binary {binary.Length} should be < json {json.Length}");
    }

    [Fact]
    public void Volume_Counts_RoundTrip()
    {
        var info = SampleInfo();
        info.Packs["p0001"] = info.Packs["p0001"] with { Volumes = 4 };
        var index = SampleIndex() with
        {
            Entries = [SampleIndex().Entries[0] with { Storage = SampleIndex().Entries[0].Storage! with { Volumes = 3 } }],
        };

        var backInfo = IndexSerializer.DeserializeInfoFile(IndexSerializer.SerializeInfoFile(info));
        var backIndex = IndexSerializer.DeserializeIndex(IndexSerializer.SerializeIndex(index));

        Assert.Equal(4, backInfo.Packs["p0001"].Volumes);          // Pack volume count (info file)
        Assert.Equal(3, backIndex.Entries[0].Storage!.Volumes);    // Single-file blob volume count (index)
    }

    [Fact]
    public void Defaults_To_One_Volume()
    {
        var backIndex = IndexSerializer.DeserializeIndex(IndexSerializer.SerializeIndex(SampleIndex()));
        Assert.Equal(1, backIndex.Entries[0].Storage!.Volumes); // Not set explicitly → 1 volume
    }

    [Fact]
    public void Volume_Sizes_And_Unrecoverable_Paths_RoundTrip()
    {
        var info = SampleInfo();
        info.Packs["p0001"] = info.Packs["p0001"] with { VolumeSizes = [100, 200, 50] };
        var index = SampleIndex() with
        {
            Entries = [SampleIndex().Entries[0] with { Storage = SampleIndex().Entries[0].Storage! with { VolumeSizes = [4096, 512] } }],
            UnrecoverablePaths = ["gone/a.txt", "changed/b.bin"],
        };

        var backInfo = IndexSerializer.DeserializeInfoFile(IndexSerializer.SerializeInfoFile(info));
        var backIndex = IndexSerializer.DeserializeIndex(IndexSerializer.SerializeIndex(index));

        Assert.Equal([100L, 200L, 50L], backInfo.Packs["p0001"].VolumeSizes);       // Pack volume sizes (info file)
        Assert.Equal([4096L, 512L], backIndex.Entries[0].Storage!.VolumeSizes);     // Single-file blob volume sizes (index)
        Assert.Equal(["gone/a.txt", "changed/b.bin"], backIndex.UnrecoverablePaths); // Unrecoverable markers
    }

    [Fact]
    public void New_Fields_Default_Empty()
    {
        var backIndex = IndexSerializer.DeserializeIndex(IndexSerializer.SerializeIndex(SampleIndex()));
        Assert.Empty(backIndex.Entries[0].Storage!.VolumeSizes);
        Assert.Empty(backIndex.UnrecoverablePaths);
    }

    [Fact]
    public void Deserialize_Rejects_Unsupported_SchemaVersion()
    {
        var future = IndexSerializer.SerializeInfoFile(SampleInfo() with { SchemaVersion = 999 });

        Assert.Throws<NotSupportedException>(() => IndexSerializer.DeserializeInfoFile(future));
    }

    [Fact]
    public void Version_StartedAt_RoundTrips()
    {
        var info = SampleInfo();
        var started = new DateTimeOffset(2026, 7, 16, 11, 40, 0, TimeSpan.Zero);
        info.Versions[0] = info.Versions[0] with { StartedAt = started };

        var back = IndexSerializer.DeserializeInfoFile(IndexSerializer.SerializeInfoFile(info));

        Assert.Equal(started, Assert.Single(back.Versions).StartedAt);
    }

    /// <summary>
    /// An info file written before the upgrade (format 2) has no start time in its version entries. It must read back as null — and
    /// it must not slip out of alignment: the pack table follows immediately after the version entries, and reading one byte too few or too many turns everything after it into garbage.
    /// </summary>
    [Fact]
    public void Legacy_Format2_Info_Reads_StartedAt_As_Null()
    {
        var back = IndexSerializer.DeserializeInfoFile(LegacyFormat2Info());

        var v = Assert.Single(back.Versions);
        Assert.Null(v.StartedAt);
        Assert.Equal(new DateTimeOffset(2026, 7, 16, 12, 5, 0, TimeSpan.Zero), v.CreatedAt);
        Assert.Equal("indexes/v1.bin.enc", v.IndexBlob);
        Assert.Equal(1200, v.Stats.Files);
        Assert.Equal("packs/p0001.7z", back.Packs["p0001"].Blob);  // The pack table was not read out of alignment
        Assert.Equal(900_000, back.Packs["p0001"].OriginalBytes);
    }

    // The byte layout of InfoFormat 2 (version entries end at stats, no StartedAt). Hand-written rather than calling the serializer:
    // the serializer only ever writes the current format, which proves nothing about backward compatibility.
    private static byte[] LegacyFormat2Info()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write((byte)2);               // InfoFormat
        w.Write(1);                     // SchemaVersion
        w.Write("photos");
        WriteLegacyNullableString(w, "family photos");
        WriteLegacyNullableString(w, "/data/photos");
        w.Write(true);                  // Encrypted
        WriteLegacyDto(w, new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));
        WriteLegacyNullableString(w, null);  // Settings
        w.Write(false);                 // KdfSalt = null

        w.Write(1);                     // Version count
        w.Write(1);                     // Version
        WriteLegacyDto(w, new DateTimeOffset(2026, 7, 16, 12, 5, 0, TimeSpan.Zero));  // CreatedAt
        w.Write("indexes/v1.bin.enc");
        w.Write(1200L);                 // Stats.Files
        w.Write(3_400_000_000L);        // Stats.Bytes
        w.Write(12L);                   // Stats.ChangedFiles
        w.Write(50_000_000L);           // Stats.ChangedBytes

        w.Write(1);                     // Pack count
        w.Write("p0001");
        w.Write("packs/p0001.7z");
        w.Write(0);                     // Member count
        w.Write(900_000L);              // OriginalBytes
        w.Write(0L);                    // DeadBytes
        w.Write(1);                     // Volumes
        w.Write(0);                     // VolumeSizes count (info format 2)

        w.Flush();
        return ms.ToArray();
    }

    private static void WriteLegacyNullableString(BinaryWriter w, string? value)
    {
        w.Write(value is not null);
        if (value is not null)
            w.Write(value);
    }

    private static void WriteLegacyDto(BinaryWriter w, DateTimeOffset value)
    {
        w.Write(value.UtcTicks);
        w.Write((short)value.Offset.TotalMinutes);
    }

    [Fact]
    public void Pack_StoreOnly_RoundTrips()
    {
        var info = SampleInfo();
        info.Packs["p0002"] = info.Packs["p0001"] with { Blob = "packs/p0002.7z", StoreOnly = true };

        var back = IndexSerializer.DeserializeInfoFile(IndexSerializer.SerializeInfoFile(info));

        Assert.False(back.Packs["p0001"].StoreOnly);
        Assert.True(back.Packs["p0002"].StoreOnly);
        // There is another pack entry right after it — if alignment had slipped, that one would not read back.
        Assert.Equal("packs/p0002.7z", back.Packs["p0002"].Blob);
        Assert.Equal(900_000, back.Packs["p0002"].OriginalBytes);
    }

    /// <summary>
    /// In an info file written before the upgrade (format 3), pack entries end at VolumeSizes with no compression mode. It must read
    /// back as false — which is exactly the historical behavior (back then every pack was compressed). format 3 is the **previous
    /// production format**, the one the backups in users' hands are written in, which makes this case matter more than the format 2 one.
    /// </summary>
    [Fact]
    public void Legacy_Format3_Info_Reads_Pack_StoreOnly_As_False()
    {
        var back = IndexSerializer.DeserializeInfoFile(LegacyFormat3Info());

        var v = Assert.Single(back.Versions);
        Assert.Equal(new DateTimeOffset(2026, 7, 16, 12, 30, 0, TimeSpan.Zero), v.StartedAt);

        // Two packs: only if the first one reads without slipping alignment does the second read back at all.
        Assert.False(back.Packs["p0001"].StoreOnly);
        Assert.False(back.Packs["p0002"].StoreOnly);
        Assert.Equal("packs/p0002.7z", back.Packs["p0002"].Blob);
        Assert.Equal([1_000L, 2_000L], back.Packs["p0001"].VolumeSizes);
        Assert.Equal(900_000, back.Packs["p0002"].OriginalBytes);
    }

    // The byte layout of InfoFormat 3 (pack entries end at VolumeSizes, no StoreOnly). Hand-written rather than calling the serializer:
    // the serializer only ever writes the current format, which proves nothing about backward compatibility.
    private static byte[] LegacyFormat3Info()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write((byte)3);               // InfoFormat
        w.Write(1);                     // SchemaVersion
        w.Write("photos");
        WriteLegacyNullableString(w, "family photos");
        WriteLegacyNullableString(w, "/data/photos");
        w.Write(true);                  // Encrypted
        WriteLegacyDto(w, new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero));
        WriteLegacyNullableString(w, null);  // Settings
        w.Write(false);                 // KdfSalt = null

        w.Write(1);                     // Version count
        w.Write(1);                     // Version
        WriteLegacyDto(w, new DateTimeOffset(2026, 7, 16, 12, 5, 0, TimeSpan.Zero));   // CreatedAt
        w.Write(true);                                                                 // StartedAt not null (format 3)
        WriteLegacyDto(w, new DateTimeOffset(2026, 7, 16, 12, 30, 0, TimeSpan.Zero));  // StartedAt
        w.Write("indexes/v1.bin.enc");
        w.Write(1200L);                 // Stats.Files
        w.Write(3_400_000_000L);        // Stats.Bytes
        w.Write(12L);                   // Stats.ChangedFiles
        w.Write(50_000_000L);           // Stats.ChangedBytes

        w.Write(2);                     // Pack count
        WriteLegacyFormat3Pack(w, "p0001", volumeSizes: [1_000L, 2_000L]);
        WriteLegacyFormat3Pack(w, "p0002", volumeSizes: []);

        w.Flush();
        return ms.ToArray();
    }

    private static void WriteLegacyFormat3Pack(BinaryWriter w, string id, long[] volumeSizes)
    {
        w.Write(id);
        w.Write($"packs/{id}.7z");
        w.Write(0);                     // Member count
        w.Write(900_000L);              // OriginalBytes
        w.Write(0L);                    // DeadBytes
        w.Write(Math.Max(1, volumeSizes.Length));  // Volumes
        w.Write(volumeSizes.Length);    // VolumeSizes count (info format 2+)
        foreach (var size in volumeSizes)
            w.Write(size);
    }
}
