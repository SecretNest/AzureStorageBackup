using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class RestoreEstimatorTests
{
    [Fact]
    public void Estimate_Counts_Shared_Pack_And_Dedup_Blob_Once()
    {
        var index = new VersionIndex
        {
            Version = 1,
            Entries =
            [
                // Two files in pack "1" (3 volumes, sizes [100,100,50])
                new IndexEntry { Path = "a.txt", Kind = "file", Length = 40, Permissions = "0644",
                    Storage = new StorageRef { Kind = "pack", Ref = "1", EntryName = "a.txt" } },
                new IndexEntry { Path = "b.txt", Kind = "file", Length = 60, Permissions = "0644",
                    Storage = new StorageRef { Kind = "pack", Ref = "1", EntryName = "b.txt" } },
                // Two files sharing one data blob (deduplicated, volume size [30])
                new IndexEntry { Path = "c.txt", Kind = "file", Length = 70, Permissions = "0644",
                    Storage = new StorageRef { Kind = "blob", Ref = "data/h", VolumeSizes = [30] } },
                new IndexEntry { Path = "d.txt", Kind = "file", Length = 70, Permissions = "0644",
                    Storage = new StorageRef { Kind = "blob", Ref = "data/h", VolumeSizes = [30] } },
            ],
        };
        var info = new BackupInfoFile
        {
            Backup = new BackupMeta { Name = "test", CreatedAt = DateTimeOffset.UtcNow },
            Packs = { ["1"] = new PackInfo { Blob = "packs/1.7z", Volumes = 3, VolumeSizes = [100, 100, 50] } },
        };

        var est = RestoreEstimator.Compute(index, info, ["a.txt", "b.txt", "c.txt", "d.txt"]);

        Assert.Equal(250 + 30, est.DownloadBytes);   // pack 250 (counted once) + data 30 (counted once)
        Assert.Equal(40 + 60 + 70 + 70, est.UncompressedBytes);
        Assert.Equal(4, est.FileCount);
        Assert.Equal(2, est.DistinctObjects.Count);  // pack:1 + blob:data/h
    }
}
