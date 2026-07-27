using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

public sealed class LocalIndexCacheTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public LocalIndexCacheTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>记录 ReadIndexAsync 调用次数、可返回指定索引的假 store。</summary>
    private sealed class FakeStore(VersionIndex index) : IBackupInfoStore
    {
        public int Reads { get; private set; }
        public Task<VersionIndex> ReadIndexAsync(Account account, string container, string indexBlob, string? password, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(index);
        }
        public Task<BackupInfoFile?> ReadInfoAsync(Account a, string c, string? p, CancellationToken ct = default) => Task.FromResult<BackupInfoFile?>(null);
        public Task<(BackupInfoFile Info, string ETag)?> ReadInfoWithETagAsync(Account a, string c, string? p, CancellationToken ct = default) => Task.FromResult<(BackupInfoFile, string)?>(null);
        public Task WriteInfoAsync(Account a, string c, BackupInfoFile i, string? p, AccessTier? t = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> WriteInfoConditionalAsync(Account a, string c, BackupInfoFile i, string? p, AccessTier? t, string? e, CancellationToken ct = default) => Task.FromResult("etag");
        public Task<string> WriteIndexAsync(Account a, string c, int v, VersionIndex i, string? p, AccessTier? t = null, CancellationToken ct = default) => Task.FromResult("indexes/v.bin");
    }

    private static Account Acc() => new() { Id = 1, Name = "a", BlobEndpoint = "http://x", AccountKeyProtected = TestSecrets.Protect("k") };

    private static VersionIndex Index(int version, string path) => new()
    {
        Version = version,
        Entries =
        [
            new IndexEntry
            {
                Path = path, Kind = "file", Length = 5, Mtime = DateTimeOffset.UnixEpoch, Permissions = "0644",
                FullHash = "xxh128:" + new string('a', 32),
                Storage = new StorageRef { Kind = "blob", Ref = "data/x" },
            },
        ],
    };

    [Fact]
    public async Task Miss_Downloads_And_Backfills_Then_Hit_Serves_From_Local()
    {
        var store = new FakeStore(Index(1, "a.txt"));
        var cache = new LocalIndexCache(_db, store);

        var first = await cache.ReadAsync(Acc(), "c", 1, identityTicks: 100, "indexes/v1.bin", null);
        var second = await cache.ReadAsync(Acc(), "c", 1, identityTicks: 100, "indexes/v1.bin", null);

        Assert.Equal("a.txt", first.Entries[0].Path);
        Assert.Equal("a.txt", second.Entries[0].Path);
        Assert.Equal(1, store.Reads); // 第二次命中本地，不再下载
    }

    [Fact]
    public async Task Put_Populates_Cache_Without_Download()
    {
        var store = new FakeStore(Index(1, "should-not-download"));
        var cache = new LocalIndexCache(_db, store);

        await cache.PutAsync(1, "c", 1, identityTicks: 100, Index(1, "a.txt"));
        var got = await cache.ReadAsync(Acc(), "c", 1, identityTicks: 100, "indexes/v1.bin", null);

        Assert.Equal("a.txt", got.Entries[0].Path);
        Assert.Equal(0, store.Reads); // 完全没下载
    }

    [Fact]
    public async Task Identity_Mismatch_Redownloads_And_Overwrites()
    {
        var store = new FakeStore(Index(1, "cloud.txt"));
        var cache = new LocalIndexCache(_db, store);

        await cache.PutAsync(1, "c", 1, identityTicks: 100, Index(1, "stale.txt")); // 旧身份
        // container 重建 → 新身份 200：缓存失效，重新下载云端。
        var got = await cache.ReadAsync(Acc(), "c", 1, identityTicks: 200, "indexes/v1.bin", null);

        Assert.Equal("cloud.txt", got.Entries[0].Path);
        Assert.Equal(1, store.Reads);
        // 覆盖后按新身份命中，不再下载。
        var again = await cache.ReadAsync(Acc(), "c", 1, identityTicks: 200, "indexes/v1.bin", null);
        Assert.Equal("cloud.txt", again.Entries[0].Path);
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task Remove_Evicts_Entry()
    {
        var store = new FakeStore(Index(1, "cloud.txt"));
        var cache = new LocalIndexCache(_db, store);

        await cache.PutAsync(1, "c", 1, 100, Index(1, "a.txt"));
        await cache.RemoveAsync(1, "c", 1);
        await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null); // 命中缺失 → 下载

        Assert.Equal(1, store.Reads);
    }

    /// <summary>删配置连带清本地版本索引缓存（P2T6 review follow-up）：按 (accountId, container) 精确清除
    /// 全部版本，不同 account 或不同 container 的缓存不受影响（避免跨备份误删）。</summary>
    [Fact]
    public async Task RemoveForContainer_Evicts_All_Versions_But_Not_Other_Account_Or_Container()
    {
        var store = new FakeStore(Index(1, "x"));
        var cache = new LocalIndexCache(_db, store);

        await cache.PutAsync(1, "c", 1, 100, Index(1, "a.txt"));
        await cache.PutAsync(1, "c", 2, 100, Index(2, "b.txt"));
        await cache.PutAsync(1, "other-c", 1, 100, Index(1, "keep-other-container.txt"));
        await cache.PutAsync(2, "c", 1, 100, Index(1, "keep-other-account.txt"));

        await cache.RemoveForContainerAsync(1, "c");

        Assert.Equal(2, await _db.CachedVersionIndexes.CountAsync());
        Assert.True(await _db.CachedVersionIndexes.AnyAsync(x => x.AccountId == 1 && x.Container == "other-c"));
        Assert.True(await _db.CachedVersionIndexes.AnyAsync(x => x.AccountId == 2 && x.Container == "c"));
    }

    // ---- 进程内索引缓存（Backup__IndexCacheSize）----
    //
    // SQLite 里存的是序列化字节，命中也仍要重建整份索引（50 万条目实测约 0.9 s / 350 MB 分配），
    // 而还原对话框每展开一个目录都会走一遍。这一层缓存反序列化后的对象。
    // 判据统一用「把 SQLite 行删掉再读」：还能读到内容，就只可能来自进程内缓存。

    /// <summary>启用时：同一版本第二次读不再碰 SQLite，也不碰云端。</summary>
    [Fact]
    public async Task Memory_Cache_Serves_Repeat_Reads_Without_Touching_The_Row()
    {
        var store = new FakeStore(Index(1, "cloud.txt"));
        var cache = new LocalIndexCache(_db, store, new VersionIndexMemoryCache(2));

        await cache.PutAsync(1, "c", 1, 100, Index(1, "a.txt"));
        var first = await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null);

        // 釜底抽薪：把行删干净。之后还能读到 a.txt，就证明来自进程内缓存。
        await _db.CachedVersionIndexes.ExecuteDeleteAsync();

        var second = await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null);

        Assert.Equal("a.txt", first.Entries[0].Path);
        Assert.Equal("a.txt", second.Entries[0].Path);
        Assert.Equal(0, store.Reads); // 全程没回落云端
    }

    /// <summary>容量 0（小内存机器）：这一层整体旁路，行为与加它之前完全一致。</summary>
    [Fact]
    public async Task Memory_Cache_Disabled_Falls_Back_To_The_Row_Every_Time()
    {
        var store = new FakeStore(Index(1, "cloud.txt"));
        var cache = new LocalIndexCache(_db, store, new VersionIndexMemoryCache(0));

        await cache.PutAsync(1, "c", 1, 100, Index(1, "a.txt"));
        await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null);
        await _db.CachedVersionIndexes.ExecuteDeleteAsync();

        // 没有进程内副本 → 只能回落云端。
        var second = await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null);

        Assert.Equal("cloud.txt", second.Entries[0].Path);
        Assert.Equal(1, store.Reads);
    }

    /// <summary>写入必须让进程内副本失效，否则修复改了索引、界面还在读改之前的那一份。</summary>
    [Fact]
    public async Task Writing_A_Version_Invalidates_The_Memory_Copy()
    {
        var store = new FakeStore(Index(1, "cloud.txt"));
        var cache = new LocalIndexCache(_db, store, new VersionIndexMemoryCache(2));

        await cache.PutAsync(1, "c", 1, 100, Index(1, "before.txt"));
        Assert.Equal("before.txt", (await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null)).Entries[0].Path);

        await cache.PutAsync(1, "c", 1, 100, Index(1, "after.txt")); // 例如修复改写了索引

        Assert.Equal("after.txt", (await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null)).Entries[0].Path);
    }

    /// <summary>退役某版本 / 删配置都要清掉进程内副本，不能留下已被删除版本的幽灵。</summary>
    [Fact]
    public async Task Removing_Evicts_The_Memory_Copy_Too()
    {
        var store = new FakeStore(Index(1, "cloud.txt"));
        var cache = new LocalIndexCache(_db, store, new VersionIndexMemoryCache(4));

        await cache.PutAsync(1, "c", 1, 100, Index(1, "v1.txt"));
        await cache.PutAsync(1, "c", 2, 100, Index(2, "v2.txt"));
        await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null);
        await cache.ReadAsync(Acc(), "c", 2, 100, "indexes/v2.bin", null);

        await cache.RemoveAsync(1, "c", 1);
        Assert.Equal("cloud.txt", (await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null)).Entries[0].Path);

        await cache.RemoveForContainerAsync(1, "c");
        await _db.CachedVersionIndexes.ExecuteDeleteAsync();
        Assert.Equal("cloud.txt", (await cache.ReadAsync(Acc(), "c", 2, 100, "indexes/v2.bin", null)).Entries[0].Path);
    }

    /// <summary>容量到顶时挤掉最久未使用的那份——这正是"小内存也能开一点"的依据。</summary>
    [Fact]
    public async Task Memory_Cache_Evicts_The_Least_Recently_Used_At_Capacity()
    {
        var store = new FakeStore(Index(1, "cloud.txt"));
        var cache = new LocalIndexCache(_db, store, new VersionIndexMemoryCache(1)); // 只留一份

        await cache.PutAsync(1, "c", 1, 100, Index(1, "v1.txt"));
        await cache.PutAsync(1, "c", 2, 100, Index(2, "v2.txt"));

        await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null); // 进缓存
        await cache.ReadAsync(Acc(), "c", 2, 100, "indexes/v2.bin", null); // 把 v1 挤出去

        await _db.CachedVersionIndexes.ExecuteDeleteAsync();

        // v2 仍在内存里；v1 已被挤出 → 回落云端。
        Assert.Equal("v2.txt", (await cache.ReadAsync(Acc(), "c", 2, 100, "indexes/v2.bin", null)).Entries[0].Path);
        Assert.Equal("cloud.txt", (await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null)).Entries[0].Path);
    }
}
