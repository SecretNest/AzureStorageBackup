using System.Text;
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
                IndexBlob = "indexes/v1.json.enc",
                Stats = new VersionStats(1200, 3_400_000_000, 12, 50_000_000),
            },
        ],
        Packs = new Dictionary<string, PackInfo>
        {
            ["p0001"] = new PackInfo
            {
                Blob = "packs/p0001.7z",
                Members = ["sha_a", "sha_b"],
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
                HeadHash = "sha256:aaa", FullHash = "sha256:bbb",
                Storage = new StorageRef { Kind = "blob", Ref = "data/sha256:bbb" },
            },
            new IndexEntry
            {
                Path = "sub/small.txt", Kind = "file", Length = 40,
                Mtime = new DateTimeOffset(2026, 7, 16, 10, 1, 0, TimeSpan.Zero),
                Permissions = "0644",
                HeadHash = "sha256:ccc", FullHash = "sha256:ddd",
                Storage = new StorageRef { Kind = "pack", Ref = "p0001", EntryName = "sub/small.txt" },
            },
        ],
        EmptyDirs = ["sub/empty1", "sub/empty2"],
    };

    [Fact]
    public void InfoFile_RoundTrips()
    {
        var bytes = IndexSerializer.SerializeInfoFile(SampleInfo());
        var back = IndexSerializer.DeserializeInfoFile(bytes);

        Assert.Equal(1, back.SchemaVersion);
        Assert.Equal("photos", back.Backup.Name);
        Assert.Equal("family photos", back.Backup.Description);
        Assert.Equal("/data/photos", back.Backup.SourceRootHint);
        Assert.True(back.Backup.Encrypted);
        Assert.Equal(SampleInfo().Backup.CreatedAt, back.Backup.CreatedAt);
        Assert.Equal(100, (int)back.Backup.Settings!["maxVersions"]!);

        var v = Assert.Single(back.Versions);
        Assert.Equal(1, v.Version);
        Assert.Equal("indexes/v1.json.enc", v.IndexBlob);
        Assert.Equal(1200, v.Stats.Files);
        Assert.Equal(3_400_000_000, v.Stats.Bytes);

        var pack = back.Packs["p0001"];
        Assert.Equal("packs/p0001.7z", pack.Blob);
        Assert.Equal(["sha_a", "sha_b"], pack.Members);
    }

    [Fact]
    public void Index_RoundTrips_With_Pack_And_Blob_Storage()
    {
        var bytes = IndexSerializer.SerializeIndex(SampleIndex());
        var back = IndexSerializer.DeserializeIndex(bytes);

        Assert.Equal(1, back.Version);
        Assert.Equal(["sub/empty1", "sub/empty2"], back.EmptyDirs);

        var a = back.Entries.Single(e => e.Path == "sub/a.txt");
        Assert.Equal("file", a.Kind);
        Assert.Equal(123, a.Length);
        Assert.Equal("0644", a.Permissions);
        Assert.Equal("sha256:aaa", a.HeadHash);
        Assert.Equal("blob", a.Storage!.Kind);
        Assert.Equal("data/sha256:bbb", a.Storage.Ref);
        Assert.Null(a.Storage.EntryName);

        var small = back.Entries.Single(e => e.Path == "sub/small.txt");
        Assert.Equal("pack", small.Storage!.Kind);
        Assert.Equal("p0001", small.Storage.Ref);
        Assert.Equal("sub/small.txt", small.Storage.EntryName);
    }

    [Fact]
    public void InfoFile_Json_Uses_Expected_Wire_Keys()
    {
        var json = Encoding.UTF8.GetString(IndexSerializer.SerializeInfoFile(SampleInfo()));

        Assert.Contains("\"schemaVersion\"", json);
        Assert.Contains("\"sourceRootHint\"", json);
        Assert.Contains("\"changedBytes\"", json);
        Assert.DoesNotContain("SchemaVersion", json); // camelCase, not PascalCase
    }

    [Fact]
    public void Index_Json_Uses_Expected_Wire_Keys()
    {
        var json = Encoding.UTF8.GetString(IndexSerializer.SerializeIndex(SampleIndex()));

        Assert.Contains("\"headHash\"", json);
        Assert.Contains("\"fullHash\"", json);
        Assert.Contains("\"emptyDirs\"", json);
        Assert.Contains("\"storage\"", json);
        Assert.Contains("\"ref\"", json);
        Assert.Contains("\"entryName\"", json);
    }

    [Fact]
    public void Null_Optional_Fields_Are_Omitted()
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
                    HeadHash = "sha256:x", FullHash = "sha256:y",
                    Storage = new StorageRef { Kind = "blob", Ref = "data/sha256:y" },
                },
            ],
        };

        var json = Encoding.UTF8.GetString(IndexSerializer.SerializeIndex(index));

        Assert.DoesNotContain("\"target\"", json);    // file entry has no symlink target
        Assert.DoesNotContain("\"entryName\"", json); // blob storage has no pack entry name
    }

    [Fact]
    public void Deserialize_Rejects_Unsupported_SchemaVersion()
    {
        var future = "{\"schemaVersion\":999,\"backup\":{\"name\":\"x\",\"encrypted\":false,\"createdAt\":\"2026-07-16T00:00:00+00:00\"},\"versions\":[],\"packs\":{}}";

        Assert.Throws<NotSupportedException>(() =>
            IndexSerializer.DeserializeInfoFile(Encoding.UTF8.GetBytes(future)));
    }
}
