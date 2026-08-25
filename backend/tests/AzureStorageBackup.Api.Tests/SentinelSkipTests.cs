using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The gate itself: what a run does when the sentinel is not there.
/// <para>
/// None of these need Azurite, and that is not a convenience — it is the property under test. The gate sits ahead
/// of every piece of network I/O, so a skipped round costs nothing and cannot fail for a second reason. A test
/// here that needed a live account would mean the gate had been placed too late.
/// </para>
/// </summary>
public sealed class SentinelSkipTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>, IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "asb-skip-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// A config written straight through the service layer: these tests are about the runner, and going via the
    /// endpoint would only add an account and a container name that no assertion here reads.
    /// </summary>
    private async Task<BackupConfig> ConfigAsync(string? sentinel, BackupStatus status = BackupStatus.Normal)
    {
        Directory.CreateDirectory(_base);
        using var scope = factory.Services.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        var created = await configs.CreateAsync(new BackupConfig
        {
            AccountId = 4242,
            ContainerName = "sentinel-" + Guid.NewGuid().ToString("N")[..8],
            Name = "guarded",
            LocalRoot = _base,
            SentinelPath = sentinel,
        });
        if (status == BackupStatus.Error)
            await configs.SetErrorAsync(created.Id, "an earlier round really did fail");
        return created;
    }

    [Fact]
    public async Task A_Missing_Sentinel_Skips_The_Run()
    {
        var config = await ConfigAsync(Path.Combine(_base, "not-mounted"));

        var state = await factory.Services.GetRequiredService<BackupRunner>().StartAsync(config.Id);
        await state.Completion.Task;

        Assert.Equal(RunStatus.Skipped, state.Status);
        // The path belongs in the reason: the operator's next move is to go and look at it, and a bare
        // "skipped" sends them hunting through the config for which path it meant.
        Assert.Contains("not-mounted", state.SkipReason);
        // Not an error. A run that never started has nothing to report as one, and Error is a status the
        // operator has to clear by hand.
        Assert.Null(state.Error);
        Assert.Null(state.Version);
    }

    [Fact]
    public async Task A_Skipped_Run_Leaves_The_Persisted_Status_Alone()
    {
        // The dangerous half of "not a failure" is the other direction: writing Normal would wipe a genuine
        // earlier error off a backup that has not run since, and the red badge is the only trace of it.
        var config = await ConfigAsync(Path.Combine(_base, "not-mounted"), BackupStatus.Error);

        var state = await factory.Services.GetRequiredService<BackupRunner>().StartAsync(config.Id);
        await state.Completion.Task;

        Assert.Equal(RunStatus.Skipped, state.Status);
        using var scope = factory.Services.CreateScope();
        var after = await scope.ServiceProvider.GetRequiredService<IBackupConfigService>().GetAsync(config.Id);
        Assert.Equal(BackupStatus.Error, after!.Status);
        Assert.Equal("an earlier round really did fail", after.LastError);
    }

    [Fact]
    public async Task A_Skipped_Run_Says_So_In_The_Operation_Log()
    {
        // Without this the feature is indistinguishable from a scheduler that has quietly stopped working: an
        // unattended backup that skips every night for a month must leave something an operator can find.
        var config = await ConfigAsync(Path.Combine(_base, "not-mounted"));

        var state = await factory.Services.GetRequiredService<BackupRunner>().StartAsync(config.Id);
        await state.Completion.Task;

        using var scope = factory.Services.CreateScope();
        var entries = await scope.ServiceProvider.GetRequiredService<IOperationLog>()
            .QueryAsync(OperationLogLevel.Warning, $"backup:{config.AccountId}/{config.ContainerName}", null, null, 10);
        var entry = Assert.Single(entries);
        Assert.Contains("not-mounted", entry.Message);
    }

    [Fact]
    public async Task A_Scheduled_Run_That_Skips_Does_Not_Clear_The_Error()
    {
        // The scheduler writes "Normal" after every dispatch it does not consider a failure, so a status it has
        // never been taught about is silently treated as success. That is the worst of both worlds: the round did
        // nothing *and* it wiped the red badge from the last round that genuinely failed.
        using var scope = factory.Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        var account = await accounts.CreateAsync(new Account
        {
            Name = "sched-" + Guid.NewGuid().ToString("N")[..6],
            BlobEndpoint = "https://x.blob.core.windows.net",
            Region = AzureRegion.Global,
            AccountKeyProtected = TestSecrets.Protect("dGVzdA=="),
        });

        var configs = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        Directory.CreateDirectory(_base);
        var config = await configs.CreateAsync(new BackupConfig
        {
            AccountId = account.Id,
            ContainerName = "sched-" + Guid.NewGuid().ToString("N")[..8],
            Name = "guarded",
            LocalRoot = _base,
            SentinelPath = Path.Combine(_base, "not-mounted"),
        });
        await configs.SetErrorAsync(config.Id, "an earlier round really did fail");

        await factory.Services.GetRequiredService<TaskDispatcher>().DispatchAsync(new ScheduledTask
        {
            TargetKind = TaskTargetKind.Backup,
            TaskType = ScheduledTaskType.Backup,
            AccountId = account.Id,
            ContainerName = config.ContainerName,
        });

        var after = await configs.GetAsync(config.Id);
        Assert.Equal(BackupStatus.Error, after!.Status);
        Assert.Equal("an earlier round really did fail", after.LastError);
    }

    [Fact]
    public async Task A_Present_Sentinel_Is_Not_A_Gate()
    {
        // The other side of the gate. This config points at an account that does not exist, so letting it
        // through means it fails on account resolution — which is precisely the proof that the gate let it
        // through rather than stopping it.
        var sentinel = Path.Combine(_base, "mounted");
        Directory.CreateDirectory(_base);
        await File.WriteAllTextAsync(sentinel, "x");
        var config = await ConfigAsync(sentinel);

        var state = await factory.Services.GetRequiredService<BackupRunner>().StartAsync(config.Id);
        await state.Completion.Task;

        Assert.NotEqual(RunStatus.Skipped, state.Status);
    }

    [Fact]
    public async Task No_Sentinel_And_A_Root_That_Is_There_Is_Not_A_Gate()
    {
        // Every backup that predates this feature has null here, and as long as its root is present it must be
        // entirely unaffected.
        var config = await ConfigAsync(null);

        var state = await factory.Services.GetRequiredService<BackupRunner>().StartAsync(config.Id);
        await state.Completion.Task;

        Assert.NotEqual(RunStatus.Skipped, state.Status);
    }

    [Fact]
    public async Task With_No_Sentinel_A_Missing_Local_Root_Skips_The_Run()
    {
        // A root that is not there cannot be backed up in any case, so it answers the sentinel's question and
        // gets the sentinel's answer — which means the backup path needs no separate root-existence test, and a
        // config that never opted in still gets the protection.
        Directory.CreateDirectory(_base);
        using var scope = factory.Services.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        var config = await configs.CreateAsync(new BackupConfig
        {
            AccountId = 4242,
            ContainerName = "noroot-" + Guid.NewGuid().ToString("N")[..8],
            Name = "guarded",
            LocalRoot = Path.Combine(_base, "never-mounted"),
        });

        var state = await factory.Services.GetRequiredService<BackupRunner>().StartAsync(config.Id);
        await state.Completion.Task;

        Assert.Equal(RunStatus.Skipped, state.Status);
        // The message has to name the root, not a sentinel this backup never configured — otherwise it sends
        // the operator looking for a setting that does not exist.
        Assert.Contains("Local root", state.SkipReason);
        Assert.Contains("never-mounted", state.SkipReason);
    }
}
