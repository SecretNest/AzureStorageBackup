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
    /// 用 IBackupConfigService.CreateAsync（服务层，不经过带闸门的端点）直接写入一条越界配置，
    /// 模拟「设置根之前就存在的旧配置」——四个端点闸门（/run、/restore、/repair、/check）与
    /// 调度器闸门要防的正是这同一场景，绕过端点上的 create 闸门是唯一能造出这种数据的办法。
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
    /// TaskDispatcher 不经过端点：直接把一个「本地根落在配置的 Backup__Root 之外」的
    /// 计划任务喂给调度器，确认它被跳过而不是尝试执行后失败——否则闸门只挡住了手动操作，
    /// 无人看管运行的计划任务反而绕过了边界（设计要点）。
    /// 用 IBackupConfigService.CreateAsync（服务层，不经过带闸门的端点）直接写入越界配置，
    /// 模拟「设置根之前就存在的旧配置」这一被设计明确保留（而非删除）的场景。
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
            // 若边界检查缺失，调度器会真去跑一次备份，拿假账户信息必然失败，落库 Error。
            // 停在 Normal/无错误证明它在触碰真实执行之前就被拦下了。
            Assert.Equal(BackupStatus.Normal, reloaded!.Status);
            Assert.Null(reloaded.LastError);
        }

        // 忙碌锁必须已释放（正常 finally 路径），而不是卡死在「跳过」这条分支里。
        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();
        Assert.True(busy.TryAcquire(acct.Id, container));
        busy.Release(acct.Id, container);
    }

    /// <summary>
    /// F1：调度器边界跳过必须留下操作员能在 UI 看到的痕迹（与忙碌跳过分支同形），
    /// 不能只是一条容器日志里的 LogError——单用户无人值守部署下没人会翻它。
    /// 断言写入的是 Error 级别、source 带 account+container 维度、消息同时点名违规的
    /// 本地根与当前配置的根，二者缺一不可才「actionable」。
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

    /// <summary>F2：/run 的边界闸门（BackupConfigEndpoints.cs :179）目前没有任何回归测试——
    /// 删掉那一行不会让任何测试变红。</summary>
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

    /// <summary>F2：/restore 的边界闸门（BackupConfigEndpoints.cs :202）目前没有任何回归测试。
    /// TargetRoot 留空 → 端点落回 config.LocalRoot（越界值），闸门必须照样拦下。</summary>
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

    /// <summary>F2：/repair 的边界闸门（BackupConfigEndpoints.cs :372）目前没有任何回归测试。</summary>
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

    /// <summary>F2：/check 的边界闸门（BackupConfigEndpoints.cs :422）目前没有任何回归测试。</summary>
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
}
