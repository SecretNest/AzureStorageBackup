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
    public async Task Referenced_Set_Includes_Info_Indexes_And_All_Volumes_Across_Versions()
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

        // What the catalog hands the pure function: one row per storage object, kind and volume count included.
        // data/h appears twice on purpose: one version records it as a single blob, another as three volumes.
        // The two spellings occupy DIFFERENT names, so both have to end up protected.
        var objects = new[]
        {
            ("pack", "p1", 1), ("blob", "data/h", 1), ("blob", "data/h", 3), ("blob", "data/big", 2),
        }.ToAsyncEnumerable();

        var refs = await BackupChecker.ReferencedBlobNamesAsync(info, objects);

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
        // The single-volume data blob (referenced by v2) — and, because another version records the same ref as
        // three volumes, its suffixed names too. One volume is the bare name and three are .001..003: the sets are
        // disjoint, so taking only the larger count would leave the live bare name unprotected.
        Assert.Contains("data/h", refs);
        Assert.Contains("data/h.001", refs);
        Assert.Contains("data/h.002", refs);
        Assert.Contains("data/h.003", refs);
        // Both volumes of a split single-file blob. The volume count for these lives on the entry, not in the info
        // file, so it has to travel with the ref — miss it and the .002 is swept as an orphan and the file is gone.
        Assert.Contains("data/big.001", refs);
        Assert.Contains("data/big.002", refs);
    }

    [Fact]
    public async Task Referenced_Set_Throws_When_Pack_Metadata_Missing()
    {
        // The pack is referenced but info.Packs holds no metadata for it → the volume count cannot be determined → throw, forcing the caller to abandon the deletion (safety first).
        var info = new BackupInfoFile
        {
            Backup = new BackupMeta { Name = "t", CreatedAt = DateTimeOffset.UtcNow },
            Versions = { new BackupVersion { Version = 1, IndexBlob = "idx/1", CreatedAt = default, Stats = new VersionStats(0, 0, 0, 0) } },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BackupChecker.ReferencedBlobNamesAsync(info, new[] { ("pack", "ghost", 1) }.ToAsyncEnumerable()));
    }
}
