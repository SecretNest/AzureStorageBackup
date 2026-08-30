using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Restore deliberately runs WITHOUT the busy lock so it can coexist with a backup — but repair and
/// dead-weight compaction REWRITE the very volume families a restore streams (VolumeBlobIO.ReplaceAsync
/// overwrites volumes under the same base name before deleting leftovers), and retention deletes blobs a
/// restore may be mid-download of. Coexistence with a backup is safe (a backup never rewrites objects an
/// existing version references); coexistence with a rewriter is data corruption. Hence the reader side of
/// BackupBusyTracker: restores register as readers, and readers exclude exactly the rewriting activities
/// (Repairing / CleaningUp / Deleting) — in both directions, atomically under the tracker's one lock.
/// </summary>
public sealed class RestoreExclusionTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    // ---- The tracker matrix -------------------------------------------------------------------------

    [Fact]
    public void Readers_Coexist_With_A_Backup_But_Not_With_Rewriters()
    {
        var busy = new BackupBusyTracker();

        // Reader alongside a running backup: allowed (the documented behavior).
        Assert.True(busy.TryAcquire(1, "c", "BackingUp"));
        Assert.True(busy.TryAddReader(1, "c", out _));

        // A rewriter must not start while a reader is active…
        Assert.True(busy.TryAddReader(2, "c2", out _));
        Assert.False(busy.TryAcquire(2, "c2", "Repairing", refuseWhenReaders: true));
        Assert.False(busy.TryAcquire(2, "c2", "CleaningUp", refuseWhenReaders: true));

        // …and a reader must not start while a rewriter holds the target.
        Assert.True(busy.TryAcquire(3, "c3", "Repairing"));
        Assert.False(busy.TryAddReader(3, "c3", out var conflict));
        Assert.Equal("Repairing", conflict);

        // Without the flag (backup/check acquisitions), readers do not block acquisition.
        Assert.True(busy.TryAcquire(4, "c2", "BackingUp"));
    }

    [Fact]
    public void Reader_Counting_Releases_Only_When_The_Last_Reader_Leaves()
    {
        var busy = new BackupBusyTracker();
        Assert.True(busy.TryAddReader(1, "c", out _));
        Assert.True(busy.TryAddReader(1, "c", out _));
        Assert.True(busy.HasReaders(1, "c"));

        busy.RemoveReader(1, "c");
        Assert.True(busy.HasReaders(1, "c"));
        Assert.False(busy.TryAcquire(1, "c", "Repairing", refuseWhenReaders: true));

        busy.RemoveReader(1, "c");
        Assert.False(busy.HasReaders(1, "c"));
        Assert.True(busy.TryAcquire(1, "c", "Repairing", refuseWhenReaders: true));
    }

    // ---- The runners honor the matrix ---------------------------------------------------------------

    private async Task<(int AccountId, int ConfigId, string Container)> CreateConfigAsync(HttpClient client, string tag)
    {
        var accountReq = new AccountRequest("acct-" + tag, null,
            "https://t" + Guid.NewGuid().ToString("N")[..12] + ".blob.core.windows.net", AzureRegion.Global,
            "dGVzdGtleQ==", false, ProxyMode.Independent, null, null, null, null);
        var account = await (await client.PostAsJsonAsync("/api/accounts", accountReq))
            .Content.ReadFromJsonAsync<AccountResponse>();
        var root = Path.Combine(Path.GetTempPath(), "asb-rex-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var container = "rex-" + Guid.NewGuid().ToString("N")[..8];
        var configReq = new BackupConfigRequest(account!.Id, container, tag, null, root,
            null, StorageTier.Hot, StorageTier.Hot, null, null, null, false,
            100, 180, RetentionMode.EitherTriggers, 5_000_000, 100_000_000);
        var config = await (await client.PostAsJsonAsync("/api/backup-configs", configReq))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        return (account.Id, config!.Id, container);
    }

    [Fact]
    public async Task Repair_Fails_As_Busy_While_A_Restore_Is_Active()
    {
        var client = factory.CreateClient();
        var (accountId, configId, container) = await CreateConfigAsync(client, "repair-vs-restore");
        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();
        Assert.True(busy.TryAddReader(accountId, container, out _)); // a live restore
        try
        {
            var state = factory.Services.GetRequiredService<RepairRunner>()
                .Start(configId, null, CloudCheckLevel.ExistenceSize, null, cleanupOrphans: false);
            for (var i = 0; i < 100 && state.Status == RunStatus.Running; i++)
                await Task.Delay(100);

            Assert.Equal(RunStatus.Failed, state.Status);
            Assert.Equal("This backup is busy with another operation.", state.Error);
        }
        finally
        {
            busy.RemoveReader(accountId, container);
        }
    }

    [Fact]
    public async Task Restore_Fails_As_Busy_While_A_Repair_Is_Running()
    {
        var client = factory.CreateClient();
        var (accountId, configId, container) = await CreateConfigAsync(client, "restore-vs-repair");
        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();
        Assert.True(busy.TryAcquire(accountId, container, "Repairing"));
        try
        {
            var target = Path.Combine(Path.GetTempPath(), "asb-rex-t-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(target);
            var state = factory.Services.GetRequiredService<RestoreRunner>()
                .Start(configId, target, version: null);
            for (var i = 0; i < 100 && state.Status == RunStatus.Running; i++)
                await Task.Delay(100);

            Assert.Equal(RunStatus.Failed, state.Status);
            Assert.Contains("busy", state.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            busy.Release(accountId, container);
        }
    }

    // ---- Retention stands down while a restore reads ------------------------------------------------

    private sealed class CountingStore : IBackupInfoStore
    {
        public int InfoReads;
        public Task<BackupInfoFile?> ReadInfoAsync(Account a, string c, string? p, CancellationToken ct = default)
        {
            Interlocked.Increment(ref InfoReads);
            return Task.FromResult<BackupInfoFile?>(null);
        }
        public Task<(BackupInfoFile Info, string ETag)?> ReadInfoWithETagAsync(Account a, string c, string? p, CancellationToken ct = default) => Task.FromResult<(BackupInfoFile, string)?>(null);
        public Task WriteInfoAsync(Account a, string c, BackupInfoFile i, string? p, AccessTier? t = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> WriteInfoConditionalAsync(Account a, string c, BackupInfoFile i, string? p, AccessTier? t, string? e, CancellationToken ct = default) => Task.FromResult("etag");
        public Task<VersionIndex> ReadIndexAsync(Account a, string c, string i, string? p, int v = 1, CancellationToken ct = default) => Task.FromResult(new VersionIndex());
        public Task<(string Name, int Volumes)> WriteIndexAsync(Account a, string c, int v, VersionIndex i, string? p, AccessTier? t = null, CancellationToken ct = default) => Task.FromResult(("indexes/v.bin", 1));
    }

    [Fact]
    public async Task Retention_Cleanup_Skips_Entirely_While_A_Restore_Is_Active()
    {
        // A restore resolved its blob set from the version it is downloading; retiring that version and
        // deleting "unreferenced" blobs mid-download 404s the restore. The cleanup is periodic — skipping
        // this round costs nothing, so with readers active it must not even begin.
        var busy = new BackupBusyTracker();
        var store = new CountingStore();
        var cleaner = new RetentionCleaner(
            new BlobClientFactory(TestSecrets.Reader), store, new RetentionEvaluator(), busy: busy);
        var account = new Account { Id = 7, Name = "a", BlobEndpoint = "http://127.0.0.1:1", AccountKeyProtected = TestSecrets.Protect("k") };

        Assert.True(busy.TryAddReader(7, "held", out _));
        var report = await cleaner.CleanupAsync(account, "held", null,
            new CleanupOptions { Retention = new RetentionPolicy { MaxVersions = 1 } });

        Assert.True(report.IsEmpty);
        Assert.Equal(0, store.InfoReads); // stood down before touching anything
    }
}
