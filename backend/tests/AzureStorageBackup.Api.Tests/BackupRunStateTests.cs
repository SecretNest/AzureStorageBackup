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
        Assert.False(response.PausedByUser);
    }

    /// <summary>
    /// The scenario the review caught: the operator presses Pause while a transient-error backoff already holds
    /// the gate. <c>Pause.Source</c> keeps reporting <c>TransientError</c> — with its countdown and its Retry-now
    /// affordance — until that backoff's own timer fires, up to one steady interval. Without a separate signal, the
    /// UI would render this run as merely stuck and retrying, as if the operator's Pause had done nothing.
    /// <c>PausedByUser</c> is read live off the gate rather than baked into the stored <see cref="PauseInfo"/>,
    /// which is exactly why it can tell the truth here where <c>Pause.Source</c> cannot.
    /// </summary>
    [Fact]
    public async Task PausedByUser_reports_the_operators_hold_even_while_a_backoff_owns_the_pause_object()
    {
        var store = new BackupJournalStore(Path.Combine(Path.GetTempPath(), "asb-rs-" + Guid.NewGuid().ToString("N")));
        var gate = new PauseGate(
            schedule: [TimeSpan.FromHours(1)], steady: TimeSpan.FromHours(1), patience: TimeSpan.FromHours(1));
        await using var control = new BackupRunControl(store, 1, "run-x", gate);
        var state = new BackupRunState { Control = control };

        _ = gate.WaitAsync(new IOException("network down"), default);
        for (var i = 0; i < 200 && gate.Current is null; i++)
            await Task.Delay(5);
        gate.PauseByUser();

        var response = BackupRunResponse.From(state);
        Assert.Equal(PauseSource.TransientError, response.Pause!.Source);
        Assert.True(response.PausedByUser, "the operator's hold is standing regardless of what Source says");
    }

    /// <summary>
    /// From the index write on, the run is past the point where Pause or Suspend can do what their labels say:
    /// every upload is done, nothing in the wrap-up consults the pause gate, and the only thing left is to let it
    /// finish. Pause used to answer success here and show "Paused" over a run that went on to Completed; Suspend
    /// used to hang the request for its cap and then hand back a Completed run labelled "Suspending…". This is
    /// the one predicate both refusals key off.
    /// </summary>
    [Fact]
    public void WrappingUp_starts_at_the_index_write_and_never_before()
    {
        static BackupRunState At(BackupStage stage) =>
            new() { Progress = new BackupProgress(stage, 0, 0, 0, 0) };

        Assert.False(new BackupRunState().WrappingUp);   // nothing reported yet: a run still starting
        Assert.False(At(BackupStage.Scanning).WrappingUp);
        Assert.False(At(BackupStage.Uploading).WrappingUp);
        Assert.True(At(BackupStage.WritingIndex).WrappingUp);
        Assert.True(At(BackupStage.Finalizing).WrappingUp);
        Assert.True(At(BackupStage.CleaningUp).WrappingUp);
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

    // These figures reach the log through BackupSummary, but the page the operator actually watches a
    // backup finish on polls this response — so they have to travel this way too, or the only way to
    // learn what a round did is to go and read the log.
    [Fact]
    public void Response_carries_what_the_round_changed_and_uploaded()
    {
        var state = new BackupRunState
        {
            Status = RunStatus.Completed,
            Version = 42,
            NewFiles = 2481,
            ModifiedFiles = 130,
            DeletedFiles = 5,
            DeletedBytes = 3_221_225_472,
            ChangedBytes = 5_046_586_572,
            UploadedBytes = 851_443_712,
        };
        var response = BackupRunResponse.From(state);

        Assert.Equal(2481, response.NewFiles);
        Assert.Equal(130, response.ModifiedFiles);
        Assert.Equal(5, response.DeletedFiles);
        Assert.Equal(3_221_225_472, response.DeletedBytes);
        Assert.Equal(5_046_586_572, response.ChangedBytes);
        Assert.Equal(851_443_712, response.UploadedBytes);
    }

    // Null rather than 0 while the run is still going: 0 would read as "this round changed nothing",
    // which is a claim nobody is in a position to make until the diff has finished.
    [Fact]
    public void Figures_are_absent_until_the_run_finishes()
    {
        var response = BackupRunResponse.From(new BackupRunState());

        Assert.Null(response.NewFiles);
        Assert.Null(response.ModifiedFiles);
        Assert.Null(response.DeletedFiles);
        Assert.Null(response.DeletedBytes);
        Assert.Null(response.ChangedBytes);
        Assert.Null(response.UploadedBytes);
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
