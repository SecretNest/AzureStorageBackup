using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Task 5 re-review, Findings 2/3: the keyring skip branch of TickAsync previously had zero coverage. Here we drive the
/// internal TickAsync directly (opened to the test assembly via InternalsVisibleTo, so no production method has to be
/// made public just for tests, and there is no need to go through the full BackgroundService lifecycle or wait on polling).
/// Two things are asserted: (1) when the keyring is lost, a due task is not fired (LastRunAt stays unwritten); (2) even when it is lost,
/// ephemeral log cleanup still runs as usual (trimming is unrelated to credentials and was moved ahead of the gate, see Finding 2).
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
                Timestamp = DateTimeOffset.UtcNow.AddDays(-30), // far beyond the default 14-day ephemeral window
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
                CronExpression = "* * * * *", // every minute, and created in the past → due immediately
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

            // Log cleanup is unrelated to credentials, so it runs as usual even with the keyring lost (Finding 2).
            Assert.False(await db.LogEntries.AnyAsync(e => e.Source == "scheduler-gate-test"));

            // The due task was skipped and never fired (LastRunAt is still null) — the gate really does hold (Findings 1/3).
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

        Assert.Equal(KeyringStatus.Healthy, keyring.Status); // confirm the baseline: this test class was not polluted by the previous test
        await scheduler.TickAsync(CancellationToken.None);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reloaded = await verifyDb.ScheduledTasks.FindAsync(taskId);
        Assert.NotNull(reloaded!.LastRunAt); // due means fired (LastRunAt is written before the background dispatch, regardless of whether the dispatch succeeds)
    }
}
