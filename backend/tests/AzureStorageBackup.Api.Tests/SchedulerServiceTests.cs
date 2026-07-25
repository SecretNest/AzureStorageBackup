using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Task 5 复审 Finding 2/3：TickAsync 的密钥环跳过分支此前零覆盖。这里直接驱动
/// internal 的 TickAsync（经 InternalsVisibleTo 对测试程序集开放，不必为测试把
/// 生产代码方法开成 public，也不必走完整 BackgroundService 生命周期/等待轮询）。
/// 断言两件事：①密钥环丢失时到期任务不被触发（LastRunAt 不写入）；②即便丢失，
/// 短存日志清理仍照常执行（trimming 与凭据无关，被移到闸门之前，见 Finding 2）。
/// </summary>
public class SchedulerServiceTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private SchedulerService BuildScheduler() => new(
        factory.Services.GetRequiredService<IServiceScopeFactory>(),
        factory.Services.GetRequiredService<TaskDispatcher>(),
        factory.Services.GetRequiredService<IConfiguration>(),
        factory.Services.GetRequiredService<ILogger<SchedulerService>>(),
        factory.Services.GetRequiredService<VerboseFileLog>(),
        factory.Services.GetRequiredService<IKeyringHealth>());

    [Fact]
    public async Task TickAsync_Skips_Due_Task_But_Still_Trims_Ephemeral_Logs_When_Keyring_Lost()
    {
        var keyring = factory.Services.GetRequiredService<IKeyringHealth>();
        var scheduler = BuildScheduler();

        int taskId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.LogEntries.Add(new LogEntry
            {
                Timestamp = DateTimeOffset.UtcNow.AddDays(-30), // 远超默认 14 天短存窗口
                Level = OperationLogLevel.Info,
                Source = "scheduler-gate-test",
                Message = "old ephemeral entry",
                Ephemeral = true,
            });
            var task = new ScheduledTask
            {
                TargetKind = TaskTargetKind.Backup,
                AccountId = 1,
                ContainerName = "scheduler-gate-test-container",
                TaskType = ScheduledTaskType.Backup,
                CronExpression = "* * * * *", // 每分钟，创建时间在过去 → 立即到期
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            };
            db.ScheduledTasks.Add(task);
            await db.SaveChangesAsync();
            taskId = task.Id;
        }

        keyring.Set(KeyringStatus.Lost);
        try
        {
            await scheduler.TickAsync(CancellationToken.None);
        }
        finally
        {
            keyring.Set(KeyringStatus.Healthy);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // 日志清理与凭据无关，密钥环丢失也照常执行（Finding 2）。
            Assert.False(await db.LogEntries.AnyAsync(e => e.Source == "scheduler-gate-test"));

            // 到期任务被跳过，未触发（LastRunAt 仍为 null）——闸门确实生效（Finding 1/3）。
            var reloaded = await db.ScheduledTasks.FindAsync(taskId);
            Assert.Null(reloaded!.LastRunAt);
        }
    }

    [Fact]
    public async Task TickAsync_Dispatches_Due_Task_When_Keyring_Healthy()
    {
        var keyring = factory.Services.GetRequiredService<IKeyringHealth>();
        var scheduler = BuildScheduler();

        int taskId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var task = new ScheduledTask
            {
                TargetKind = TaskTargetKind.Backup,
                AccountId = 1,
                ContainerName = "scheduler-healthy-test-container",
                TaskType = ScheduledTaskType.Backup,
                CronExpression = "* * * * *",
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            };
            db.ScheduledTasks.Add(task);
            await db.SaveChangesAsync();
            taskId = task.Id;
        }

        Assert.Equal(KeyringStatus.Healthy, keyring.Status); // 确认基线：本测试类未被前一测试污染
        await scheduler.TickAsync(CancellationToken.None);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reloaded = await verifyDb.ScheduledTasks.FindAsync(taskId);
        Assert.NotNull(reloaded!.LastRunAt); // 到期即触发（LastRunAt 先于后台 dispatch 写入，不依赖 dispatch 是否成功）
    }
}
