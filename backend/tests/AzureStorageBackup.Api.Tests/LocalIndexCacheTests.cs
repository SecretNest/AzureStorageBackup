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
}
