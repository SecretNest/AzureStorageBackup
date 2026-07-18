using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 引用集构造的纯逻辑单测（无需 Azurite）。这是孤儿删除路径的**承重安全测试**：
/// 引用集必须涵盖 信息文件 + 每个保留版本的 IndexBlob + 每个 StorageRef 的全部分卷（跨全部版本，
/// 含仅被旧版本引用者）。漏掉任何一项都会把被引用数据误判为孤儿而删除 = 数据丢失。
/// </summary>
public sealed class BackupReferencedSetTests
{
    [Fact]
    public void Referenced_Set_Includes_Info_Indexes_And_All_Volumes_Across_Versions()
    {
        // 2 版本：v1 IndexBlob=idx/1（引用 3 卷 pack p1 的成员），v2 IndexBlob=idx/2（引用单卷 data/h）。
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

        // 信息文件（两种命名都保护，绝不删）。
        Assert.Contains(BackupDiscovery.IndexBlobName, refs);
        Assert.Contains(BackupDiscovery.EncryptedIndexBlobName, refs);
        // 每个版本的第二级索引。
        Assert.Contains("idx/1", refs);
        Assert.Contains("idx/2", refs);
        // pack p1 的全部 3 卷（仅被 v1 引用，仍在集内）。
        Assert.Contains("packs/p1.7z.001", refs);
        Assert.Contains("packs/p1.7z.002", refs);
        Assert.Contains("packs/p1.7z.003", refs);
        // 单卷 data blob（被 v2 引用）。
        Assert.Contains("data/h", refs);
    }

    [Fact]
    public void Referenced_Set_Throws_When_Pack_Metadata_Missing()
    {
        // pack 被引用但 info.Packs 无其元数据 → 无法确定分卷数 → 抛错，迫使调用方放弃删除（安全优先）。
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
