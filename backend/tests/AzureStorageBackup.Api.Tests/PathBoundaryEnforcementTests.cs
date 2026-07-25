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
}
