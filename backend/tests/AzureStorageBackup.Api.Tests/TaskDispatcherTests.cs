using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// DispatchAsync is a fire-and-forget target: SchedulerService discards the task (`_ = DispatchAsync(...)`)
/// and the run-now endpoint's design comment relies on "DispatchAsync's catch reduces it to a log line".
/// Anything it lets escape therefore becomes an unobserved task exception — invisible to the scheduler's
/// own try/catch and to the operator. The cancelled-token case is the everyday one: a tick fires right as
/// the process begins a planned shutdown.
/// </summary>
public sealed class TaskDispatcherTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    [Fact]
    public async Task DispatchAsync_Swallows_Cancellation_Instead_Of_Propagating()
    {
        _ = factory.CreateClient(); // boot the host
        var dispatcher = factory.Services.GetRequiredService<TaskDispatcher>();
        var task = new ScheduledTask
        {
            TargetKind = TaskTargetKind.Backup,
            AccountId = 12345,
            ContainerName = "no-such-container",
            TaskType = ScheduledTaskType.Backup,
        };

        // A throw here is the bug: the caller never observes the task, so this exception would vanish.
        await dispatcher.DispatchAsync(task, new CancellationToken(canceled: true));
    }

    [Fact]
    public async Task DispatchAsync_Swallows_Cancellation_For_Group_Targets_Too()
    {
        _ = factory.CreateClient();
        var dispatcher = factory.Services.GetRequiredService<TaskDispatcher>();
        var task = new ScheduledTask
        {
            TargetKind = TaskTargetKind.Group,
            GroupId = 12345, // resolving the group is the first awaited call — the other escape path
            TaskType = ScheduledTaskType.Check,
        };

        await dispatcher.DispatchAsync(task, new CancellationToken(canceled: true));
    }
}
