using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Endpoints;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

public class BackupConfigEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly IServiceProvider _services = factory.Services;

    // AccountId defaults to 0: not a real account, so callers must explicitly pass the account id created by CreateAccountAsync
    // (otherwise, as of P2T7, they get stopped by "Account not found."). Keep 0/999999 only when the test really means to assert that refusal.
    private static BackupConfigRequest SampleRequest(string name = "photos", int accountId = 0) => new(
        AccountId: accountId,
        ContainerName: "photos",
        Name: name,
        Description: "family",
        LocalRoot: "/data/photos",
        Password: "s3cret",
        IndexTier: StorageTier.Hot,
        DataTier: StorageTier.Cool,
        IgnoreRules: "*.tmp",
        DontCompressRules: null,
        DontGroupRules: null,
        IncludeSymlinks: false,
        MaxVersions: 50,
        MaxAgeDays: 180,
        RetentionMode: RetentionMode.EitherTriggers,
        SingleFileThresholdBytes: 5_000_000,
        GroupCapBytes: 100_000_000);

    /// <summary>Creates a real account for tests that need to get past the "Account not found." gate.</summary>
    private async Task<int> CreateAccountAsync(string name)
    {
        var req = new AccountRequest(
            Name: "acct-" + name + "-" + Guid.NewGuid().ToString("N")[..6],
            Description: null,
            BlobEndpoint: "https://t" + Guid.NewGuid().ToString("N")[..12] + ".blob.core.windows.net",
            Region: AzureRegion.Global,
            AccountKey: "dGVzdGtleQ==",
            UseProxy: false,
            ProxyMode: ProxyMode.Independent,
            ProxyHost: null, ProxyPort: null, ProxyUsername: null, ProxyPassword: null);
        var res = await _client.PostAsJsonAsync("/api/accounts", req);
        var account = await res.Content.ReadFromJsonAsync<AccountResponse>();
        return account!.Id;
    }

    [Fact]
    public async Task Post_Creates_Config_And_Hides_Password()
    {
        var accountId = await CreateAccountAsync("post-creates");
        var res = await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest(accountId: accountId));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var created = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.True(created!.Id > 0);
        Assert.Equal("photos", created.Name);
        Assert.True(created.HasPassword); // it has an encryption password, but the plaintext is never returned

        var body = await (await _client.GetAsync($"/api/backup-configs/{created.Id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("s3cret", body);
    }

    [Fact]
    public async Task Post_Without_LocalRoot_Returns_400()
    {
        var accountId = await CreateAccountAsync("no-local-root");
        var res = await _client.PostAsJsonAsync("/api/backup-configs",
            SampleRequest(accountId: accountId) with { LocalRoot = "" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Post_Without_Name_Returns_400()
    {
        var accountId = await CreateAccountAsync("no-name");
        var res = await _client.PostAsJsonAsync("/api/backup-configs",
            SampleRequest(accountId: accountId) with { Name = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("Name is required.", body!["error"]);
    }

    [Fact]
    public async Task Post_With_Nonexistent_Account_Returns_400()
    {
        var res = await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest(accountId: 999_999));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("Account not found.", body!["error"]);
    }

    [Fact]
    public async Task Put_With_Empty_Password_Keeps_Existing()
    {
        var accountId = await CreateAccountAsync("keep-pw");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("keep-pw", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var res = await _client.PutAsJsonAsync($"/api/backup-configs/{created!.Id}",
            SampleRequest("keep-pw", accountId) with { Password = null, Name = "renamed" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var updated = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal("renamed", updated!.Name);
        Assert.True(updated.HasPassword); // password preserved
    }

    [Fact]
    public async Task Put_With_Blank_Name_Returns_400()
    {
        var accountId = await CreateAccountAsync("put-blank-name");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("put-blank-name", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var res = await _client.PutAsJsonAsync($"/api/backup-configs/{created!.Id}",
            SampleRequest("put-blank-name", accountId) with { Password = null, Name = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("Name is required.", body!["error"]);
    }

    [Fact]
    public async Task Delete_Removes_Config()
    {
        var accountId = await CreateAccountAsync("del");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("del", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/backup-configs/{created!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/backup-configs/{created.Id}")).StatusCode);
    }

    /// <summary>The audit line a deletion leaves behind must carry accountId in its source key. It was once in the pre-revamp format
    /// "backup:{container}": without the account dimension, the same container name under two accounts writes identical lines,
    /// and since the log page filters by exact source equality, the record that most deserves a trace cannot be found in either backup's view.
    /// This also pins down that it is written **after** the cleanup — written before, DeleteForContainerAsync would delete it along with everything else.</summary>
    [Fact]
    public async Task Delete_Records_The_Audit_Line_Under_The_Backups_Own_Source_Key()
    {
        var accountId = await CreateAccountAsync("del-audit");
        var container = "delaudit" + Guid.NewGuid().ToString("N")[..8];
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("del-audit", accountId) with { ContainerName = container }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.DeleteAsync($"/api/backup-configs/{created!.Id}")).StatusCode);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = Assert.Single(db.LogEntries.Where(e => e.Source == $"backup:{accountId}/{container}"));
        Assert.Equal(OperationLogLevel.Warning, entry.Level);
        Assert.False(entry.Ephemeral);          // audit: long-lived
        Assert.Contains("deleted", entry.Message);
    }

    /// <summary>Deleting a config does not stop the background run. Let it through and the run keeps going, keeps holding
    /// the (account, container) busy lock, while the progress state is keyed by config id — delete the config and it can never be queried again,
    /// so a newly created backup on the same container is refused as "busy" while the status says BackingUp with no detail at all.
    /// A user really hit this (and had also ticked "delete the container too", so that run went on uploading into a container that no longer
    /// existed). Hence a deletion while busy must be refused outright.</summary>
    [Fact]
    public async Task Delete_Is_Refused_While_An_Operation_Is_Running()
    {
        var accountId = await CreateAccountAsync("del-busy");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("del-busy", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var busy = _services.GetRequiredService<BackupBusyTracker>();
        Assert.True(busy.TryAcquire(accountId, created!.ContainerName, "BackingUp"));
        try
        {
            var refused = await _client.DeleteAsync($"/api/backup-configs/{created.Id}");
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var body = await refused.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            Assert.Contains("backing up", body!["error"]);
            // The config must still be there: a refusal that only half refuses is worse than not refusing at all.
            Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/backup-configs/{created.Id}")).StatusCode);
        }
        finally
        {
            busy.Release(accountId, created.ContainerName);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/backup-configs/{created.Id}")).StatusCode);
    }

    // Azurite's well-known account and key (same as the other integration tests, see BackupRunEndpointsTests).
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string AzuriteEndpoint = "http://127.0.0.1:10000/devstoreaccount1";

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Delete_Config_Optionally_Deletes_Cloud_Container()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var containerName = "del-" + Guid.NewGuid().ToString("N")[..8];

        // One endpoint, one record: adopt the Azurite account another test in this class registered.
        var account = await CreateAzuriteAccountAsync();

        var factoryClient = new BlobClientFactory(TestSecrets.Reader);
        var azuriteAccount = new Account { BlobEndpoint = AzuriteEndpoint, AccountKeyProtected = TestSecrets.Protect(AzuriteKey), Region = AzureRegion.Global };
        var cc = factoryClient.CreateServiceClient(azuriteAccount).GetBlobContainerClient(containerName);
        await cc.CreateIfNotExistsAsync();

        try
        {
            // deleteContainer=false (the default): the local config is deleted, the cloud container is still there.
            var config1 = await (await _client.PostAsJsonAsync("/api/backup-configs",
                    SampleRequest("del-keep") with { AccountId = account.Id, ContainerName = containerName }))
                .Content.ReadFromJsonAsync<BackupConfigResponse>();

            Assert.Equal(HttpStatusCode.NoContent,
                (await _client.DeleteAsync($"/api/backup-configs/{config1!.Id}?deleteContainer=false")).StatusCode);
            Assert.True((await cc.ExistsAsync()).Value);

            // deleteContainer=true: create another config pointing at the same container; deleting it removes the cloud container too.
            var config2 = await (await _client.PostAsJsonAsync("/api/backup-configs",
                    SampleRequest("del-purge") with { AccountId = account.Id, ContainerName = containerName }))
                .Content.ReadFromJsonAsync<BackupConfigResponse>();

            Assert.Equal(HttpStatusCode.NoContent,
                (await _client.DeleteAsync($"/api/backup-configs/{config2!.Id}?deleteContainer=true")).StatusCode);
            Assert.False((await cc.ExistsAsync()).Value);
        }
        finally
        {
            await cc.DeleteIfExistsAsync();
        }
    }

    /// <summary>P2T6 review follow-up: deleting a config also purges the local authoritative cache/state (CachedVersionIndex + LocalBackupState),
    /// otherwise rebuilding a backup on the same account+container hits orphan rows whose version identity does not match the new backup. The purge is
    /// scoped exactly to (accountId, container); rows under a different account or a different container must survive (no collateral damage).</summary>
    [Fact]
    public async Task Delete_Config_Purges_Local_Index_Cache_And_Local_Backup_State_Scoped_To_Account_Container()
    {
        var acctId = await CreateAccountAsync("del-cache");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("del-cache", acctId) with { ContainerName = "del-cache-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        var accountId = created!.AccountId;
        var container = created.ContainerName;
        var otherContainer = "del-cache-other-container";
        var otherAccountId = accountId + 999;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CachedVersionIndexes.Add(new CachedVersionIndex { AccountId = accountId, Container = container, Version = 1, IdentityTicks = 1, Bytes = [1] });
            db.LocalBackupStates.Add(new LocalBackupState { AccountId = accountId, Container = container, InfoBytes = [1], ETag = "e1" });
            // same account, different container → must survive
            db.CachedVersionIndexes.Add(new CachedVersionIndex { AccountId = accountId, Container = otherContainer, Version = 1, IdentityTicks = 1, Bytes = [1] });
            db.LocalBackupStates.Add(new LocalBackupState { AccountId = accountId, Container = otherContainer, InfoBytes = [1], ETag = "e2" });
            // different account, same container name → must survive
            db.CachedVersionIndexes.Add(new CachedVersionIndex { AccountId = otherAccountId, Container = container, Version = 1, IdentityTicks = 1, Bytes = [1] });
            db.LocalBackupStates.Add(new LocalBackupState { AccountId = otherAccountId, Container = container, InfoBytes = [1], ETag = "e3" });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/backup-configs/{created.Id}")).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.CachedVersionIndexes.AnyAsync(x => x.AccountId == accountId && x.Container == container));
            Assert.False(await db.LocalBackupStates.AnyAsync(x => x.AccountId == accountId && x.Container == container));
            Assert.True(await db.CachedVersionIndexes.AnyAsync(x => x.AccountId == accountId && x.Container == otherContainer));
            Assert.True(await db.LocalBackupStates.AnyAsync(x => x.AccountId == accountId && x.Container == otherContainer));
            Assert.True(await db.CachedVersionIndexes.AnyAsync(x => x.AccountId == otherAccountId && x.Container == container));
            Assert.True(await db.LocalBackupStates.AnyAsync(x => x.AccountId == otherAccountId && x.Container == container));
        }
    }

    [Fact]
    public async Task New_Config_Reports_Normal_Status_And_Idle_Activity()
    {
        var accountId = await CreateAccountAsync("status-idle");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("status-idle", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        Assert.Equal(BackupStatus.Normal, created!.Status);
        Assert.Null(created.LastError);
        Assert.Equal("Idle", created.Activity);

        var fetched = await (await _client.GetAsync($"/api/backup-configs/{created.Id}"))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal("Idle", fetched!.Activity);
    }

    [Fact]
    public async Task Busy_Config_Reports_Checking_Activity_In_List_And_Detail()
    {
        var accountId = await CreateAccountAsync("status-busy");
        var req = SampleRequest("status-busy", accountId) with { ContainerName = "busy-container" };
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", req))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        // Simulate the busy lock held by a check with no matching record in CheckRunner (e.g. a check started by a scheduled task):
        // DeriveActivity has to fall back to BackupBusyTracker to work out Checking.
        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();
        Assert.True(busy.TryAcquire(created!.AccountId, created.ContainerName));
        try
        {
            var list = await (await _client.GetAsync("/api/backup-configs"))
                .Content.ReadFromJsonAsync<List<BackupConfigResponse>>();
            Assert.Equal("Checking", list!.Single(c => c.Id == created.Id).Activity);

            var single = await (await _client.GetAsync($"/api/backup-configs/{created.Id}"))
                .Content.ReadFromJsonAsync<BackupConfigResponse>();
            Assert.Equal("Checking", single!.Activity);
        }
        finally
        {
            busy.Release(created.AccountId, created.ContainerName);
        }

        var afterRelease = await (await _client.GetAsync($"/api/backup-configs/{created.Id}"))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal("Idle", afterRelease!.Activity);
    }

    // The BackupActivity union type in the frontend's api/backupConfigs.ts ('Idle' | 'BackingUp' | 'Restoring' |
    // 'Checking' | 'Repairing' | 'CleaningUp') mirrors exactly these six strings, but there is no test for it over there.
    // Change one of these literals in the backend and both sides still compile — the frontend just quietly stops polling that kind.
    // This drives out all six branches of DeriveActivity one by one (BackingUp/Checking/CleaningUp/Repairing go through
    // BackupBusyTracker's fallback channel, sharing their source with the literals really used in TaskDispatcher.cs, BackupRunner.cs
    // and RepairRunner.cs; Restoring is the only one that does not take the busy lock — see the comment at the top of RestoreRunner.cs,
    // it can only be triggered by reflecting its own run state in), asserting on the literals, so a rename blows up loudly right here (Fix 8).
    [Fact]
    public async Task Activity_Strings_Match_The_Frontend_BackupActivity_Union()
    {
        var accountId = await CreateAccountAsync("activity-strings");
        var req = SampleRequest("activity-strings", accountId) with { ContainerName = "activity-strings-container" };
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", req))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        async Task<string> ActivityAsync() =>
            (await (await _client.GetAsync($"/api/backup-configs/{created!.Id}")).Content.ReadFromJsonAsync<BackupConfigResponse>())!.Activity;

        Assert.Equal("Idle", await ActivityAsync());

        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();

        // BackingUp/Checking/CleaningUp/Repairing: with no matching Runner record, DeriveActivity falls back to reading
        // BackupBusyTracker.CurrentActivity, and these literals are exactly the ones really passed by the switch in
        // TaskDispatcher.cs and by the TryAcquire calls in BackupRunner.cs / RepairRunner.cs.
        foreach (var label in new[] { "BackingUp", "Checking", "CleaningUp", "Repairing" })
        {
            Assert.True(busy.TryAcquire(created!.AccountId, created.ContainerName, label));
            try
            {
                Assert.Equal(label, await ActivityAsync());
            }
            finally
            {
                busy.Release(created.AccountId, created.ContainerName);
            }
        }

        Assert.Equal("Idle", await ActivityAsync());

        // Restoring does not take the busy lock (comment at the top of RestoreRunner.cs: restore can run in parallel with backup), so DeriveActivity
        // only looks at RestoreRunner's own run state — reflect straight into its private dictionary to fake a "running" restore,
        // rather than running a real restore just to trigger this one branch and depending on Azure/Azurite timing.
        var restoreRunner = factory.Services.GetRequiredService<RestoreRunner>();
        var runsField = typeof(RestoreRunner).GetField("_runs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var runs = (Dictionary<int, RestoreRunState>)runsField.GetValue(restoreRunner)!;
        runs[created!.Id] = new RestoreRunState { Status = RunStatus.Running };
        try
        {
            Assert.Equal("Restoring", await ActivityAsync());
        }
        finally
        {
            runs.Remove(created.Id);
        }

        Assert.Equal("Idle", await ActivityAsync());
    }

    [Fact]
    public async Task Failed_Operation_Sets_Error_And_Reset_Status_Clears_It()
    {
        var accountId = await CreateAccountAsync("status-error");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("status-error", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        // Simulate one of the runners persisting Error at its status-writing point because an operation failed (decision 2).
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IBackupConfigService>()
                .SetErrorAsync(created!.Id, "simulated failure");

        var afterFailure = await (await _client.GetAsync($"/api/backup-configs/{created!.Id}"))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal(BackupStatus.Error, afterFailure!.Status);
        Assert.Equal("simulated failure", afterFailure.LastError);
        Assert.NotNull(afterFailure.LastErrorAt);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.PostAsync($"/api/backup-configs/{created.Id}/reset-status", null)).StatusCode);

        var afterReset = await (await _client.GetAsync($"/api/backup-configs/{created.Id}"))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal(BackupStatus.Normal, afterReset!.Status);
        Assert.Null(afterReset.LastError);
    }

    [Fact]
    public async Task Reset_Status_On_Missing_Config_Returns_404()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PostAsync("/api/backup-configs/999999/reset-status", null)).StatusCode);
    }

    // ---- §5.8: /check, /repair, /versions, /file-versions, /unrecoverable, /tree, /restore-estimate ----

    private async Task<AccountResponse> CreateAzuriteAccountAsync()
    {
        var req = new AccountRequest("azurite-" + Guid.NewGuid().ToString("N")[..6], null, AzuriteEndpoint,
            AzureRegion.Global, AzuriteKey, false, ProxyMode.Independent, null, null, null, null);
        // One endpoint, one record: after the first test in this class registers Azurite, later ones adopt it.
        var id = await TestAccounts.EnsureAsync(_client, req);
        return (await _client.GetFromJsonAsync<List<AccountResponse>>("/api/accounts"))!.Single(a => a.Id == id);
    }

    /// <summary>Writes the local authoritative info file directly (when TrackedInfoStore.LoadAsync hits locally it never reads the cloud), so the /versions, /tree,
    /// /file-versions and /unrecoverable endpoints can be tested against local state without Azurite. Returns identityTicks (= Backup.CreatedAt.UtcTicks),
    /// which the /tree endpoint uses to match CachedVersionIndex.IdentityTicks.</summary>
    private long SeedLocalInfo(int accountId, string container, List<BackupVersion> versions)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var info = new BackupInfoFile
        {
            Backup = new BackupMeta { Name = "seed", CreatedAt = createdAt, Encrypted = false },
            Versions = versions,
        };
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.LocalBackupStates.Add(new LocalBackupState
        {
            AccountId = accountId, Container = container,
            InfoBytes = IndexSerializer.SerializeInfoFile(info), ETag = "seed-etag",
        });
        db.SaveChanges();
        return createdAt.UtcTicks;
    }

    private sealed record VersionSummary(int version, DateTimeOffset createdAt, long files, long bytes, long changedFiles);
    private sealed record VersionSpanRow(int version, DateTimeOffset createdAt, DateTimeOffset? startedAt);
    private sealed record FileVersionCandidate(int version, DateTimeOffset createdAt, long length);
    private sealed record RestoreEstimateResult(long downloadBytes, long uncompressedBytes, int fileCount, int archivedObjects, int rehydratePending);

    [Fact]
    public async Task Versions_Endpoint_Returns_Seeded_Version_Stats()
    {
        var account = await CreateAzuriteAccountAsync();
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("ep-versions") with { AccountId = account.Id, ContainerName = "ep-versions-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        SeedLocalInfo(account.Id, created!.ContainerName,
        [
            new BackupVersion
            {
                Version = 1, CreatedAt = DateTimeOffset.UtcNow, IndexBlob = "v1.index",
                Stats = new VersionStats(Files: 2, Bytes: 100, ChangedFiles: 2, ChangedBytes: 100),
            },
        ]);

        var res = await _client.GetAsync($"/api/backup-configs/{created.Id}/versions");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var rows = await res.Content.ReadFromJsonAsync<List<VersionSummary>>();
        var row = Assert.Single(rows!);
        Assert.Equal(1, row.version);
        Assert.Equal(2, row.files);
        Assert.Equal(100, row.bytes);
    }

    /// <summary>The restore dialog identifies versions by these two timestamps. Versions written before the upgrade have no start time and the endpoint honestly reports null,
    /// with the UI showing "—" — it does not pass off the previous version's finish time as a substitute.</summary>
    [Fact]
    public async Task Versions_Endpoint_Exposes_Start_Time_And_Null_For_Legacy_Versions()
    {
        var account = await CreateAzuriteAccountAsync();
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("ep-vspan") with { AccountId = account.Id, ContainerName = "ep-vspan-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var started = new DateTimeOffset(2026, 8, 2, 14, 3, 0, TimeSpan.Zero);
        var finished = new DateTimeOffset(2026, 8, 2, 14, 47, 0, TimeSpan.Zero);
        SeedLocalInfo(account.Id, created!.ContainerName,
        [
            new BackupVersion
            {
                Version = 1, CreatedAt = finished.AddDays(-1), IndexBlob = "v1.index",  // written before the upgrade: no StartedAt
                Stats = new VersionStats(1, 10, 1, 10),
            },
            new BackupVersion
            {
                Version = 2, CreatedAt = finished, StartedAt = started, IndexBlob = "v2.index",
                Stats = new VersionStats(2, 20, 1, 10),
            },
        ]);

        var rows = await _client.GetFromJsonAsync<List<VersionSpanRow>>(
            $"/api/backup-configs/{created.Id}/versions");

        Assert.NotNull(rows);
        Assert.Null(rows.Single(r => r.version == 1).startedAt);
        Assert.Equal(started, rows.Single(r => r.version == 2).startedAt);
        Assert.Equal(finished, rows.Single(r => r.version == 2).createdAt);
    }

    /// <summary>In the version dropdown for restore/check, everything after "Latest" must run newest to oldest — the most likely pick is closest to hand.
    /// The endpoint returns them in descending version order, consistent with the "nearest first" ordering of /file-versions.</summary>
    [Fact]
    public async Task Versions_Endpoint_Returns_Newest_First()
    {
        var account = await CreateAzuriteAccountAsync();
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("ep-vorder") with { AccountId = account.Id, ContainerName = "ep-vorder-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        SeedLocalInfo(account.Id, created!.ContainerName,
        [
            new BackupVersion { Version = 1, CreatedAt = DateTimeOffset.UtcNow.AddDays(-3), IndexBlob = "v1.index", Stats = new VersionStats(1, 10, 1, 10) },
            new BackupVersion { Version = 2, CreatedAt = DateTimeOffset.UtcNow.AddDays(-2), IndexBlob = "v2.index", Stats = new VersionStats(2, 20, 1, 10) },
            new BackupVersion { Version = 3, CreatedAt = DateTimeOffset.UtcNow.AddDays(-1), IndexBlob = "v3.index", Stats = new VersionStats(3, 30, 1, 10) },
        ]);

        var rows = await _client.GetFromJsonAsync<List<VersionSummary>>(
            $"/api/backup-configs/{created.Id}/versions");

        Assert.Equal([3, 2, 1], rows!.Select(r => r.version));
    }

    [Fact]
    public async Task Tree_Endpoint_Returns_Root_Children()
    {
        var account = await CreateAzuriteAccountAsync();
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("ep-tree") with { AccountId = account.Id, ContainerName = "ep-tree-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var identityTicks = SeedLocalInfo(account.Id, created!.ContainerName,
        [
            new BackupVersion
            {
                Version = 1, CreatedAt = DateTimeOffset.UtcNow, IndexBlob = "v1.index",
                Stats = new VersionStats(1, 5, 1, 5),
            },
        ]);

        var index = new VersionIndex
        {
            Version = 1,
            Entries =
            [
                new IndexEntry
                {
                    Path = "a.txt", Kind = "file", Length = 5, Mtime = DateTimeOffset.UtcNow, Permissions = "644",
                    Storage = new StorageRef { Kind = "blob", Ref = "data/abc" },
                },
            ],
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CachedVersionIndexes.Add(new CachedVersionIndex
            {
                AccountId = account.Id, Container = created.ContainerName, Version = 1,
                IdentityTicks = identityTicks, Bytes = IndexSerializer.SerializeIndex(index),
            });
            await db.SaveChangesAsync();
        }

        var res = await _client.GetAsync($"/api/backup-configs/{created.Id}/tree?version=1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var nodes = await res.Content.ReadFromJsonAsync<List<TreeNode>>();
        var node = Assert.Single(nodes!);
        Assert.Equal("a.txt", node.Name);
        Assert.False(node.IsDir);
        Assert.Equal(5, node.Length);

        // Requesting a version that does not exist → 200 with an empty array (same as /unrecoverable and /file-versions, not 404).
        var missingVer = await _client.GetAsync($"/api/backup-configs/{created.Id}/tree?version=999");
        Assert.Equal(HttpStatusCode.OK, missingVer.StatusCode);
        Assert.Empty((await missingVer.Content.ReadFromJsonAsync<List<TreeNode>>())!);
    }

    /// <summary>
    /// Endpoints that read version indexes must go through the **local authoritative cache** and must not read the cloud at runtime
    /// (core design principle: the local index is authoritative, zero cloud reads at runtime). /tree was always right, while /unrecoverable,
    /// /file-versions and /unreadable all called IBackupInfoStore.ReadIndexAsync directly — opening the restore dialog meant at least two
    /// cloud index downloads, and /file-versions did **one per version**: on top of the latency that is real Azure egress traffic charges,
    /// while an authoritative copy is lying right there locally.
    /// <para>
    /// The criterion is clean: this test's container **does not exist at all** on Azurite, yet the local cache holds a complete index.
    /// Going local, all three endpoints return the correct content; going to the cloud they are bound to fail. Before the fix, /unrecoverable and /unreadable
    /// returned 500 and /file-versions returned empty.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Index_Reading_Endpoints_Use_The_Local_Cache_Not_The_Cloud()
    {
        var account = await CreateAzuriteAccountAsync();
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("ep-nocloud") with { AccountId = account.Id, ContainerName = "ep-nocloud-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var identityTicks = SeedLocalInfo(account.Id, created!.ContainerName,
        [
            new BackupVersion
            {
                Version = 1, CreatedAt = DateTimeOffset.UtcNow, IndexBlob = "v1.index",
                Stats = new VersionStats(2, 10, 2, 10),
            },
        ]);

        var stale = new DateTimeOffset(2026, 7, 20, 8, 30, 0, TimeSpan.Zero);
        var index = new VersionIndex
        {
            Version = 1,
            Entries =
            [
                new IndexEntry
                {
                    Path = "carried.txt", Kind = "file", Length = 5, Mtime = DateTimeOffset.UtcNow,
                    Permissions = "644", UnreadableAt = stale,
                    Storage = new StorageRef { Kind = "blob", Ref = "data/abc" },
                },
                new IndexEntry
                {
                    Path = "broken.txt", Kind = "file", Length = 5, Mtime = DateTimeOffset.UtcNow,
                    Permissions = "644", Storage = new StorageRef { Kind = "blob", Ref = "data/def" },
                },
            ],
            UnrecoverablePaths = ["broken.txt"],
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CachedVersionIndexes.Add(new CachedVersionIndex
            {
                AccountId = account.Id, Container = created.ContainerName, Version = 1,
                IdentityTicks = identityTicks, Bytes = IndexSerializer.SerializeIndex(index),
            });
            await db.SaveChangesAsync();
        }

        // /unreadable — the cloud has no such container, so returning content proves it read the local cache.
        var unread = await _client.GetAsync($"/api/backup-configs/{created.Id}/unreadable?version=1");
        Assert.Equal(HttpStatusCode.OK, unread.StatusCode);
        var unreadRow = Assert.Single((await unread.Content.ReadFromJsonAsync<List<UnreadableRow>>())!);
        Assert.Equal("carried.txt", unreadRow.path);
        Assert.Equal(stale, unreadRow.unreadableAt);

        // /unrecoverable
        var unrec = await _client.GetAsync($"/api/backup-configs/{created.Id}/unrecoverable?version=1");
        Assert.Equal(HttpStatusCode.OK, unrec.StatusCode);
        Assert.Equal(["broken.txt"], (await unrec.Content.ReadFromJsonAsync<List<string>>())!);

        // /file-versions — the loop reads one index per version, so it is the one that most needs to go local.
        var fv = await _client.GetAsync(
            $"/api/backup-configs/{created.Id}/file-versions?path={Uri.EscapeDataString("carried.txt")}");
        Assert.Equal(HttpStatusCode.OK, fv.StatusCode);
        var candidate = Assert.Single((await fv.Content.ReadFromJsonAsync<List<FileVersionCandidate>>())!);
        Assert.Equal(1, candidate.version);

        // A path marked unrecoverable must not show up among the substitution candidates (reading locally did not lose that existing semantic).
        var fvBroken = await _client.GetAsync(
            $"/api/backup-configs/{created.Id}/file-versions?path={Uri.EscapeDataString("broken.txt")}");
        Assert.Empty((await fvBroken.Content.ReadFromJsonAsync<List<FileVersionCandidate>>())!);
    }

    [Fact]
    public async Task File_Versions_And_Unrecoverable_Return_Empty_Array_When_No_Versions_Exist()
    {
        var account = await CreateAzuriteAccountAsync();
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("ep-fv-empty") with { AccountId = account.Id, ContainerName = "ep-fv-empty-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        SeedLocalInfo(account.Id, created!.ContainerName, []); // no versions → both endpoints short-circuit and never touch the cloud

        var fv = await _client.GetFromJsonAsync<List<FileVersionCandidate>>(
            $"/api/backup-configs/{created.Id}/file-versions?path=a.txt");
        Assert.Empty(fv!);

        var unrec = await _client.GetFromJsonAsync<List<string>>($"/api/backup-configs/{created.Id}/unrecoverable");
        Assert.Empty(unrec!);

        // /unreadable is shaped like /unrecoverable: with no versions it also short-circuits to 200 with an empty array and never touches the cloud.
        var unread = await _client.GetFromJsonAsync<List<UnreadableRow>>($"/api/backup-configs/{created.Id}/unreadable");
        Assert.Empty(unread!);
    }

    private sealed record UnreadableRow(string path, DateTimeOffset unreadableAt);

    private sealed record HashFileRow(string path, int local, bool repairable, bool grown);

    private sealed record PlanRow(string path, string? @ref, string action, bool grown, long uploadBytes);
    private sealed record PlanResponse(
        int version, List<PlanRow> rows, int reuploadObjects, long reuploadBytes, int unrecoverableCount, int grownCount);

    /// <summary>The cost of a repair must be on the table before anything runs: the plan is built from the last
    /// check report, the local index and a stat per file — no cloud request and not a byte read, so it answers
    /// instantly even when every problem file is 100 GB. Hashing belongs inside the repair the user then
    /// confirms, never before consent.</summary>
    [Fact]
    public async Task Repair_Plan_Prices_The_Repair_From_Stats_Alone()
    {
        var accountId = await CreateAccountAsync("repair-plan");
        var root = Path.Combine(Path.GetTempPath(), "asb-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                    SampleRequest("repair-plan", accountId) with { ContainerName = "repair-plan-container", LocalRoot = root }))
                .Content.ReadFromJsonAsync<BackupConfigResponse>();

            // Three problem files, one of each fate: unchanged (same length → re-upload), grown (longer →
            // prefix-rebuild candidate), rewritten shorter (→ unrecoverable).
            await File.WriteAllTextAsync(Path.Combine(root, "same.bin"), "0123456789");
            await File.WriteAllTextAsync(Path.Combine(root, "grown.bin"), "0123456789ABCDEF");
            await File.WriteAllTextAsync(Path.Combine(root, "shrunk.bin"), "0123");

            var identity = SeedLocalInfo(accountId, created!.ContainerName,
                [new BackupVersion
                {
                    Version = 9, IndexBlob = "indexes/9", CreatedAt = DateTimeOffset.UtcNow,
                    Stats = new VersionStats(3, 30, 3, 30),
                }]);
            using (var scope = factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<ILocalIndexCache>().PutAsync(
                    accountId, created.ContainerName, 9, identity,
                    new VersionIndex
                    {
                        Version = 9,
                        Entries =
                        [
                            new IndexEntry
                            {
                                Path = "same.bin", Kind = "file", Permissions = "0644", Length = 10, FullHash = "h1",
                                Storage = new StorageRef { Kind = "blob", Ref = "data/s", Volumes = 2, VolumeSizes = [7, 3] },
                            },
                            new IndexEntry
                            {
                                Path = "grown.bin", Kind = "file", Permissions = "0644", Length = 10, FullHash = "h2",
                                Storage = new StorageRef { Kind = "blob", Ref = "data/g", Volumes = 1, VolumeSizes = [8] },
                            },
                            new IndexEntry
                            {
                                Path = "shrunk.bin", Kind = "file", Permissions = "0644", Length = 10, FullHash = "h3",
                                Storage = new StorageRef { Kind = "blob", Ref = "data/k", Volumes = 1, VolumeSizes = [6] },
                            },
                        ],
                    });

            await factory.Services.GetRequiredService<CheckRunner>().PersistAsync(created.Id, new CheckRunState
            {
                Status = RunStatus.Completed,
                Report = new CheckReport(9,
                [
                    new FileFinding("same.bin", "data/s", CloudState.MissingOrBad, LocalState.NotChecked),
                    new FileFinding("grown.bin", "data/g", CloudState.MissingOrBad, LocalState.NotChecked),
                    new FileFinding("shrunk.bin", "data/k", CloudState.MissingOrBad, LocalState.NotChecked),
                    new FileFinding("fine.bin", "data/f", CloudState.Ok, LocalState.NotChecked),
                ]),
            });

            var plan = await _client.GetFromJsonAsync<PlanResponse>(
                $"/api/backup-configs/{created.Id}/repair-plan");

            Assert.Equal(9, plan!.version);
            Assert.Equal("reupload", plan.rows.Single(r => r.path == "same.bin").action);
            Assert.Equal(10, plan.rows.Single(r => r.path == "same.bin").uploadBytes); // 7 + 3 recorded volume bytes
            var grown = plan.rows.Single(r => r.path == "grown.bin");
            Assert.Equal("grown", grown.action);
            Assert.True(grown.grown);
            Assert.Equal("unrecoverable", plan.rows.Single(r => r.path == "shrunk.bin").action);
            Assert.DoesNotContain(plan.rows, r => r.path == "fine.bin");
            Assert.Equal(1, plan.reuploadObjects);
            Assert.Equal(10, plan.reuploadBytes);
            Assert.Equal(1, plan.unrecoverableCount);
            Assert.Equal(1, plan.grownCount);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>The in-memory report already survives closing the dialog; this pins that it survives the
    /// process. The host in this test has never run a check, so its runner's memory is exactly a freshly
    /// restarted container's — the GET must come back from the persisted row.</summary>
    [Fact]
    public async Task A_Persisted_Check_Report_Survives_A_Restart()
    {
        var accountId = await CreateAccountAsync("check-persist");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("check-persist", accountId) with { ContainerName = "check-persist-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var runner = factory.Services.GetRequiredService<CheckRunner>();
        await runner.PersistAsync(created!.Id, new CheckRunState
        {
            Status = RunStatus.Completed,
            Report = new CheckReport(9,
                [new FileFinding("a.bin", "data/x", CloudState.MissingOrBad, LocalState.NotChecked)]),
        });

        var run = await (await _client.GetAsync($"/api/backup-configs/{created.Id}/check"))
            .Content.ReadFromJsonAsync<CheckRunResponse>();
        Assert.Equal("Completed", run!.Status);
        Assert.Equal(9, run.Report!.Version);
        Assert.Equal("a.bin", Assert.Single(run.Report.Findings).Path);
    }

    /// <summary>Dropping the last result is the doorway back to "start a new check" (the dialog shows the
    /// result for as long as one exists): the persisted row goes too, or reopening the dialog would resurrect
    /// what the user just dismissed.</summary>
    [Fact]
    public async Task Dropping_The_Last_Check_Result_Clears_It_For_Good()
    {
        var accountId = await CreateAccountAsync("check-drop");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("check-drop", accountId) with { ContainerName = "check-drop-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var runner = factory.Services.GetRequiredService<CheckRunner>();
        await runner.PersistAsync(created!.Id, new CheckRunState
        {
            Status = RunStatus.Completed,
            // Actionable: a clean report never persists in the first place (it retires itself).
            Report = new CheckReport(9,
                [new FileFinding("bad.bin", "data/x", CloudState.MissingOrBad, LocalState.NotChecked)]),
        });

        var drop = await _client.DeleteAsync($"/api/backup-configs/{created.Id}/check");
        Assert.Equal(HttpStatusCode.NoContent, drop.StatusCode);

        // 204 from the GET = there is no check to report — the dialog lands on the start view.
        var after = await _client.GetAsync($"/api/backup-configs/{created.Id}/check");
        Assert.Equal(HttpStatusCode.NoContent, after.StatusCode);
    }

    /// <summary>A clean verdict retires itself instead of persisting: the persisted report GATES every
    /// further check ("有报告就修,没报告才能查"), and a clean one would close that gate forever with nothing to
    /// act on. Only reports with problems (or orphans) hold the gate.</summary>
    [Fact]
    public async Task A_Clean_Report_Retires_Itself_And_Gates_Nothing()
    {
        var accountId = await CreateAccountAsync("check-clean");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("check-clean", accountId) with { ContainerName = "check-clean-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var runner = factory.Services.GetRequiredService<CheckRunner>();
        await runner.PersistAsync(created!.Id, new CheckRunState
        {
            Status = RunStatus.Completed,
            Report = new CheckReport(9, [new FileFinding("fine.bin", "data/x", CloudState.Ok, LocalState.NotChecked)]),
        });

        Assert.False(await runner.HasPersistedReportAsync(created.Id));
        var list = await _client.GetFromJsonAsync<List<BackupConfigResponse>>("/api/backup-configs");
        Assert.False(list!.Single(c => c.Id == created.Id).HasCheckReport);
    }

    /// <summary>With an actionable report persisted, a new check is refused — the report is the plan a repair
    /// works from, and quietly replacing it is how selections go stale. The row exposes the state so the UI
    /// can show the red Repair instead of Check.</summary>
    [Fact]
    public async Task A_Pending_Report_Gates_New_Checks()
    {
        var accountId = await CreateAccountAsync("check-gate");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("check-gate", accountId) with { ContainerName = "check-gate-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var runner = factory.Services.GetRequiredService<CheckRunner>();
        await runner.PersistAsync(created!.Id, new CheckRunState
        {
            Status = RunStatus.Completed,
            Report = new CheckReport(9,
                [new FileFinding("bad.bin", "data/x", CloudState.MissingOrBad, LocalState.NotChecked)]),
        });

        var refused = await _client.PostAsync($"/api/backup-configs/{created.Id}/check", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var list = await _client.GetFromJsonAsync<List<BackupConfigResponse>>("/api/backup-configs");
        Assert.True(list!.Single(c => c.Id == created.Id).HasCheckReport);

        // Dropping reopens the door.
        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.DeleteAsync($"/api/backup-configs/{created.Id}/check")).StatusCode);
        Assert.False(await runner.HasPersistedReportAsync(created.Id));
    }

    /// <summary>The persisted row answers "what did the last finished check find", not "what happened most
    /// recently": a failed run (here, a busy click) carries no report and must leave the row untouched.</summary>
    [Fact]
    public async Task A_Failed_Run_Does_Not_Clobber_The_Persisted_Report()
    {
        var accountId = await CreateAccountAsync("check-noclobber");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("check-noclobber", accountId) with { ContainerName = "check-noclobber-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var runner = factory.Services.GetRequiredService<CheckRunner>();
        await runner.PersistAsync(created!.Id, new CheckRunState
        {
            Status = RunStatus.Completed,
            // Actionable, or it would retire itself instead of persisting.
            Report = new CheckReport(9,
                [new FileFinding("bad.bin", "data/x", CloudState.MissingOrBad, LocalState.NotChecked)]),
        });

        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();
        Assert.True(busy.TryAcquire(created.AccountId, created.ContainerName));
        try
        {
            await _client.PostAsync($"/api/backup-configs/{created.Id}/check", null);
            for (var i = 0; i < 200; i++)
            {
                var r = await (await _client.GetAsync($"/api/backup-configs/{created.Id}/check"))
                    .Content.ReadFromJsonAsync<CheckRunResponse>();
                if (r!.Status != "Running") break;
                await Task.Delay(25);
            }
        }
        finally
        {
            busy.Release(created.AccountId, created.ContainerName);
        }

        using var scope = factory.Services.CreateScope();
        var row = scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .LastCheckRuns.Single(x => x.BackupConfigId == created.Id);
        Assert.Contains("\"Version\":9", row.ReportJson);
    }

    /// <summary>The targeted follow-up to a cloud-only check: a check run without a content-level local pass
    /// cannot say whether a damaged blob is repairable from local, and hashing the whole tree to find out for
    /// four files is out of proportion. This endpoint hashes ONE path against the version's recorded content —
    /// with a length shortcut, so an appended 100 GB file answers "changed" without reading a byte.</summary>
    [Fact]
    public async Task Hash_File_Answers_Repairability_For_One_Path()
    {
        var accountId = await CreateAccountAsync("hash-file");
        var root = Path.Combine(Path.GetTempPath(), "asb-hashfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                    SampleRequest("hash-file", accountId) with { ContainerName = "hash-file-container", LocalRoot = root }))
                .Content.ReadFromJsonAsync<BackupConfigResponse>();

            var file = Path.Combine(root, "a.bin");
            await File.WriteAllTextAsync(file, "the recorded content");
            var hash = await new FileHasher().FullHashAsync(file);
            var identity = SeedLocalInfo(accountId, created!.ContainerName,
                [new BackupVersion
                {
                    Version = 9, IndexBlob = "indexes/9", CreatedAt = DateTimeOffset.UtcNow,
                    Stats = new VersionStats(1, 20, 1, 20),
                }]);
            using (var scope = factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<ILocalIndexCache>().PutAsync(
                    accountId, created.ContainerName, 9, identity,
                    new VersionIndex
                    {
                        Version = 9,
                        Entries =
                        [
                            new IndexEntry
                            {
                                Path = "a.bin", Kind = "file", Length = new FileInfo(file).Length,
                                Permissions = "0644", FullHash = hash,
                            },
                        ],
                    });

            var url = $"/api/backup-configs/{created.Id}/hash-file?version=9&path=a.bin";

            // Unchanged content → repairable.
            var ok = await _client.GetFromJsonAsync<HashFileRow>(url);
            Assert.Equal((int)LocalState.Ok, ok!.local);
            Assert.True(ok.repairable);

            // Appended → changed; the length answers it without reading the content, and the growth is
            // surfaced so the UI can point at repair's opt-in prefix recovery.
            await File.AppendAllTextAsync(file, "!");
            var changedRow = await _client.GetFromJsonAsync<HashFileRow>(url);
            Assert.Equal((int)LocalState.Changed, changedRow!.local);
            Assert.False(changedRow.repairable);
            Assert.True(changedRow.grown);

            // Gone → missing.
            File.Delete(file);
            var missingRow = await _client.GetFromJsonAsync<HashFileRow>(url);
            Assert.Equal((int)LocalState.Missing, missingRow!.local);
            Assert.False(missingRow.repairable);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Now that the check is a background job, busy is no longer a synchronous 409: the POST only gets the run started,
    /// and the conflict has to surface in the **run state** (the same convention as /repair).</summary>
    [Fact]
    public async Task Check_Endpoint_Reports_Busy_Through_The_Run_State()
    {
        var account = await CreateAzuriteAccountAsync();
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("ep-check-busy") with { AccountId = account.Id, ContainerName = "ep-check-busy-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();
        Assert.True(busy.TryAcquire(created!.AccountId, created.ContainerName));
        try
        {
            var res = await _client.PostAsync($"/api/backup-configs/{created.Id}/check", null);
            Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

            CheckRunResponse? run = null;
            for (var i = 0; i < 200; i++)
            {
                run = await (await _client.GetAsync($"/api/backup-configs/{created.Id}/check"))
                    .Content.ReadFromJsonAsync<CheckRunResponse>();
                if (run!.Status != "Running") break;
                await Task.Delay(25);
            }
            Assert.Equal("Failed", run!.Status);
            Assert.Contains("busy", run.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.Null(run.Report);
        }
        finally
        {
            busy.Release(created.AccountId, created.ContainerName);
        }
    }

    /// <summary>With no check ever run, answer 204 rather than 404: the dialog asks once as soon as it opens, and a 404 leaves
    /// a red error in the browser console that looks like a malfunction.</summary>
    [Fact]
    public async Task Check_Status_Endpoint_Is_204_Until_A_Check_Has_Been_Started()
    {
        var accountId = await CreateAccountAsync("check-never-run");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("check-never-run", accountId) with { ContainerName = "check-never-run-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.GetAsync($"/api/backup-configs/{created!.Id}/check")).StatusCode);
    }

    /// <summary>Stopping: before this, the only way to stop a backup that had been running for hours was to restart the container. Stop per operation rather than
    /// stopping everything with one button — backup and restore can run concurrently, and accidentally stopping the other one is just as many hours lost.</summary>
    [Fact]
    public async Task Cancel_Endpoint_Signals_Only_The_Requested_Operation()
    {
        var accountId = await CreateAccountAsync("cancel-dispatch");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("cancel-dispatch", accountId) with { ContainerName = "cancel-dispatch-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        // Nothing running → 409: distinguishable from "we stopped, but nothing was actually stopped", so the UI can report it honestly.
        var idle = await _client.PostAsync($"/api/backup-configs/{created!.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, idle.StatusCode);

        // Stuff a Running record straight into both runners' private dictionaries (the same trick as the Activity_Strings test):
        // running a real backup/restore just to trigger the cancel dispatch would chain this test to Azurite's timing.
        var backupRunner = factory.Services.GetRequiredService<BackupRunner>();
        var restoreRunner = factory.Services.GetRequiredService<RestoreRunner>();
        var backupState = new BackupRunState { Status = RunStatus.Running };
        var restoreState = new RestoreRunState { Status = RunStatus.Running };
        // This bare state never went through RunCoreAsync, so nobody will ever call Completion.TrySetResult() on it.
        // The backup branch of /cancel now waits for Completion to settle (StopAndWaitAsync), giving up only after 20 seconds —
        // without this extra step, a dispatch test that should take milliseconds would sit here burning the full cap, with the two cancel calls eating 40 seconds between them.
        // Hook it onto Cancellation's token: for a bare state with no Control, RequestStop takes exactly the
        // `state.Cancellation.Cancel()` branch, and the callback fires synchronously with it, equivalent to the real wind-down moment.
        // To test the "genuinely did not settle" branch honestly, see Suspend_Does_Not_Settle_Within_The_Cap and
        // Cancel_Does_Not_Settle_Within_The_Cap.
        backupState.Cancellation.Token.Register(() => backupState.Completion.TrySetResult());
        InjectRun(backupRunner, created.Id, backupState);
        InjectRun(restoreRunner, created.Id, restoreState);
        try
        {
            var res = await _client.PostAsync($"/api/backup-configs/{created.Id}/cancel?what=backup", null);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<CanceledBody>();
            Assert.Equal(["backup"], body!.canceled);
            Assert.False(body.stopping);   // it settled fast enough not to hit the 20-second cap

            Assert.True(backupState.Cancellation.IsCancellationRequested);
            // A concurrent restore must not get stopped along the way.
            Assert.False(restoreState.Cancellation.IsCancellationRequested);

            // Without what → stop every operation running on this config.
            var all = await _client.PostAsync($"/api/backup-configs/{created.Id}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, all.StatusCode);
            Assert.True(restoreState.Cancellation.IsCancellationRequested);
        }
        finally
        {
            RemoveRun(backupRunner, created.Id);
            RemoveRun(restoreRunner, created.Id);
        }
    }

    [Fact]
    public async Task Cancel_On_Missing_Config_Returns_404()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PostAsync("/api/backup-configs/999999/cancel", null)).StatusCode);
    }

    /// <summary>If it has not settled, say honestly that it is "still stopping" — do not pretend it already stopped.
    /// A genuinely unsettled run is produced with a bare run state on which Completion.TrySetResult() is never called:
    /// the suspend request went out (Cancellation is set) but nobody winds it down. Turning <see cref="BackupConfigEndpoints.StopWaitCap"/>
    /// down from production's 20 seconds to a few dozen milliseconds asserts the very same timeout branch without really waiting 20 seconds.</summary>
    [Fact]
    public async Task Suspend_Does_Not_Settle_Within_The_Cap()
    {
        var accountId = await CreateAccountAsync("suspend-timeout");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("st", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var backupRunner = factory.Services.GetRequiredService<BackupRunner>();
        var backupState = new BackupRunState { Status = RunStatus.Running };  // Completion never settles
        InjectRun(backupRunner, created!.Id, backupState);
        var original = BackupConfigEndpoints.StopWaitCap;
        BackupConfigEndpoints.StopWaitCap = TimeSpan.FromMilliseconds(50);
        try
        {
            var res = await _client.PostAsync($"/api/backup-configs/{created.Id}/suspend", null);
            Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<StoppingBody>();
            Assert.True(body!.stopping);

            // A timeout does not mean it did not stop: the request really did go out.
            Assert.True(backupState.Cancellation.IsCancellationRequested);
        }
        finally
        {
            BackupConfigEndpoints.StopWaitCap = original;
            RemoveRun(backupRunner, created.Id);
        }
    }

    /// <summary>Same as above, but through /cancel. This branch deliberately stays at 200 OK instead of dropping to 202 the way /suspend does —
    /// see the commit message of f96866c: on /cancel the Settled and StillStopping outcomes share the same
    /// <c>Results.Ok</c>, and the only thing telling them apart is the stopping field in the body.</summary>
    [Fact]
    public async Task Cancel_Does_Not_Settle_Within_The_Cap()
    {
        var accountId = await CreateAccountAsync("cancel-timeout");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("ct", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var backupRunner = factory.Services.GetRequiredService<BackupRunner>();
        var backupState = new BackupRunState { Status = RunStatus.Running };  // Completion never settles
        InjectRun(backupRunner, created!.Id, backupState);
        var original = BackupConfigEndpoints.StopWaitCap;
        BackupConfigEndpoints.StopWaitCap = TimeSpan.FromMilliseconds(50);
        try
        {
            var res = await _client.PostAsync($"/api/backup-configs/{created.Id}/cancel?what=backup", null);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);   // not 202 — /cancel does not use it
            var body = await res.Content.ReadFromJsonAsync<CanceledBody>();
            Assert.Equal(["backup"], body!.canceled);
            Assert.True(body.stopping);

            Assert.True(backupState.Cancellation.IsCancellationRequested);
        }
        finally
        {
            BackupConfigEndpoints.StopWaitCap = original;
            RemoveRun(backupRunner, created.Id);
        }
    }

    private sealed record CanceledBody(List<string> canceled, bool stopping);

    private sealed record StoppingBody(bool stopping);

    private static Dictionary<int, TState> RunsOf<TRunner, TState>(TRunner runner) where TRunner : notnull =>
        (Dictionary<int, TState>)typeof(TRunner)
            .GetField("_runs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(runner)!;

    private static void InjectRun<TRunner, TState>(TRunner runner, int configId, TState state) where TRunner : notnull =>
        RunsOf<TRunner, TState>(runner)[configId] = state;

    private static void RemoveRun<TRunner>(TRunner runner, int configId) where TRunner : notnull =>
        ((System.Collections.IDictionary)typeof(TRunner)
            .GetField("_runs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(runner)!).Remove(configId);

    [Fact]
    public async Task Read_And_Action_Endpoints_On_Missing_Config_Return_404()
    {
        const int missingId = 999999;
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/backup-configs/{missingId}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/backup-configs/{missingId}/file-versions?path=a.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/backup-configs/{missingId}/unrecoverable")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/backup-configs/{missingId}/unreadable")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/backup-configs/{missingId}/tree")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/backup-configs/{missingId}/check", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/backup-configs/{missingId}/repair", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PostAsJsonAsync($"/api/backup-configs/{missingId}/restore-estimate",
                new RestoreEstimateRequestBody(null, []))).StatusCode);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Check_Repair_RestoreEstimate_FileVersions_Unrecoverable_Endpoints_Work_After_Backup()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var localRoot = Path.Combine(Path.GetTempPath(), "asb-ep-cre-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localRoot);
        await File.WriteAllTextAsync(Path.Combine(localRoot, "a.txt"), "alpha");

        var containerName = "ep-cre-" + Guid.NewGuid().ToString("N")[..8];
        var account = await CreateAzuriteAccountAsync();
        var config = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("ep-cre") with { AccountId = account.Id, ContainerName = containerName, LocalRoot = localRoot }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var factoryClient = new BlobClientFactory(TestSecrets.Reader);
        var azurite = new Account { BlobEndpoint = AzuriteEndpoint, AccountKeyProtected = TestSecrets.Protect(AzuriteKey), Region = AzureRegion.Global };
        var container = factoryClient.CreateServiceClient(azurite).GetBlobContainerClient(containerName);

        try
        {
            await _client.PostAsync($"/api/backup-configs/{config!.Id}/run", null);
            BackupRunResponse? backup = null;
            for (var i = 0; i < 600; i++)
            {
                backup = await (await _client.GetAsync($"/api/backup-configs/{config.Id}/run")).Content.ReadFromJsonAsync<BackupRunResponse>();
                if (backup!.Status != "Running") break;
                await Task.Delay(200);
            }
            Assert.Equal("Completed", backup!.Status);

            // /check: a healthy backup → ok=true, with the single file on record. The check is a background job (202 + polling):
            // a content-level check downloads the whole backup and recomputes hashes, and a synchronous endpoint would get cut off by a browser/reverse-proxy timeout first.
            var checkStart = await _client.PostAsync($"/api/backup-configs/{config.Id}/check", null);
            Assert.Equal(HttpStatusCode.Accepted, checkStart.StatusCode);
            CheckRunResponse? check = null;
            for (var i = 0; i < 600; i++)
            {
                check = await (await _client.GetAsync($"/api/backup-configs/{config.Id}/check"))
                    .Content.ReadFromJsonAsync<CheckRunResponse>();
                if (check!.Status != "Running") break;
                await Task.Delay(200);
            }
            Assert.Equal("Completed", check!.Status);
            var checkReport = check.Report;
            Assert.True(checkReport!.Ok);
            Assert.Single(checkReport.Findings);

            // /repair: nothing to repair → Completed
            var repairStart = await _client.PostAsync($"/api/backup-configs/{config.Id}/repair", null);
            Assert.Equal(HttpStatusCode.Accepted, repairStart.StatusCode);
            RepairRunResponse? repair = null;
            for (var i = 0; i < 600; i++)
            {
                repair = await (await _client.GetAsync($"/api/backup-configs/{config.Id}/repair")).Content.ReadFromJsonAsync<RepairRunResponse>();
                if (repair!.Status != "Running") break;
                await Task.Delay(200);
            }
            Assert.Equal("Completed", repair!.Status);

            // /restore-estimate: download volume estimate for a single file
            var estimateRes = await _client.PostAsJsonAsync($"/api/backup-configs/{config.Id}/restore-estimate",
                new RestoreEstimateRequestBody(null, ["a.txt"]));
            Assert.Equal(HttpStatusCode.OK, estimateRes.StatusCode);
            var estimate = await estimateRes.Content.ReadFromJsonAsync<RestoreEstimateResult>();
            Assert.Equal(1, estimate!.fileCount);
            Assert.True(estimate.downloadBytes > 0);

            // /file-versions + /unrecoverable: on a healthy backup the candidate exists and there are no unrecoverable items
            var fv = await _client.GetFromJsonAsync<List<FileVersionCandidate>>($"/api/backup-configs/{config.Id}/file-versions?path=a.txt");
            var candidate = Assert.Single(fv!);
            Assert.Equal(1, candidate.version);

            var unrec = await _client.GetFromJsonAsync<List<string>>($"/api/backup-configs/{config.Id}/unrecoverable");
            Assert.Empty(unrec!);
        }
        finally
        {
            try { Directory.Delete(localRoot, recursive: true); } catch { /* best effort */ }
            await container.DeleteIfExistsAsync();
        }
    }

    [Fact]
    public async Task Delete_config_discards_its_journals()
    {
        var accountId = await CreateAccountAsync("journal-sweep");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("j", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var journals = _services.GetRequiredService<BackupJournalStore>();
        await using (var j = await journals.CreateAsync(accountId, "photos", "leftover", new JournalHeader
        {
            RunId = "leftover", ConfigId = created!.Id, StartedAt = DateTimeOffset.UtcNow,
            BaselineVersion = 0, LocalRoot = "/data/photos", EncryptionIdentity = "plain",
        }, default))
            await j.AppendAsync(
                new JournalRecord { Kind = "blob", Ref = "data/aaa", Path = "a.bin", FullHash = "aaa" }, default);
        Assert.Single(await journals.ListAsync(accountId, "photos", default));

        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.DeleteAsync($"/api/backup-configs/{created.Id}")).StatusCode);

        // With the config gone, nobody will ever adopt this journal again; keeping it around only protects that batch of blocks from cleanup forever.
        Assert.Empty(await journals.ListAsync(accountId, "photos", default));
    }

    [Fact]
    public async Task Suspend_without_a_running_backup_is_a_conflict()
    {
        var accountId = await CreateAccountAsync("suspend-idle");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("s", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var res = await _client.PostAsync($"/api/backup-configs/{created!.Id}/suspend", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Retry_now_without_a_paused_backup_is_a_conflict()
    {
        var accountId = await CreateAccountAsync("retry-idle");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("r", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var res = await _client.PostAsync($"/api/backup-configs/{created!.Id}/retry-now", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    /// <summary>Same shape as Suspend/Retry now: the operator pressed a button and is owed an answer about why
    /// nothing happened, not a silent no-op.</summary>
    [Fact]
    public async Task Pausing_without_a_running_backup_is_a_conflict()
    {
        var accountId = await CreateAccountAsync("pause-idle");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("pa", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var res = await _client.PostAsync($"/api/backup-configs/{created!.Id}/pause", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Resuming_without_a_paused_backup_is_a_conflict()
    {
        var accountId = await CreateAccountAsync("resume-idle");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("re", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var res = await _client.PostAsync($"/api/backup-configs/{created!.Id}/resume", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    /// <summary>
    /// The window this endpoint used to answer 204 in while doing nothing at all. A run that has been told to stop
    /// stays <see cref="RunStatus.Running"/> for the whole wind-down — potentially minutes, since a Suspend waits
    /// for the volume in hand to finish uploading — but its gate has been downgraded, and a downgraded gate can
    /// never hold anyone again. So the operator would press Pause on a stopping run, be told it worked, and watch
    /// the run stop anyway. Same for the shape a patience auto-suspend leaves behind, which downgrades the very
    /// same gate with nothing else about the run changing.
    /// <para>
    /// <see cref="BackupRunControl.RequestStop"/> is what puts a real run into this state, and it is used here
    /// rather than a bare <c>Gate.Downgrade()</c> for that reason: it is the production path, and it downgrades the
    /// gate as a side effect of setting the stop intent.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Pausing_a_backup_that_is_already_stopping_is_a_conflict()
    {
        var accountId = await CreateAccountAsync("pause-stopping");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("ps", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var backupRunner = factory.Services.GetRequiredService<BackupRunner>();
        var journals = _services.GetRequiredService<BackupJournalStore>();
        await using var control = new BackupRunControl(journals, created!.Id, "run-stopping");
        control.RequestStop(StopKind.Suspend);
        InjectRun(backupRunner, created.Id, new BackupRunState { Status = RunStatus.Running, Control = control });
        try
        {
            var paused = await _client.PostAsync($"/api/backup-configs/{created.Id}/pause", null);
            Assert.Equal(HttpStatusCode.Conflict, paused.StatusCode);

            // And the same for its counterpart: the downgrade ended whatever hold there was, so there is nothing
            // for a Resume to lift either.
            var resumed = await _client.PostAsync($"/api/backup-configs/{created.Id}/resume", null);
            Assert.Equal(HttpStatusCode.Conflict, resumed.StatusCode);
        }
        finally
        {
            RemoveRun(backupRunner, created.Id);
        }
    }

    /// <summary>
    /// The wind-down has to reach the browser, and this asserts it **on the wire** rather than on
    /// <see cref="BackupRunState"/>.
    /// <para>
    /// That distinction is the bug this was written for. <c>BackupRunState.StopRequested</c> reports the standing
    /// stop kind live off the control, and the frontend reads <c>run.stopRequested</c> to decide which controls
    /// stay live during a wind-down — but nothing carried the one into the other, because the polled payload is
    /// <see cref="BackupRunResponse"/> and the field was never added to it. Both ends had tests and both passed:
    /// the state exposed it, the browser's ladder parsed it, and the property in between was simply absent from the
    /// JSON. What the operator saw was the symptom the reporting was added to fix, unchanged — press Suspend,
    /// switch page, come back, and Suspend is offered again for a decision already taken, with nothing on screen
    /// about the stop already running.
    /// </para>
    /// <para>
    /// So this reads the raw JSON rather than deserialising into a typed record: a typed read would fill a missing
    /// property with a default and assert nothing about whether it crossed the boundary at all.
    /// </para>
    /// <para>
    /// The name, not the number. Nothing in this application registers a <c>JsonStringEnumConverter</c> — see the
    /// remarks on <see cref="PauseSource"/> — so a stop kind published as itself would arrive as <c>1</c> and the
    /// browser's <c>windDownFromServer</c>, which switches on the names, would read it as no wind-down at all: the
    /// very failure this test exists to catch, in a shape that looks like it works.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_running_backup_reports_the_stop_it_was_asked_for()
    {
        var accountId = await CreateAccountAsync("stop-reported");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("sr", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var backupRunner = factory.Services.GetRequiredService<BackupRunner>();
        var journals = _services.GetRequiredService<BackupJournalStore>();
        await using var control = new BackupRunControl(journals, created!.Id, "run-winding-down");
        InjectRun(backupRunner, created.Id, new BackupRunState { Status = RunStatus.Running, Control = control });
        try
        {
            // Before anyone asks: the bottom of the ladder, spelled out rather than omitted, so the browser can
            // tell "no stop" apart from "this build does not report it".
            var before = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>(
                $"/api/backup-configs/{created.Id}/run");
            Assert.Equal(nameof(StopKind.None), before.GetProperty("stopRequested").GetString());

            // RequestStop is the production path — it is what the /suspend endpoint calls, and it downgrades the
            // gate as a side effect of setting the intent.
            control.RequestStop(StopKind.Suspend);

            var during = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>(
                $"/api/backup-configs/{created.Id}/run");
            // Still Running throughout the wind-down — which is exactly why the stop kind has to be reported
            // separately for the row to say anything true about it.
            Assert.Equal(nameof(RunStatus.Running), during.GetProperty("status").GetString());
            Assert.Equal(nameof(StopKind.Suspend), during.GetProperty("stopRequested").GetString());

            // And it escalates, because the ladder is what the browser keys Stop's own affordance off.
            control.RequestStop(StopKind.StopNow);
            var escalated = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>(
                $"/api/backup-configs/{created.Id}/run");
            Assert.Equal(nameof(StopKind.StopNow), escalated.GetProperty("stopRequested").GetString());
        }
        finally
        {
            RemoveRun(backupRunner, created.Id);
        }
    }

    [Fact]
    public async Task Interrupted_runs_are_listed_with_their_block_count()
    {
        var accountId = await CreateAccountAsync("interrupted-list");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("i", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var journals = _services.GetRequiredService<BackupJournalStore>();
        await using (var j = await journals.CreateAsync(accountId, "photos", "run-1", new JournalHeader
        {
            RunId = "run-1", ConfigId = created!.Id, StartedAt = DateTimeOffset.UnixEpoch,
            BaselineVersion = 0, LocalRoot = "/data/photos", EncryptionIdentity = "plain",
        }, default))
        {
            await j.AppendAsync(
                new JournalRecord { Kind = "blob", Ref = "data/aaa", Path = "a.bin", FullHash = "aaa" }, default);
            await j.AppendAsync(
                new JournalRecord { Kind = "blob", Ref = "data/bbb", Path = "b.bin", FullHash = "bbb" }, default);
        }

        var listed = await _client.GetFromJsonAsync<List<InterruptedRunResponse>>(
            $"/api/backup-configs/{created.Id}/interrupted");

        Assert.Single(listed!);
        Assert.Equal("run-1", listed![0].RunId);
        Assert.Equal(2, listed[0].Blocks);          // the header line does not count
        Assert.True(listed[0].JournalBytes > 0);
        Assert.True(listed[0].Resumable);
    }

    [Fact]
    public async Task Interrupted_run_from_another_local_root_is_listed_but_not_resumable()
    {
        var accountId = await CreateAccountAsync("interrupted-moved");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("m", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var journals = _services.GetRequiredService<BackupJournalStore>();
        await using (var j = await journals.CreateAsync(accountId, "photos", "run-2", new JournalHeader
        {
            RunId = "run-2", ConfigId = created!.Id, StartedAt = DateTimeOffset.UnixEpoch,
            BaselineVersion = 0, LocalRoot = "/somewhere/else", EncryptionIdentity = "plain",
        }, default)) { }

        var listed = await _client.GetFromJsonAsync<List<InterruptedRunResponse>>(
            $"/api/backup-configs/{created.Id}/interrupted");
        Assert.False(listed![0].Resumable);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.DeleteAsync($"/api/backup-configs/{created.Id}/interrupted")).StatusCode);
        Assert.Empty(await _client.GetFromJsonAsync<List<InterruptedRunResponse>>(
            $"/api/backup-configs/{created.Id}/interrupted") ?? []);
    }
}
