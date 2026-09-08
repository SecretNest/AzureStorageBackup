using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class RestoreEstimatorTests
{
    private static IndexEntry Entry(string path, long length, StorageRef? storage) => new()
    {
        Path = path,
        Kind = "file",
        Length = length,
        Mtime = DateTimeOffset.UnixEpoch,
        Permissions = "0644",
        Storage = storage,
    };

    private static BackupInfoFile Info() => new()
    {
        Backup = new BackupMeta { Name = "test", CreatedAt = DateTimeOffset.UtcNow },
        Packs = { ["1"] = new PackInfo { Blob = "packs/1.7z", Volumes = 3, VolumeSizes = [100, 100, 50] } },
    };

    /// <summary>The estimate is now handed the entries the catalog looked up for the selected paths, not a whole
    /// index plus the paths to filter it by; the deduplication it does over them is unchanged.</summary>
    [Fact]
    public void Estimate_Counts_Shared_Pack_And_Dedup_Blob_Once()
    {
        // Two files in pack "1" (3 volumes, sizes [100,100,50]) and two sharing one data blob (volume size [30]).
        var selected = new List<IndexEntry>
        {
            Entry("a.txt", 40, new StorageRef { Kind = "pack", Ref = "1", EntryName = "a.txt" }),
            Entry("b.txt", 60, new StorageRef { Kind = "pack", Ref = "1", EntryName = "b.txt" }),
            Entry("c.txt", 70, new StorageRef { Kind = "blob", Ref = "data/h", VolumeSizes = [30] }),
            Entry("d.txt", 70, new StorageRef { Kind = "blob", Ref = "data/h", VolumeSizes = [30] }),
        };

        var est = RestoreEstimator.Compute(selected, Info());

        Assert.Equal(250 + 30, est.DownloadBytes);   // pack 250 (counted once) + data 30 (counted once)
        Assert.Equal(40 + 60 + 70 + 70, est.UncompressedBytes);
        Assert.Equal(4, est.FileCount);
        Assert.Equal(2, est.DistinctObjects.Count);  // pack:1 + blob:data/h
    }

    /// <summary>
    /// An entry with no storage costs nothing to download and is not a file the restore fetches, so it must not be
    /// counted — the old code got that from filtering the index by <c>Storage is not null</c>, and now that the
    /// caller hands over whatever the catalog found at those paths, the filter has to live here.
    /// </summary>
    [Fact]
    public void Entries_Without_Storage_Are_Not_Counted()
    {
        var selected = new List<IndexEntry>
        {
            Entry("a.txt", 40, new StorageRef { Kind = "blob", Ref = "data/h", VolumeSizes = [30] }),
            Entry("empty.txt", 0, null),
        };

        var est = RestoreEstimator.Compute(selected, Info());

        Assert.Equal(1, est.FileCount);
        Assert.Equal(40, est.UncompressedBytes);
        Assert.Equal(30, est.DownloadBytes);
    }

    /// <summary>A pack the info file has never heard of contributes no download bytes rather than throwing: an info
    /// file and an index can disagree while a repair is in flight, and an estimate is not the place to fail over it.</summary>
    [Fact]
    public void An_Unknown_Pack_Contributes_Nothing_To_The_Download()
    {
        var selected = new List<IndexEntry>
        {
            Entry("a.txt", 40, new StorageRef { Kind = "pack", Ref = "ghost", EntryName = "a.txt" }),
        };

        var est = RestoreEstimator.Compute(selected, Info());

        Assert.Equal(0, est.DownloadBytes);
        Assert.Equal(40, est.UncompressedBytes);
        Assert.Equal(["pack:ghost"], est.DistinctObjects);
    }
}
