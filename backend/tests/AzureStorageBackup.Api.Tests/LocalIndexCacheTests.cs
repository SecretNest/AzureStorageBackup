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

    /// <summary>A fake store that counts ReadIndexAsync calls and hands back a supplied index.</summary>
    private sealed class FakeStore(VersionIndex index) : IBackupInfoStore
    {
        public int Reads { get; private set; }
        public Task<VersionIndex> ReadIndexAsync(Account account, string container, string indexBlob, string? password, int volumes = 1, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(index);
        }
        public Task<BackupInfoFile?> ReadInfoAsync(Account a, string c, string? p, CancellationToken ct = default) => Task.FromResult<BackupInfoFile?>(null);
        public Task<(BackupInfoFile Info, string ETag)?> ReadInfoWithETagAsync(Account a, string c, string? p, CancellationToken ct = default) => Task.FromResult<(BackupInfoFile, string)?>(null);
        public Task WriteInfoAsync(Account a, string c, BackupInfoFile i, string? p, AccessTier? t = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> WriteInfoConditionalAsync(Account a, string c, BackupInfoFile i, string? p, AccessTier? t, string? e, CancellationToken ct = default) => Task.FromResult("etag");
        public Task<(string Name, int Volumes)> WriteIndexAsync(Account a, string c, int v, VersionIndex i, string? p, AccessTier? t = null, CancellationToken ct = default) => Task.FromResult(("indexes/v.bin", 1));
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
        Assert.Equal(1, store.Reads); // The second read hits locally, no further download
    }

    [Fact]
    public async Task Put_Populates_Cache_Without_Download()
    {
        var store = new FakeStore(Index(1, "should-not-download"));
        var cache = new LocalIndexCache(_db, store);

        await cache.PutAsync(1, "c", 1, identityTicks: 100, Index(1, "a.txt"));
        var got = await cache.ReadAsync(Acc(), "c", 1, identityTicks: 100, "indexes/v1.bin", null);

        Assert.Equal("a.txt", got.Entries[0].Path);
        Assert.Equal(0, store.Reads); // No download at all
    }

    [Fact]
    public async Task Identity_Mismatch_Redownloads_And_Overwrites()
    {
        var store = new FakeStore(Index(1, "cloud.txt"));
        var cache = new LocalIndexCache(_db, store);

        await cache.PutAsync(1, "c", 1, identityTicks: 100, Index(1, "stale.txt")); // Old identity
        // Container rebuilt → new identity 200: the cache entry is stale, download from the cloud again.
        var got = await cache.ReadAsync(Acc(), "c", 1, identityTicks: 200, "indexes/v1.bin", null);

        Assert.Equal("cloud.txt", got.Entries[0].Path);
        Assert.Equal(1, store.Reads);
        // After the overwrite it hits under the new identity, no further download.
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
        await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null); // Entry gone → download

        Assert.Equal(1, store.Reads);
    }

    /// <summary>Deleting a config also clears the local version index cache (P2T6 review follow-up): clear every version for exactly
    /// that (accountId, container), leaving caches of a different account or a different container untouched (avoids deleting across backups).</summary>
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

    // ---- In-process index cache (Backup__IndexCacheSize) ----
    //
    // SQLite holds serialized bytes, so even a hit still has to rebuild the whole index (measured at roughly 0.9 s / 350 MB
    // allocated for 500k entries), and the restore dialog goes through it every time a directory is expanded. This layer caches the deserialized objects.
    // The test for it is always "delete the SQLite row, then read": if content still comes back, it can only have come from the in-process cache.

    /// <summary>When enabled: a second read of the same version touches neither SQLite nor the cloud.</summary>
    [Fact]
    public async Task Memory_Cache_Serves_Repeat_Reads_Without_Touching_The_Row()
    {
        var store = new FakeStore(Index(1, "cloud.txt"));
        var cache = new LocalIndexCache(_db, store, new VersionIndexMemoryCache(2));

        await cache.PutAsync(1, "c", 1, 100, Index(1, "a.txt"));
        var first = await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null);

        // Pull the rug out: delete the row entirely. If a.txt still reads back after that, it proves it came from the in-process cache.
        await _db.CachedVersionIndexes.ExecuteDeleteAsync();

        var second = await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null);

        Assert.Equal("a.txt", first.Entries[0].Path);
        Assert.Equal("a.txt", second.Entries[0].Path);
        Assert.Equal(0, store.Reads); // Never fell back to the cloud at any point
    }

    /// <summary>Capacity 0 (low-memory machines): the layer is bypassed entirely, behaving exactly as it did before it existed.</summary>
    [Fact]
    public async Task Memory_Cache_Disabled_Falls_Back_To_The_Row_Every_Time()
    {
        var store = new FakeStore(Index(1, "cloud.txt"));
        var cache = new LocalIndexCache(_db, store, new VersionIndexMemoryCache(0));

        await cache.PutAsync(1, "c", 1, 100, Index(1, "a.txt"));
        await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null);
        await _db.CachedVersionIndexes.ExecuteDeleteAsync();

        // No in-process copy → falling back to the cloud is the only option.
        var second = await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null);

        Assert.Equal("cloud.txt", second.Entries[0].Path);
        Assert.Equal(1, store.Reads);
    }

    /// <summary>A write must invalidate the in-process copy, or repair changes the index while the UI is still reading the pre-change one.</summary>
    [Fact]
    public async Task Writing_A_Version_Invalidates_The_Memory_Copy()
    {
        var store = new FakeStore(Index(1, "cloud.txt"));
        var cache = new LocalIndexCache(_db, store, new VersionIndexMemoryCache(2));

        await cache.PutAsync(1, "c", 1, 100, Index(1, "before.txt"));
        Assert.Equal("before.txt", (await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null)).Entries[0].Path);

        await cache.PutAsync(1, "c", 1, 100, Index(1, "after.txt")); // e.g. repair rewrote the index

        Assert.Equal("after.txt", (await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null)).Entries[0].Path);
    }

    /// <summary>Retiring a version and deleting a config must both clear the in-process copy; no ghost of a deleted version may linger.</summary>
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

    /// <summary>At capacity, evict the least recently used one — this is exactly what makes "even a small memory budget can turn some on" hold.</summary>
    [Fact]
    public async Task Memory_Cache_Evicts_The_Least_Recently_Used_At_Capacity()
    {
        var store = new FakeStore(Index(1, "cloud.txt"));
        var cache = new LocalIndexCache(_db, store, new VersionIndexMemoryCache(1)); // Keep only one

        await cache.PutAsync(1, "c", 1, 100, Index(1, "v1.txt"));
        await cache.PutAsync(1, "c", 2, 100, Index(2, "v2.txt"));

        await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null); // Into the cache
        await cache.ReadAsync(Acc(), "c", 2, 100, "indexes/v2.bin", null); // Evicts v1

        await _db.CachedVersionIndexes.ExecuteDeleteAsync();

        // v2 is still in memory; v1 has been evicted → falls back to the cloud.
        Assert.Equal("v2.txt", (await cache.ReadAsync(Acc(), "c", 2, 100, "indexes/v2.bin", null)).Entries[0].Path);
        Assert.Equal("cloud.txt", (await cache.ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null)).Entries[0].Path);
    }

    /// <summary>A fake store that parks every ReadIndexAsync caller until the expected number have arrived —
    /// the deterministic way to hold two scopes inside the same cold-miss window.</summary>
    private sealed class RendezvousStore(VersionIndex index, int parties) : IBackupInfoStore
    {
        private int _arrived;
        private readonly TaskCompletionSource _allIn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<VersionIndex> ReadIndexAsync(Account account, string container, string indexBlob, string? password, int volumes = 1, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _arrived) >= parties)
                _allIn.TrySetResult();
            await _allIn.Task;
            return index;
        }
        public Task<BackupInfoFile?> ReadInfoAsync(Account a, string c, string? p, CancellationToken ct = default) => Task.FromResult<BackupInfoFile?>(null);
        public Task<(BackupInfoFile Info, string ETag)?> ReadInfoWithETagAsync(Account a, string c, string? p, CancellationToken ct = default) => Task.FromResult<(BackupInfoFile, string)?>(null);
        public Task WriteInfoAsync(Account a, string c, BackupInfoFile i, string? p, AccessTier? t = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> WriteInfoConditionalAsync(Account a, string c, BackupInfoFile i, string? p, AccessTier? t, string? e, CancellationToken ct = default) => Task.FromResult("etag");
        public Task<(string Name, int Volumes)> WriteIndexAsync(Account a, string c, int v, VersionIndex i, string? p, AccessTier? t = null, CancellationToken ct = default) => Task.FromResult(("indexes/v.bin", 1));
    }

    /// <summary>
    /// Two scopes (their own DbContexts, as in production: a restore and a concurrent cleanup) both cold-miss
    /// the same (account, container, version): both FirstOrDefault null, both download, both Add — and the
    /// second SaveChanges hits the (AccountId, Container, Version) unique index. That is a harmless race, not
    /// an error: the loser must fall back to updating the winner's row instead of throwing DbUpdateException
    /// out of a read path (failing a whole restore group or cleanup pass over cache bookkeeping).
    /// </summary>
    [Fact]
    public async Task Concurrent_Cold_Misses_Backfill_Once_Instead_Of_Throwing()
    {
        // A file database of its own: the fixture's in-memory database lives on one connection, and this
        // test needs two contexts that genuinely interleave (see TestWebAppFactory for the same note).
        var dbPath = Path.Combine(Path.GetTempPath(), "asb-idx-race-" + Guid.NewGuid().ToString("N") + ".db");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"DataSource={dbPath}").Options;
        try
        {
            using (var setup = new AppDbContext(options))
                setup.Database.EnsureCreated();

            var store = new RendezvousStore(Index(1, "raced.txt"), parties: 2);
            using var db1 = new AppDbContext(options);
            using var db2 = new AppDbContext(options);

            var reads = await Task.WhenAll(
                Task.Run(() => new LocalIndexCache(db1, store).ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null)),
                Task.Run(() => new LocalIndexCache(db2, store).ReadAsync(Acc(), "c", 1, 100, "indexes/v1.bin", null)));

            Assert.All(reads, r => Assert.Equal("raced.txt", r.Entries[0].Path));
            using var verify = new AppDbContext(options);
            Assert.Equal(1, await verify.CachedVersionIndexes.CountAsync(
                x => x.AccountId == 1 && x.Container == "c" && x.Version == 1));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                try { File.Delete(f); } catch { /* best effort */ }
        }
    }
}
