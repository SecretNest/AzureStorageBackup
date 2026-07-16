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
        // 构造一个较多条目、真实 64-hex hash 的索引。
        var entries = Enumerable.Range(0, 50).Select(i => new IndexEntry
        {
            Path = $"dir/file{i}.txt", Kind = "file", Length = i,
            Mtime = DateTimeOffset.UnixEpoch, Permissions = "0644",
            HeadHash = "sha256:" + i.ToString("x2") + new string('0', 62),
            FullHash = "sha256:" + i.ToString("x2") + new string('f', 62),
            Storage = new StorageRef { Kind = "blob", Ref = "data/sha256:" + i.ToString("x2") + new string('f', 62) },
        }).ToList();
        var index = new VersionIndex { Version = 1, Entries = entries };

        var binary = IndexSerializer.SerializeIndex(index);
        var json = JsonSerializer.SerializeToUtf8Bytes(index);

        Assert.True(binary.Length < json.Length, $"binary {binary.Length} should be < json {json.Length}");
    }

    [Fact]
    public void Deserialize_Rejects_Unsupported_SchemaVersion()
    {
        var future = IndexSerializer.SerializeInfoFile(SampleInfo() with { SchemaVersion = 999 });

        Assert.Throws<NotSupportedException>(() => IndexSerializer.DeserializeInfoFile(future));
    }
}
