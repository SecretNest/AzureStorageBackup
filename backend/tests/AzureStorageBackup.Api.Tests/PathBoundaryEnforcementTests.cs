using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

public class PathBoundaryEnforcementTests
{
    private sealed class RootedFactory(string root) : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Backup:Root", root);
        }
    }

    private static string TempRoot()
    {
        var p = Path.Combine(Path.GetTempPath(), "asb-enforce-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    private static AccountRequest SampleAccount() => new(
        Name: "acct", Description: null,
        BlobEndpoint: "https://x.blob.core.windows.net",
        Region: AzureRegion.Global, AccountKey: "dGVzdA==",
        UseProxy: false, ProxyMode: ProxyMode.Independent,
        ProxyHost: null, ProxyPort: null, ProxyUsername: null, ProxyPassword: null);

    private sealed record PathOutsideRootError(string error, string code);

    /// <summary>
    /// Writes one out-of-bounds config directly through IBackupConfigService.CreateAsync (the service layer, bypassing the guarded endpoint),
    /// simulating "a legacy config that already existed before the root was set" — that is exactly the scenario the four endpoint guards
    /// (/run, /restore, /repair, /check) and the scheduler guard exist to stop, and bypassing the create guard on the endpoint is the only way to produce such data.
    /// </summary>
    private static async Task<int> CreateOutOfRootConfigAsync(
        TestWebAppFactory factory, int accountId, string container, string name)
    {
        using var scope = factory.Services.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        var created = await configs.CreateAsync(new BackupConfig
        {
            AccountId = accountId,
            ContainerName = container,
            Name = name,
            LocalRoot = "/definitely/outside/the/root",
        });
        return created.Id;
    }

    /// <summary>
    /// The positive-direction fixture symmetric to <see cref="CreateOutOfRootConfigAsync"/>: a config whose local root lies
    /// **inside** <c>Backup__Root</c>. It is built through the service layer rather than the endpoint so that the four guards under test are the only
    /// boundary check on that request path — otherwise the guard on the create endpoint would run first and the positive cases would end up testing the create guard.
    /// <para><paramref name="accountId"/> may be a nonexistent account: all four guards sit before the account lookup,
    /// so letting the missing account terminate the request yields a fast, deterministic, network-free non-409 result.</para>
    /// </summary>
    private static async Task<int> CreateInRootConfigAsync(
        TestWebAppFactory factory, int accountId, string container, string name, string root)
    {
        using var scope = factory.Services.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        var created = await configs.CreateAsync(new BackupConfig
        {
            AccountId = accountId,
            ContainerName = container,
            Name = name,
            LocalRoot = Path.Combine(root, "photos"),
        });
        return created.Id;
    }

    [Fact]
    public async Task Creating_A_Config_Outside_The_Root_Is_Rejected()
    {
        var root = TempRoot();
        using var factory = new RootedFactory(root);
        var client = factory.CreateClient();
        var acct = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount()))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var res = await client.PostAsJsonAsync("/api/backup-configs", new
        {
            accountId = acct!.Id,
            containerName = "c",
            name = "outside",
            localRoot = "/definitely/outside/the/root",
        });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("path_outside_root", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Creating_A_Config_Inside_The_Root_Is_Accepted()
    {
        var root = TempRoot();
        using var factory = new RootedFactory(root);
        var client = factory.CreateClient();
        var acct = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount()))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var res = await client.PostAsJsonAsync("/api/backup-configs", new
        {
            accountId = acct!.Id,
            containerName = "c",
            name = "inside",
            localRoot = Path.Combine(root, "photos"),
        });

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    [Fact]
    public async Task Without_A_Root_Any_Local_Path_Is_Accepted()
    {
        using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        var acct = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount()))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var res = await client.PostAsJsonAsync("/api/backup-configs", new
        {
            accountId = acct!.Id,
            containerName = "c",
            name = "anywhere",
            localRoot = "/anywhere/at/all",
        });

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    /// <summary>
    /// TaskDispatcher does not go through the endpoints: feed the scheduler a scheduled task whose local root lies
    /// outside the configured Backup__Root directly, and confirm it is skipped rather than attempted and then failing — otherwise the guards
    /// would only block manual operations while the scheduled tasks running unattended slipped past the boundary (a design point).
    /// The out-of-bounds config is written directly through IBackupConfigService.CreateAsync (the service layer, bypassing the guarded endpoint),
    /// simulating "a legacy config that already existed before the root was set", a scenario the design explicitly preserves (rather than deletes).
    /// </summary>
    [Fact]
    public async Task Scheduled_Task_For_A_Config_Outside_The_Root_Is_Skipped_Not_Attempted()
    {
        var root = TempRoot();
        using var factory = new RootedFactory(root);
        var client = factory.CreateClient();
        var acct = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount()))
            .Content.ReadFromJsonAsync<AccountResponse>();

        const string container = "scheduler-boundary-test-container";
        int configId;
        using (var scope = factory.Services.CreateScope())
        {
            var configs = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
            var created = await configs.CreateAsync(new BackupConfig
            {
                AccountId = acct!.Id,
                ContainerName = container,
                Name = "legacy-outside-root",
                LocalRoot = "/definitely/outside/the/root",
            });
            configId = created.Id;
        }

        var task = new AzureStorageBackup.Api.Models.ScheduledTask
        {
            TargetKind = AzureStorageBackup.Api.Models.TaskTargetKind.Backup,
            AccountId = acct.Id,
            ContainerName = container,
            TaskType = AzureStorageBackup.Api.Models.ScheduledTaskType.Backup,
            CronExpression = "* * * * *",
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };

        var dispatcher = factory.Services.GetRequiredService<TaskDispatcher>();
        await dispatcher.DispatchAsync(task, CancellationToken.None);

        using (var scope = factory.Services.CreateScope())
        {
            var configs = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
            var reloaded = await configs.GetAsync(configId, CancellationToken.None);
            // Without the boundary check the scheduler would really run a backup, inevitably fail on the fake account credentials, and persist Error.
            // Stopping at Normal with no error proves it was intercepted before touching real execution.
            Assert.Equal(BackupStatus.Normal, reloaded!.Status);
            Assert.Null(reloaded.LastError);
        }

        // The busy lock must have been released (the normal finally path), not left stuck in the "skipped" branch.
        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();
        Assert.True(busy.TryAcquire(acct.Id, container));
        busy.Release(acct.Id, container);
    }

    /// <summary>
    /// F1: a scheduler boundary skip must leave a trace the operator can see in the UI (the same shape as the busy-skip branch),
    /// not just a LogError in the container log — in a single-user unattended deployment nobody is ever going to dig through that.
    /// The assertions cover: the entry is written at Error level, the source carries the account+container dimensions, and the message names both the offending
    /// local root and the currently configured root — it is only "actionable" with both.
    /// </summary>
    [Fact]
    public async Task Scheduled_Task_Skip_For_Config_Outside_The_Root_Writes_An_Operation_Log_Entry()
    {
        var root = TempRoot();
        using var factory = new RootedFactory(root);
        var client = factory.CreateClient();
        var acct = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount()))
            .Content.ReadFromJsonAsync<AccountResponse>();

        const string container = "scheduler-boundary-log-test-container";
        await CreateOutOfRootConfigAsync(factory, acct!.Id, container, "legacy-outside-root-log");

        var task = new AzureStorageBackup.Api.Models.ScheduledTask
        {
            TargetKind = AzureStorageBackup.Api.Models.TaskTargetKind.Backup,
            AccountId = acct.Id,
            ContainerName = container,
            TaskType = AzureStorageBackup.Api.Models.ScheduledTaskType.Backup,
            CronExpression = "* * * * *",
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };

        var dispatcher = factory.Services.GetRequiredService<TaskDispatcher>();
        await dispatcher.DispatchAsync(task, CancellationToken.None);

        using var scope = factory.Services.CreateScope();
        var log = scope.ServiceProvider.GetRequiredService<IOperationLog>();
        var entries = await log.QueryAsync(null, null, null, null, 100, CancellationToken.None);
        Assert.Contains(entries, e =>
            e.Source == $"schedule:{acct.Id}/{container}" &&
            e.Level == OperationLogLevel.Error &&
            e.Message.Contains("/definitely/outside/the/root", StringComparison.Ordinal) &&
            e.Message.Contains(root, StringComparison.Ordinal));
    }

    /// <summary>F2: the boundary guard on /run (BackupConfigEndpoints.cs :179) currently has no regression test at all —
    /// deleting that line would turn no test red.</summary>
    [Fact]
    public async Task Run_Endpoint_Rejects_A_Config_Outside_The_Root()
    {
        var root = TempRoot();
        using var factory = new RootedFactory(root);
        var client = factory.CreateClient();
        var acct = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount()))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var configId = await CreateOutOfRootConfigAsync(
            factory, acct!.Id, "run-guard-test-container", "legacy-outside-root-run");

        var res = await client.PostAsync($"/api/backup-configs/{configId}/run", null);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<PathOutsideRootError>();
        Assert.Equal("path_outside_root", body!.code);
    }

    /// <summary>F2: the boundary guard on /restore (BackupConfigEndpoints.cs :202) currently has no regression test at all.
    /// Leaving TargetRoot empty → the endpoint falls back to config.LocalRoot (the out-of-bounds value), and the guard must stop it all the same.</summary>
    [Fact]
    public async Task Restore_Endpoint_Rejects_A_Config_Outside_The_Root()
    {
        var root = TempRoot();
        using var factory = new RootedFactory(root);
        var client = factory.CreateClient();
        var acct = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount()))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var configId = await CreateOutOfRootConfigAsync(
            factory, acct!.Id, "restore-guard-test-container", "legacy-outside-root-restore");

        var res = await client.PostAsJsonAsync(
            $"/api/backup-configs/{configId}/restore", new RestoreRequestBody(null, null));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<PathOutsideRootError>();
        Assert.Equal("path_outside_root", body!.code);
    }

    /// <summary>F2: the boundary guard on /repair (BackupConfigEndpoints.cs :372) currently has no regression test at all.</summary>
    [Fact]
    public async Task Repair_Endpoint_Rejects_A_Config_Outside_The_Root()
    {
        var root = TempRoot();
        using var factory = new RootedFactory(root);
        var client = factory.CreateClient();
        var acct = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount()))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var configId = await CreateOutOfRootConfigAsync(
            factory, acct!.Id, "repair-guard-test-container", "legacy-outside-root-repair");

        var res = await client.PostAsync($"/api/backup-configs/{configId}/repair", null);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<PathOutsideRootError>();
        Assert.Equal("path_outside_root", body!.code);
    }

    /// <summary>F2: the boundary guard on /check (BackupConfigEndpoints.cs :422) currently has no regression test at all.</summary>
    [Fact]
    public async Task Check_Endpoint_Rejects_A_Config_Outside_The_Root()
    {
        var root = TempRoot();
        using var factory = new RootedFactory(root);
        var client = factory.CreateClient();
        var acct = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount()))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var configId = await CreateOutOfRootConfigAsync(
            factory, acct!.Id, "check-guard-test-container", "legacy-outside-root-check");

        var res = await client.PostAsync($"/api/backup-configs/{configId}/check", null);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<PathOutsideRootError>();
        Assert.Equal("path_outside_root", body!.code);
    }

    /// <summary>
    /// The "out-of-bounds path → 409 + code: path_outside_root" row listed in the migration design doc
    /// (docs/change-local-root-design.md, "Tests") had nothing covering it on the preview endpoint — the guard sits right inside
    /// PrepareLocalRootAsync (BackupConfigEndpoints.cs :844), and deleting that line would turn no
    /// test red. Whether the target root is out of bounds and whether the migrated config's own LocalRoot is in or out of bounds are two different things:
    /// here CreateInRootConfigAsync builds a clean in-bounds config so that only "the new root is out of bounds" trips the guard,
    /// without mixing in the guard for a config that is itself out of bounds (which is what the F2 cases above test).
    /// </summary>
    [Fact]
    public async Task LocalRoot_Preview_Endpoint_Rejects_A_New_Root_Outside_The_Root()
    {
        var root = TempRoot();
        using var factory = new RootedFactory(root);
        var client = factory.CreateClient();
        var acct = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount()))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var configId = await CreateInRootConfigAsync(
            factory, acct!.Id, "local-root-preview-guard-test-container", "in-root-for-preview-guard", root);

        var res = await client.PostAsJsonAsync(
            $"/api/backup-configs/{configId}/local-root/preview",
            new LocalRootPreviewRequest("/definitely/outside/the/root"));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<PathOutsideRootError>();
        Assert.Equal("path_outside_root", body!.code);
    }

    /// <summary>
    /// Symmetric to the one above: the apply endpoint (/local-root, without /preview) reuses the same PrepareLocalRootAsync,
    /// and the guard must run before anything is persisted. When the target root is out of bounds, besides asserting 409 + path_outside_root we also
    /// assert that the config's LocalRoot in the database is untouched — if the guard were bypassed or placed wrong (say, after
    /// ChangeLocalRootAsync), the first thing to surface would be "an out-of-bounds request actually got written to the database".
    /// </summary>
    [Fact]
    public async Task LocalRoot_Apply_Endpoint_Rejects_A_New_Root_Outside_The_Root_And_Does_Not_Write()
    {
        var root = TempRoot();
        using var factory = new RootedFactory(root);
        var client = factory.CreateClient();
        var acct = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount()))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var originalRoot = Path.Combine(root, "photos");
        var configId = await CreateInRootConfigAsync(
            factory, acct!.Id, "local-root-apply-guard-test-container", "in-root-for-apply-guard", root);

        var res = await client.PostAsJsonAsync(
            $"/api/backup-configs/{configId}/local-root",
            new LocalRootChangeRequest("/definitely/outside/the/root"));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<PathOutsideRootError>();
        Assert.Equal("path_outside_root", body!.code);

        using var scope = factory.Services.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        var reloaded = await configs.GetAsync(configId, CancellationToken.None);
        Assert.Equal(originalRoot, reloaded!.LocalRoot);
    }

    // ---- F3 (final review): the **pass-through** direction of the four guards ----
    // The five cases above only nail down the rejection direction. Without the pass-through direction, any guard passing the wrong argument — say
    // `PathBoundaryGuard.Blocked(boundary, config.ContainerName)` — would leave all 510 tests green:
    // a container name is not an absolute path, IsInside simply returns false, and the rejection cases still get their 409.
    // One case per endpoint below: local root inside the root → it must get past the guard and reach that endpoint's own subsequent result.
    // A nonexistent account id is used so the request terminates deterministically after the guard and before any network activity.

    private const int MissingAccountId = 999999;

    [Fact]
    public async Task Run_Endpoint_Accepts_A_Config_Inside_The_Root()
    {
        var root = TempRoot();
        using var factory = new RootedFactory(root);
        var client = factory.CreateClient();

        var configId = await CreateInRootConfigAsync(
            factory, MissingAccountId, "run-pass-test-container", "inside-root-run", root);

        var res = await client.PostAsync($"/api/backup-configs/{configId}/run", null);

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
    }

    [Fact]
    public async Task Restore_Endpoint_Accepts_A_Config_Inside_The_Root()
    {
        var root = TempRoot();
        using var factory = new RootedFactory(root);
        var client = factory.CreateClient();

        var configId = await CreateInRootConfigAsync(
            factory, MissingAccountId, "restore-pass-test-container", "inside-root-restore", root);

        // Leaving TargetRoot empty → the endpoint falls back to config.LocalRoot (inside the root), the same value branch the rejection case takes.
        var res = await client.PostAsJsonAsync(
            $"/api/backup-configs/{configId}/restore", new RestoreRequestBody(null, null));

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
    }

    [Fact]
    public async Task Repair_Endpoint_Accepts_A_Config_Inside_The_Root()
    {
        var root = TempRoot();
        using var factory = new RootedFactory(root);
        var client = factory.CreateClient();

        var configId = await CreateInRootConfigAsync(
            factory, MissingAccountId, "repair-pass-test-container", "inside-root-repair", root);

        var res = await client.PostAsync($"/api/backup-configs/{configId}/repair", null);

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
    }

    [Fact]
    public async Task Check_Endpoint_Accepts_A_Config_Inside_The_Root()
    {
        var root = TempRoot();
        using var factory = new RootedFactory(root);
        var client = factory.CreateClient();

        var configId = await CreateInRootConfigAsync(
            factory, MissingAccountId, "check-pass-test-container", "inside-root-check", root);

        var res = await client.PostAsync($"/api/backup-configs/{configId}/check", null);

        // Since check became a background job it has the same shape as /repair: out of bounds gets 400/409 at the guard, so getting 202
        // proves this step has already been passed (the nonexistent-account failure shows up later in the run state, not in the status code).
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
    }

    /// <summary>
    /// F1 (final review): the picker's "Use this folder" round trip. The <c>path</c> the browse endpoint returns gets POSTed back
    /// verbatim by the frontend as <c>localRoot</c> — with a **relative** root configured, that value must still get past the create endpoint's guard.
    /// It used to be the relative string the operator typed, and since IsInside only accepts absolute input, picking the root directory itself was guaranteed to 409.
    /// </summary>
    [Fact]
    public async Task The_Browse_Default_Path_Can_Be_Used_As_A_Local_Root_When_The_Root_Is_Relative()
    {
        var root = TempRoot();
        var relativeRoot = Path.GetRelativePath(Directory.GetCurrentDirectory(), root);
        using var factory = new RootedFactory(relativeRoot);
        var client = factory.CreateClient();
        var acct = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount()))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var browsed = await client.GetFromJsonAsync<BrowsePathOnly>("/api/system/browse");

        var res = await client.PostAsJsonAsync("/api/backup-configs", new
        {
            accountId = acct!.Id,
            containerName = "picker-round-trip-container",
            name = "picked",
            localRoot = browsed!.path,
        });

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    private sealed record BrowsePathOnly(string path);
}
