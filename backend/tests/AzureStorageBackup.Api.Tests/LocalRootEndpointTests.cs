using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

public class LocalRootEndpointTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>, IDisposable
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly IServiceProvider _services = factory.Services;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lre-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private async Task<int> CreateAccountAsync()
    {
        var req = new AccountRequest(
            Name: "acct-" + Guid.NewGuid().ToString("N")[..6],
            Description: null,
            BlobEndpoint: "https://example.blob.core.windows.net",
            Region: AzureRegion.Global,
            AccountKey: "dGVzdGtleQ==",
            UseProxy: false,
            ProxyMode: ProxyMode.Independent,
            ProxyHost: null, ProxyPort: null, ProxyUsername: null, ProxyPassword: null);
        var res = await _client.PostAsJsonAsync("/api/accounts", req);
        var account = await res.Content.ReadFromJsonAsync<AccountResponse>();
        return account!.Id;
    }

    // Azurite's well-known account and key (same as BackupConfigEndpointsTests).
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string AzuriteEndpoint = "http://127.0.0.1:10000/devstoreaccount1";

    // Same as every other Azurite test in the repo: if it will not come up, skip instead of painting the whole
    // suite red (CI does run Azurite, see .github/workflows/ci.yml, so skipping only ever happens locally).
    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    /// <summary>Any "there really is no baseline" test that actually reaches LoadBaselineAsync must use this rather than
    /// CreateAccountAsync: that one uses a fake domain that does not resolve, and with no local state
    /// TrackedInfoStore.LoadAsync falls back to the cloud to backfill; under a fake domain that step is a genuine network
    /// failure (tens of seconds of timeout), which the new code classifies as BaselineUnreadable rather than NoBaseline —
    /// not what we are testing here. When the container really does not exist on Azurite, ExistsAsync returns false
    /// cleanly: no local state, no cloud info file, a real "nothing".</summary>
    private async Task<int> CreateAzuriteAccountAsync()
    {
        var req = new AccountRequest(
            Name: "azurite-" + Guid.NewGuid().ToString("N")[..6], Description: null,
            BlobEndpoint: AzuriteEndpoint, Region: AzureRegion.Global, AccountKey: AzuriteKey,
            UseProxy: false, ProxyMode: ProxyMode.Independent,
            ProxyHost: null, ProxyPort: null, ProxyUsername: null, ProxyPassword: null);
        var res = await _client.PostAsJsonAsync("/api/accounts", req);
        var account = await res.Content.ReadFromJsonAsync<AccountResponse>();
        return account!.Id;
    }

    /// <summary>Create a config straight in the database (bypassing the create endpoint's check that the local root exists).</summary>
    private async Task<int> CreateConfigAsync(int accountId, string localRoot)
    {
        using var scope = _services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        var created = await svc.CreateAsync(new BackupConfig
        {
            AccountId = accountId,
            ContainerName = "c" + Guid.NewGuid().ToString("N")[..8],
            Name = "photos",
            LocalRoot = localRoot,
            IndexTier = StorageTier.Hot,
            DataTier = StorageTier.Cool,
        });
        return created.Id;
    }

    private async Task<string> ContainerOfAsync(int configId)
    {
        using var scope = _services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        return (await svc.GetAsync(configId))!.ContainerName;
    }

    /// <summary>This backup's source key in the operation log. Repo-wide it has the shape "{op}:{accountId}/{container}"
    /// (OperationLogService.cs:91-96) — the test builds that shape itself, which is the only way to pin that the endpoint did not write something else.</summary>
    private async Task<string> SourceKeyOfAsync(int configId)
    {
        using var scope = _services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        var config = (await svc.GetAsync(configId))!;
        return $"backup:{config.AccountId}/{config.ContainerName}";
    }

    private List<LogEntry> LogsOf(string source)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return [.. db.LogEntries.Where(e => e.Source == source)];
    }

    private async Task<string> LocalRootOfAsync(int configId)
    {
        using var scope = _services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        return (await svc.GetAsync(configId))!.LocalRoot;
    }

    /// <summary>Write the local authoritative info file directly (TrackedInfoStore.LoadAsync never reads the cloud once it hits locally),
    /// the same trick as BackupConfigEndpointsTests.SeedLocalInfo. Returns identityTicks for SeedIndex to use.</summary>
    private long SeedLocalInfo(int accountId, string container, List<BackupVersion> versions)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var info = new BackupInfoFile
        {
            Backup = new BackupMeta { Name = "seed", CreatedAt = createdAt, Encrypted = false },
            Versions = versions,
        };
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.LocalBackupStates.Add(new LocalBackupState
        {
            AccountId = accountId, Container = container,
            InfoBytes = IndexSerializer.SerializeInfoFile(info), ETag = "seed-etag",
        });
        db.SaveChanges();
        return createdAt.UtcTicks;
    }

    /// <summary>Write the local info file as bytes that fail validation — format byte 99 is greater than the newest format
    /// currently supported, so IndexSerializer.DeserializeInfoFile throws NotSupportedException right after reading the first byte.
    /// Used to reproduce "there is history but the index cannot be read" (BaselineUnreadable) deterministically in tests, without
    /// depending on encryption or cloud failures, which are far harder to stage.</summary>
    private void SeedCorruptLocalInfo(int accountId, string container)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.LocalBackupStates.Add(new LocalBackupState
        {
            AccountId = accountId, Container = container,
            InfoBytes = [99], ETag = "seed-etag",
        });
        db.SaveChanges();
    }

    private void SeedIndex(int accountId, string container, int version, long identityTicks, VersionIndex index)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.CachedVersionIndexes.Add(new CachedVersionIndex
        {
            AccountId = accountId, Container = container, Version = version,
            IdentityTicks = identityTicks, Bytes = IndexSerializer.SerializeIndex(index),
        });
        db.SaveChanges();
    }

    /// <summary>Create a config whose baseline does not match the new root at all: the new root is an empty directory, so
    /// the single file in the baseline index is nowhere to be found, sample match rate 0% → Rejected, which needs force to
    /// write. Reused by the force-gate tests. Passing null for <paramref name="localRoot"/> means use _dir as the current
    /// root; passing "" simulates the kind of config an import left behind when it had no SourceRootHint.</summary>
    private async Task<(int Id, string Target)> SeedMismatchingBaselineAsync(string? localRoot = null)
    {
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var accountId = await CreateAccountAsync();
        var id = await CreateConfigAsync(accountId, localRoot ?? _dir);
        var container = await ContainerOfAsync(id);

        var identityTicks = SeedLocalInfo(accountId, container,
        [
            new BackupVersion
            {
                Version = 1, CreatedAt = DateTimeOffset.UtcNow, IndexBlob = "v1.index",
                Stats = new VersionStats(1, 5, 1, 5),
            },
        ]);
        SeedIndex(accountId, container, 1, identityTicks, new VersionIndex
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
        });

        return (id, target);
    }

    /// <summary>Create a config with "history exists but the index cannot be read" (see SeedCorruptLocalInfo).</summary>
    private async Task<(int Id, string Target)> SeedUnreadableBaselineAsync()
    {
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var accountId = await CreateAccountAsync();
        var id = await CreateConfigAsync(accountId, _dir);
        var container = await ContainerOfAsync(id);

        SeedCorruptLocalInfo(accountId, container);

        return (id, target);
    }

    [Fact]
    public async Task Preview_Rejects_A_Relative_Path()
    {
        Directory.CreateDirectory(_dir);
        var id = await CreateConfigAsync(await CreateAccountAsync(), _dir);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root/preview", new { newRoot = "relative/path" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Preview_Rejects_An_Empty_Path()
    {
        Directory.CreateDirectory(_dir);
        var id = await CreateConfigAsync(await CreateAccountAsync(), _dir);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root/preview", new { newRoot = "" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Preview_Rejects_A_Path_That_Does_Not_Exist()
    {
        Directory.CreateDirectory(_dir);
        var id = await CreateConfigAsync(await CreateAccountAsync(), _dir);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root/preview",
            new { newRoot = Path.Combine(_dir, "nope") });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Preview_Rejects_A_Path_That_Is_A_File()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "afile");
        await File.WriteAllTextAsync(file, "x");
        var id = await CreateConfigAsync(await CreateAccountAsync(), _dir);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root/preview", new { newRoot = file });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [SkippableFact]
    public async Task Preview_Reports_NoBaseline_When_The_Backup_Has_No_Versions()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var id = await CreateConfigAsync(await CreateAzuriteAccountAsync(), _dir);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root/preview", new { newRoot = target });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<LocalRootPreviewResponse>();
        Assert.Equal(nameof(LocalRootVerdict.NoBaseline), body!.Verdict);
        Assert.NotNull(body.Reason);
    }

    /// <summary>preview is a pure query: once it has run, the config must be unchanged down to the byte.</summary>
    [Fact]
    public async Task Preview_Does_Not_Change_Anything()
    {
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var id = await CreateConfigAsync(await CreateAccountAsync(), _dir);

        await _client.PostAsJsonAsync($"/api/backup-configs/{id}/local-root/preview", new { newRoot = target });

        using var scope = _services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        var config = await svc.GetAsync(id);
        Assert.Equal(_dir, config!.LocalRoot);
    }

    [SkippableFact]
    public async Task Apply_Moves_The_Root_When_There_Is_No_Baseline()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var id = await CreateConfigAsync(await CreateAzuriteAccountAsync(), _dir);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = false });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal(target, body!.LocalRoot);
    }

    /// <summary>A config imported without a SourceRootHint has an empty-string root — and it has to be fillable.
    /// This one takes the "the cloud has no versions yet" path (`NoBaseline`), so what it really guards is that
    /// writing the audit line with <c>oldRoot == ""</c> does not blow up (it renders as <c>(none)</c>); the path where an
    /// empty root **does** have a baseline to compare against is covered by
    /// <c>An_Imported_Backup_With_No_Root_Is_Still_Checked_Against_Its_Index</c>.</summary>
    [SkippableFact]
    public async Task Apply_Fills_In_An_Empty_Root_Left_Behind_By_Import()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        Directory.CreateDirectory(_dir);
        var id = await CreateConfigAsync(await CreateAzuriteAccountAsync(), localRoot: "");

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = _dir, force = false });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal(_dir, body!.LocalRoot);
    }

    [Fact]
    public async Task Apply_Is_Refused_While_The_Backup_Is_Busy()
    {
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var accountId = await CreateAccountAsync();
        var id = await CreateConfigAsync(accountId, _dir);

        string container;
        using (var scope = _services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
            container = (await svc.GetAsync(id))!.ContainerName;
        }

        var busy = _services.GetRequiredService<BackupBusyTracker>();
        Assert.True(busy.TryAcquire(accountId, container, "BackingUp"));
        try
        {
            var res = await _client.PostAsJsonAsync(
                $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = false });

            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);

            using var scope = _services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
            Assert.Equal(_dir, (await svc.GetAsync(id))!.LocalRoot);   // not persisted
        }
        finally
        {
            busy.Release(accountId, container);
        }
    }

    /// <summary>The force gate is what makes this whole feature safe: NeedsConfirm/Rejected without force must be refused,
    /// and the LocalRoot in the database must be unchanged down to the byte. The previous 10 tests all went down the
    /// NoBaseline branch (needsForce always false), so this gate was never actually executed — invert a boolean and it would still be all green.</summary>
    [Fact]
    public async Task Apply_Refuses_A_Mismatching_Baseline_Without_Force()
    {
        var (id, target) = await SeedMismatchingBaselineAsync();

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = false });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("local_root_mismatch", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            nameof(LocalRootVerdict.Rejected),
            doc.RootElement.GetProperty("preview").GetProperty("verdict").GetString());

        Assert.Equal(_dir, await LocalRootOfAsync(id));   // not persisted
    }

    /// <summary>The same mismatching baseline, this time with force:true — it must really be written. The other half of the gate.</summary>
    [Fact]
    public async Task Apply_Writes_A_Mismatching_Baseline_When_Forced()
    {
        var (id, target) = await SeedMismatchingBaselineAsync();

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = true });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal(target, body!.LocalRoot);
        Assert.Equal(target, await LocalRootOfAsync(id));
    }

    /// <summary>
    /// A config imported without a SourceRootHint has an empty-string root, yet its version indexes all landed in the
    /// local cache at import time (BackupConfigEndpoints.cs:110-127). "The current root is empty" used to short-circuit the
    /// whole comparison into NoBaseline and wave it through — precisely the case where the user is most likely guessing at
    /// a mount point, and the last one that should get a free pass.
    /// Whether we can compare now depends only on whether a baseline exists: point it at the wrong directory and you are still stopped.
    /// </summary>
    [Fact]
    public async Task An_Imported_Backup_With_No_Root_Is_Still_Checked_Against_Its_Index()
    {
        var (id, target) = await SeedMismatchingBaselineAsync(localRoot: "");

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = false });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(
            nameof(LocalRootVerdict.Rejected),
            doc.RootElement.GetProperty("preview").GetProperty("verdict").GetString());

        Assert.Equal("", await LocalRootOfAsync(id));   // not persisted
    }

    /// <summary>The index cannot be read (Finding 1): preview must report BaselineUnreadable rather than disguising it as
    /// NoBaseline and waving it through — Reason has to carry the underlying exception message; the NAS user has no command line, and this is the only diagnostic there is.</summary>
    [Fact]
    public async Task Preview_Reports_BaselineUnreadable_When_The_Local_Index_Is_Corrupt()
    {
        var (id, target) = await SeedUnreadableBaselineAsync();

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root/preview", new { newRoot = target });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<LocalRootPreviewResponse>();
        Assert.Equal(nameof(LocalRootVerdict.BaselineUnreadable), body!.Verdict);
        Assert.Contains("could not be read", body.Reason);
        Assert.Contains("newer than supported", body.Reason);   // the underlying exception message really is passed through
    }

    /// <summary>BaselineUnreadable goes through the force gate too: without force it must be refused and nothing persisted.</summary>
    [Fact]
    public async Task Apply_Refuses_An_Unreadable_Baseline_Without_Force()
    {
        var (id, target) = await SeedUnreadableBaselineAsync();

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = false });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("local_root_mismatch", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            nameof(LocalRootVerdict.BaselineUnreadable),
            doc.RootElement.GetProperty("preview").GetProperty("verdict").GetString());

        Assert.Equal(_dir, await LocalRootOfAsync(id));   // not persisted
    }

    /// <summary>...and with force:true it must really be written.</summary>
    [Fact]
    public async Task Apply_Writes_Through_An_Unreadable_Baseline_When_Forced()
    {
        var (id, target) = await SeedUnreadableBaselineAsync();

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = true });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal(target, body!.LocalRoot);
        Assert.Equal(target, await LocalRootOfAsync(id));
    }

    /// <summary>
    /// The audit entry must hang off the source "backup:{accountId}/{container}". Writing a bare "backup" has two
    /// consequences, neither of which anyone spots on the spot: DeleteForContainerAsync cleans up by the
    /// ":{accountId}/{container}" suffix, so this Warning-level (long-lived) record stays behind in the database after the
    /// backup is deleted; and QueryAsync filters on exact source equality, so in the "what has happened to this backup"
    /// log view, changing the root — a big deal — is simply invisible.
    ///
    /// While we are here, pin the wording for the no-baseline case: with nothing sampled it must not render as
    /// "0/0 sampled entries matched" — that reads like "nothing matched at all", which is exactly the opposite meaning.
    /// </summary>
    [SkippableFact]
    public async Task Apply_Logs_An_Audit_Entry_Under_This_Backups_Source_Key()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var id = await CreateConfigAsync(await CreateAzuriteAccountAsync(), _dir);
        var source = await SourceKeyOfAsync(id);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = false });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var entry = Assert.Single(LogsOf(source));
        Assert.Equal(OperationLogLevel.Warning, entry.Level);
        Assert.False(entry.Ephemeral);              // audit: long-lived, kept until the backup is deleted
        Assert.Contains(target, entry.Message);
        Assert.Contains(nameof(LocalRootVerdict.NoBaseline), entry.Message);
        Assert.DoesNotContain("sampled", entry.Message);
        // With nothing sampled, that slot goes to reason instead — for the BaselineUnreadable tier the reason is the
        // verbatim underlying exception, the only diagnostic that NAS user gets, and it must not live only in the response he casually dismissed.
        Assert.Contains("no version index", entry.Message);
    }

    /// <summary>On the path where sampling actually ran, the sample counts still have to reach the log; a forced change must leave a trace too.</summary>
    [Fact]
    public async Task Apply_Logs_The_Sample_Counts_When_A_Comparison_Actually_Ran()
    {
        var (id, target) = await SeedMismatchingBaselineAsync();
        var source = await SourceKeyOfAsync(id);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = true });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var entry = Assert.Single(LogsOf(source));
        Assert.Contains("0/1 sampled entries matched", entry.Message);
        Assert.Contains("forced", entry.Message);
    }

    [Fact]
    public async Task Unknown_Config_Is_A_404()
    {
        Directory.CreateDirectory(_dir);

        var res = await _client.PostAsJsonAsync(
            "/api/backup-configs/999999/local-root/preview", new { newRoot = _dir });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
