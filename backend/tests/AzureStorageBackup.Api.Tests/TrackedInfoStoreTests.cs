using Azure;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

public sealed class TrackedInfoStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly LocalBackupStateStore _state;

    public TrackedInfoStoreTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        _state = new LocalBackupStateStore(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    /// <summary>A fake store with configurable returns/throws that records the calls it receives.</summary>
    private sealed class FakeStore : IBackupInfoStore
    {
        public (BackupInfoFile Info, string ETag)? CloudInfo { get; set; }
        public bool ThrowConflictOnWrite { get; set; }
        public int InfoReads { get; private set; }
        public string? LastIfMatch { get; private set; }
        public int Writes { get; private set; }

        public Task<(BackupInfoFile Info, string ETag)?> ReadInfoWithETagAsync(Account a, string c, string? p, CancellationToken ct = default)
        {
            InfoReads++;
            return Task.FromResult(CloudInfo);
        }
        public Task<string> WriteInfoConditionalAsync(Account a, string c, BackupInfoFile i, string? p, AccessTier? t, string? ifMatch, CancellationToken ct = default)
        {
            Writes++;
            LastIfMatch = ifMatch;
            if (ThrowConflictOnWrite)
                throw new RequestFailedException(412, "precondition failed");
            return Task.FromResult("etag-" + Writes);
        }
        public Task<BackupInfoFile?> ReadInfoAsync(Account a, string c, string? p, CancellationToken ct = default) => Task.FromResult(CloudInfo?.Info);
        public Task WriteInfoAsync(Account a, string c, BackupInfoFile i, string? p, AccessTier? t = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<VersionIndex> ReadIndexAsync(Account a, string c, string b, string? p, int volumes = 1, CancellationToken ct = default) => Task.FromResult(new VersionIndex());
        public Task<(string Name, int Volumes)> WriteIndexAsync(Account a, string c, int v, VersionIndex i, string? p, AccessTier? t = null, CancellationToken ct = default, StageTracker? progress = null) => Task.FromResult(("i", 1));
    }

    private static Account Acc() => new() { Id = 1, Name = "a", BlobEndpoint = "http://x", AccountKeyProtected = TestSecrets.Protect("k") };
    private static BackupInfoFile Info(string name) => new() { Backup = new BackupMeta { Name = name, CreatedAt = DateTimeOffset.UnixEpoch } };

    [Fact]
    public async Task Load_From_Local_Does_Not_Read_Cloud()
    {
        var store = new FakeStore();
        var tracked = new TrackedInfoStore(store, _state);
        await _state.PutAsync(1, "c", IndexSerializer.SerializeInfoFile(Info("local")), "etag0");

        var info = await tracked.LoadAsync(Acc(), "c", null);

        Assert.Equal("local", info!.Backup.Name);
        Assert.Equal(0, store.InfoReads); // local hit, no cloud read
    }

    [Fact]
    public async Task Load_When_Local_Absent_Reads_Cloud_And_Seeds()
    {
        var store = new FakeStore { CloudInfo = (Info("cloud"), "etagX") };
        var tracked = new TrackedInfoStore(store, _state);

        var info = await tracked.LoadAsync(Acc(), "c", null);

        Assert.Equal("cloud", info!.Backup.Name);
        Assert.Equal(1, store.InfoReads);
        Assert.Equal("etagX", (await _state.TryGetAsync(1, "c"))!.Value.ETag); // the local copy was backfilled
    }

    [Fact]
    public async Task Write_Uses_Local_ETag_As_IfMatch_And_Updates_It()
    {
        var store = new FakeStore();
        var tracked = new TrackedInfoStore(store, _state);
        await _state.PutAsync(1, "c", IndexSerializer.SerializeInfoFile(Info("v1")), "etag0");

        await tracked.WriteAsync(Acc(), "c", Info("v2"), null, null);

        Assert.Equal("etag0", store.LastIfMatch);                       // uses the local ETag as If-Match
        var local = await _state.TryGetAsync(1, "c");
        Assert.Equal("etag-1", local!.Value.ETag);                      // updated to the new ETag
        Assert.Equal("v2", IndexSerializer.DeserializeInfoFile(local.Value.InfoBytes).Backup.Name);
    }

    [Fact]
    public async Task Write_Conflict_Clears_Local_And_Throws()
    {
        var store = new FakeStore { ThrowConflictOnWrite = true };
        var tracked = new TrackedInfoStore(store, _state);
        await _state.PutAsync(1, "c", IndexSerializer.SerializeInfoFile(Info("v1")), "etag0");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tracked.WriteAsync(Acc(), "c", Info("v2"), null, null));

        Assert.Null(await _state.TryGetAsync(1, "c")); // after a conflict the local state is cleared, and re-synced next time
    }

    /// <summary>
    /// Two scopes cold-miss the same (account, container) local state — an ETag conflict just cleared the
    /// row, and two concurrent reads both took LoadAsync's backfill path: both query null, both insert, the
    /// loser hits the (AccountId, Container) unique index. That is a harmless race on a locally cached copy,
    /// not an error — surfaced live by the damage-repair chaos storm as a bare 500 out of /file-versions.
    /// The loser must fall back to updating the winner's row (the same discipline LocalIndexCache.UpsertAsync
    /// learned in the first audit round). The interleave is pinned deterministically: the winner's row is
    /// inserted from a second context inside the loser's own SavingChanges window — after its null query,
    /// before its insert executes.
    /// </summary>
    [Fact]
    public async Task A_Concurrent_Cold_Backfill_Falls_Back_To_The_Winners_Row()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "asb-state-race-" + Guid.NewGuid().ToString("N") + ".db");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"DataSource={dbPath}").Options;
        try
        {
            using (var setup = new AppDbContext(options))
                setup.Database.EnsureCreated();

            using var loser = new AppDbContext(options);
            var raced = false;
            loser.SavingChanges += (_, _) =>
            {
                if (raced)
                    return; // only the first save (the doomed insert) gets the rival; the fallback must not
                raced = true;
                using var winner = new AppDbContext(options);
                winner.LocalBackupStates.Add(new LocalBackupState
                {
                    AccountId = 1, Container = "c",
                    InfoBytes = [1], ETag = "etag-winner", UpdatedAt = DateTimeOffset.UtcNow,
                });
                winner.SaveChanges();
            };

            await new LocalBackupStateStore(loser).PutAsync(1, "c", [2], "etag-loser");

            using var verify = new AppDbContext(options);
            var row = await verify.LocalBackupStates.SingleAsync(x => x.AccountId == 1 && x.Container == "c");
            Assert.Equal("etag-loser", row.ETag); // the loser's (later) payload lands as an update
            Assert.Equal([2], row.InfoBytes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                try { File.Delete(f); } catch { /* best effort */ }
        }
    }
}
