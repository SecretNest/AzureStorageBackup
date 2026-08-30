using System.Net.Http.Json;
using Azure.Storage.Blobs;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The startup sweep of restore-tmp/ deletes the Hot copies a restore makes of archived volumes. Its doc
/// used to claim "restores begin after startup, so they cannot collide" — but StartAsync detaches the sweep
/// and returns at once, and Kestrel is already serving /restore while the sweep is still listing: a restart
/// followed by an immediate archive restore could have its temp copy deleted out from under the download.
/// The sweeper therefore participates in the busy matrix per container: it holds "CleaningUp" (refusing to
/// start where a reader is active) for exactly the span of that container's sweep, and skips — the next
/// start retries — where it cannot.
/// </summary>
public sealed class RestoreTempSweeperTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private sealed class RecordingBlobFactory : IBlobClientFactory
    {
        public readonly List<int> Calls = [];
        public Action<Account>? OnCreate;

        public BlobServiceClient CreateServiceClient(Account account)
        {
            lock (Calls)
                Calls.Add(account.Id);
            OnCreate?.Invoke(account);
            // Swallowed by the sweep's per-container catch: this test is about the gate, not the cloud.
            throw new InvalidOperationException("no cloud in this test");
        }

        public Task<ConnectionResult> TestConnectionAsync(Account account, CancellationToken ct = default)
            => Task.FromResult(new ConnectionResult(true, null));
    }

    private async Task<(int AccountId, string Container)> CreateConfigAsync(string tag)
    {
        var client = factory.CreateClient();
        var accountReq = new AccountRequest("acct-" + tag, null,
            "https://t" + Guid.NewGuid().ToString("N")[..12] + ".blob.core.windows.net", AzureRegion.Global,
            "dGVzdGtleQ==", false, ProxyMode.Independent, null, null, null, null);
        var account = await (await client.PostAsJsonAsync("/api/accounts", accountReq))
            .Content.ReadFromJsonAsync<AccountResponse>();
        var root = Path.Combine(Path.GetTempPath(), "asb-rts-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var container = "rts-" + Guid.NewGuid().ToString("N")[..8];
        var configReq = new BackupConfigRequest(account!.Id, container, tag, null, root,
            null, StorageTier.Hot, StorageTier.Hot, null, null, null, false,
            100, 180, RetentionMode.EitherTriggers, 5_000_000, 100_000_000);
        await client.PostAsJsonAsync("/api/backup-configs", configReq);
        return (account.Id, container);
    }

    [Fact]
    public async Task The_Sweep_Skips_A_Container_With_An_Active_Reader()
    {
        var (accountId, container) = await CreateConfigAsync("reader-held");
        var busy = new BackupBusyTracker();
        Assert.True(busy.TryAddReader(accountId, container, out _)); // a restore already downloading

        var blobs = new RecordingBlobFactory();
        var sweeper = new RestoreTempSweeper(
            factory.Services.GetRequiredService<IServiceScopeFactory>(), blobs, busy: busy);
        await sweeper.SweepAllAsync(CancellationToken.None);

        Assert.DoesNotContain(accountId, blobs.Calls); // never even built a client for the held container
        Assert.False(busy.IsBusy(accountId, container)); // and left no mark behind
    }

    [Fact]
    public async Task The_Sweep_Holds_CleaningUp_For_The_Span_Of_A_Container_And_Releases_It()
    {
        var (accountId, container) = await CreateConfigAsync("unheld");
        var busy = new BackupBusyTracker();
        var blobs = new RecordingBlobFactory();
        string? duringActivity = null;
        bool? readerAdmittedDuring = null;
        blobs.OnCreate = account =>
        {
            if (account.Id != accountId)
                return;
            duringActivity = busy.CurrentActivity(accountId, container);
            readerAdmittedDuring = busy.TryAddReader(accountId, container, out _);
            if (readerAdmittedDuring == true)
                busy.RemoveReader(accountId, container);
        };
        var sweeper = new RestoreTempSweeper(
            factory.Services.GetRequiredService<IServiceScopeFactory>(), blobs, busy: busy);
        await sweeper.SweepAllAsync(CancellationToken.None);

        Assert.Contains(accountId, blobs.Calls);         // an unheld container is swept
        Assert.Equal("CleaningUp", duringActivity);      // as a rewriter, for the span of the work
        Assert.False(readerAdmittedDuring);              // so a restore starting mid-sweep is refused
        Assert.False(busy.IsBusy(accountId, container)); // and released when that container is done
    }
}
