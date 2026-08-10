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
        public Task<(string Name, int Volumes)> WriteIndexAsync(Account a, string c, int v, VersionIndex i, string? p, AccessTier? t = null, CancellationToken ct = default) => Task.FromResult(("i", 1));
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
}
