using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class BackupRunStateTests
{
    [Fact]
    public void Response_carries_run_id_and_no_pause_by_default()
    {
        var state = new BackupRunState();
        var response = BackupRunResponse.From(state);

        Assert.Equal("Running", response.Status);
        Assert.False(string.IsNullOrEmpty(response.RunId));
        Assert.Null(response.Pause);
        Assert.Null(response.SuspendReason);
    }

    // While paused the status is still Running (a sub-state), or the scheduler would conclude the round had ended and start another one on top of it.
    [Fact]
    public async Task Paused_run_is_still_reported_as_running()
    {
        var store = new BackupJournalStore(Path.Combine(Path.GetTempPath(), "asb-rs-" + Guid.NewGuid().ToString("N")));
        var gate = new PauseGate(
            schedule: [TimeSpan.FromMinutes(5)], steady: TimeSpan.FromMinutes(5), patience: TimeSpan.FromHours(1));
        await using var control = new BackupRunControl(store, 1, "run-x", gate);
        var state = new BackupRunState { Control = control };

        _ = gate.WaitAsync(new IOException("network down"), default);
        for (var i = 0; i < 200 && gate.Current is null; i++)
            await Task.Delay(5);

        var response = BackupRunResponse.From(state);
        Assert.Equal("Running", response.Status);
        Assert.Equal("network down", response.Pause!.Reason);
    }

    [Fact]
    public void Suspended_is_a_terminal_status_with_a_reason()
    {
        var state = new BackupRunState
        {
            Status = RunStatus.Suspended,
            SuspendReason = SuspendReason.AutoSuspended,
        };
        var response = BackupRunResponse.From(state);

        Assert.Equal("Suspended", response.Status);
        Assert.Equal("AutoSuspended", response.SuspendReason);
    }
}
