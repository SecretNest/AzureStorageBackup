using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Pure-logic unit tests for building the referenced set (no Azurite required). This is the **load-bearing safety test** of the orphan
/// deletion path: the referenced set must cover the info file + the IndexBlob of every retained version + every volume of every StorageRef
/// (across all versions, including those referenced only by an old version). Miss any one of them and referenced data is misjudged as an orphan and deleted = data loss.
/// </summary>
public sealed class BackupReferencedSetTests
{
    [Fact]
    public void Referenced_Set_Includes_Info_Indexes_And_All_Volumes_Across_Versions()
    {
        // 2 versions: v1 IndexBlob=idx/1 (references a member of the 3-volume pack p1), v2 IndexBlob=idx/2 (references the single-volume data/h).
        var info = new BackupInfoFile
        {
            Backup = new BackupMeta { Name = "t", CreatedAt = DateTimeOffset.UtcNow },
            Versions =
            {
                new BackupVersion { Version = 1, IndexBlob = "idx/1", CreatedAt = default, Stats = new VersionStats(0, 0, 0, 0) },
                new BackupVersion { Version = 2, IndexBlob = "idx/2", CreatedAt = default, Stats = new VersionStats(0, 0, 0, 0) },
            },
            Packs = { ["p1"] = new PackInfo { Blob = "packs/p1.7z", Volumes = 3, Members = { "hh" } } },
        };

        var v1 = new VersionIndex
        {
            Version = 1,
            Entries =
            {
                new IndexEntry
                {
                    Path = "foo.txt", Kind = "file", Permissions = "0644", FullHash = "hh",
                    Storage = new StorageRef { Kind = "pack", Ref = "p1", EntryName = "foo.txt" },
                },
            },
        };
        var v2 = new VersionIndex
        {
            Version = 2,
            Entries =
            {
                new IndexEntry
                {
                    Path = "bar.bin", Kind = "file", Permissions = "0644", FullHash = "h",
                    Storage = new StorageRef { Kind = "blob", Ref = "data/h", Volumes = 1 },
                },
            },
        };
        var indexes = new Dictionary<int, VersionIndex> { [1] = v1, [2] = v2 };

        var refs = BackupChecker.ReferencedBlobNames(info, indexes);

        // The info file (both namings are protected, never deleted).
        Assert.Contains(BackupDiscovery.IndexBlobName, refs);
        Assert.Contains(BackupDiscovery.EncryptedIndexBlobName, refs);
        // The second-level index of every version.
        Assert.Contains("idx/1", refs);
        Assert.Contains("idx/2", refs);
        // All 3 volumes of pack p1 (referenced only by v1, still in the set).
        Assert.Contains("packs/p1.7z.001", refs);
        Assert.Contains("packs/p1.7z.002", refs);
        Assert.Contains("packs/p1.7z.003", refs);
        // The single-volume data blob (referenced by v2).
        Assert.Contains("data/h", refs);
    }

    [Fact]
    public void Referenced_Set_Throws_When_Pack_Metadata_Missing()
    {
        // The pack is referenced but info.Packs holds no metadata for it → the volume count cannot be determined → throw, forcing the caller to abandon the deletion (safety first).
        var info = new BackupInfoFile
        {
            Backup = new BackupMeta { Name = "t", CreatedAt = DateTimeOffset.UtcNow },
            Versions = { new BackupVersion { Version = 1, IndexBlob = "idx/1", CreatedAt = default, Stats = new VersionStats(0, 0, 0, 0) } },
        };
        var idx = new VersionIndex
        {
            Version = 1,
            Entries =
            {
                new IndexEntry
                {
                    Path = "foo.txt", Kind = "file", Permissions = "0644",
                    Storage = new StorageRef { Kind = "pack", Ref = "ghost", EntryName = "foo.txt" },
                },
            },
        };

        Assert.Throws<InvalidOperationException>(() =>
            BackupChecker.ReferencedBlobNames(info, new Dictionary<int, VersionIndex> { [1] = idx }));
    }
}
