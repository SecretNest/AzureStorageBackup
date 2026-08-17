using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class PauseGateTests
{
    private static PauseGate Fast(TimeSpan? patience = null) => new(
        schedule: [TimeSpan.FromMilliseconds(10)],
        steady: TimeSpan.FromMilliseconds(10),
        patience: patience ?? TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Self_heal_timer_releases_the_waiter()
    {
        using var gate = Fast();
        Assert.True(await gate.WaitAsync(new IOException("blip"), default));
        Assert.Null(gate.Current);
    }

    [Fact]
    public async Task Exposes_why_it_is_paused_while_waiting()
    {
        using var gate = new PauseGate(
            schedule: [TimeSpan.FromSeconds(30)], steady: TimeSpan.FromSeconds(30),
            patience: TimeSpan.FromMinutes(10));
        var waiting = gate.WaitAsync(new IOException("network down"), default);

        // Wait for it to publish the state (opening the gate is synchronous, but the waiter has not reached the await yet)
        for (var i = 0; i < 200 && gate.Current is null; i++)
            await Task.Delay(5);

        Assert.Equal("network down", gate.Current!.Reason);
        Assert.Equal(1, gate.Current.Failures);
        Assert.NotNull(gate.Current.NextRetryAt);

        gate.ReleaseNow();
        Assert.True(await waiting);
    }

    [Fact]
    public async Task Manual_push_releases_immediately()
    {
        using var gate = new PauseGate(
            schedule: [TimeSpan.FromMinutes(5)], steady: TimeSpan.FromMinutes(5),
            patience: TimeSpan.FromHours(1));
        var waiting = gate.WaitAsync(new IOException("blip"), default);
        gate.ReleaseNow();
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task All_waiters_are_released_together()
    {
        using var gate = Fast();
        var a = gate.WaitAsync(new IOException("blip"), default);
        var b = gate.WaitAsync(new IOException("blip"), default);
        var c = gate.WaitAsync(new IOException("blip"), default);
        Assert.Equal(new[] { true, true, true }, await Task.WhenAll(a, b, c));
    }

    // Patience runs out -> downgrade. The caller takes the suspend-and-exit path instead of dumbly waiting on.
    [Fact]
    public async Task Downgrades_when_patience_runs_out()
    {
        using var gate = Fast(patience: TimeSpan.Zero);
        Assert.False(await gate.WaitAsync(new IOException("blip"), default));
        Assert.True(gate.IsDowngraded);
    }

    [Fact]
    public async Task Downgraded_gate_never_waits_again()
    {
        using var gate = Fast();
        gate.Downgrade();
        Assert.False(await gate.WaitAsync(new IOException("blip"), default));
    }

    // Another worker got work done -> the network is obviously up -> failure count resets, backoff starts over, patience restarts too.
    [Fact]
    public async Task Success_resets_the_failure_count()
    {
        using var gate = Fast();
        Assert.True(await gate.WaitAsync(new IOException("blip"), default));
        Assert.True(await gate.WaitAsync(new IOException("blip"), default));

        gate.ReportSuccess();

        // Opening the gate (OpenLocked) is synchronous: calling WaitAsync runs it to completion before the first
        // await that actually suspends, so there is no await gap between that call and reading Current here —
        // the 10ms self-heal timer cannot possibly get there in time, no need to stretch the backoff to bet on an
        // observation window.
        var waiting = gate.WaitAsync(new IOException("blip"), default);

        // After the reset this one counts as "the first failure" — no disjunction escape hatch.
        Assert.NotNull(gate.Current);
        Assert.Equal(1, gate.Current!.Failures);

        gate.ReleaseNow();
        Assert.True(await waiting);
    }

    // The user pressed cancel: cancellation always wins, the gate must not swallow it.
    [Fact]
    public async Task User_cancellation_wins_over_waiting()
    {
        using var gate = new PauseGate(
            schedule: [TimeSpan.FromMinutes(5)], steady: TimeSpan.FromMinutes(5),
            patience: TimeSpan.FromHours(1));
        using var cts = new CancellationTokenSource();
        var waiting = gate.WaitAsync(new IOException("blip"), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    // A worker already on its way out via cancellation must not open the gate on the way, not even briefly publishing a phantom state for others to see.
    [Fact]
    public async Task Already_cancelled_token_throws_without_opening_the_gate()
    {
        using var gate = Fast();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.WaitAsync(new IOException("blip"), cts.Token));
        Assert.Null(gate.Current);
    }

    // A 5-minute timer must not outlive the run.
    [Fact]
    public async Task Dispose_kills_the_pending_timer()
    {
        var gate = new PauseGate(
            schedule: [TimeSpan.FromMinutes(5)], steady: TimeSpan.FromMinutes(5),
            patience: TimeSpan.FromHours(1));
        var waiting = gate.WaitAsync(new IOException("blip"), default);
        gate.Dispose();

        // Just looking at IsDowngraded cannot catch "the timer was not torn down" — DowngradeLocked sets that flag by itself.
        // What really proves Dispose took the pending 5-minute timer down with it is the waiter left hanging there:
        // it has to get the downgrade result immediately, not be left on a 5-minute Task.Delay until the end of time.
        var completed = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(waiting, completed);
        Assert.False(await waiting);
        Assert.True(gate.IsDowngraded);
    }

    /// <summary>A user pause has no timer to release it: it holds until the user says otherwise.</summary>
    [Fact]
    public async Task A_User_Pause_Holds_Until_Resumed()
    {
        using var gate = new PauseGate(
            schedule: [TimeSpan.FromMilliseconds(10)], steady: TimeSpan.FromMilliseconds(10),
            patience: TimeSpan.FromMilliseconds(50));

        gate.PauseByUser();
        var waiting = gate.WaitIfPausedAsync(CancellationToken.None);

        await Task.Delay(200);   // far longer than both the schedule and the patience
        Assert.False(waiting.IsCompleted);
        Assert.Equal(PauseSource.User, gate.Current!.Source);
        Assert.False(gate.IsDowngraded, "a user pause must never downgrade the run");

        gate.ResumeByUser();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(gate.Current);
    }

    /// <summary>An open gate is a no-op — this is the call at the top of every producing loop.</summary>
    [Fact]
    public async Task Waiting_At_An_Open_Gate_Returns_Immediately()
    {
        using var gate = new PauseGate();
        await gate.WaitIfPausedAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// The two reasons compose. A volume already on the wire when Pause is pressed can still fail, and
    /// resuming must not let the run charge back into a network that is still down.
    /// </summary>
    [Fact]
    public async Task Resuming_Does_Not_Release_A_Gate_Trouble_Still_Holds()
    {
        using var gate = new PauseGate(
            schedule: [TimeSpan.FromHours(1)], steady: TimeSpan.FromHours(1),
            patience: TimeSpan.FromHours(1));

        gate.PauseByUser();
        var trouble = gate.WaitAsync(new IOException("network down"), CancellationToken.None);

        gate.ResumeByUser();

        await Task.Delay(100);
        Assert.False(trouble.IsCompleted, "the transient-error reason still holds the gate");
        Assert.Equal(PauseSource.TransientError, gate.Current!.Source);

        gate.ReleaseNow();
        Assert.True(await trouble.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// The mirror: trouble clears on its own while the user pause stands. The gate stays closed and starts
    /// reporting the user as the reason, so the UI stops offering "Retry now" for a pause nobody can retry.
    /// </summary>
    [Fact]
    public async Task Trouble_Clearing_Leaves_A_User_Pause_Standing()
    {
        using var gate = new PauseGate(
            schedule: [TimeSpan.FromMilliseconds(20)], steady: TimeSpan.FromMilliseconds(20),
            patience: TimeSpan.FromSeconds(30));

        gate.PauseByUser();
        var trouble = gate.WaitAsync(new IOException("blip"), CancellationToken.None);

        await Task.Delay(200);   // the timer has long since fired
        Assert.False(trouble.IsCompleted);
        Assert.Equal(PauseSource.User, gate.Current!.Source);

        gate.ResumeByUser();
        Assert.True(await trouble.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>Downgrade must pierce a user pause: it is how "Suspend" reaches a parked worker.</summary>
    [Fact]
    public async Task Downgrade_Releases_A_User_Pause()
    {
        using var gate = new PauseGate();
        gate.PauseByUser();
        var waiting = gate.WaitIfPausedAsync(CancellationToken.None);

        gate.Downgrade();

        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(gate.IsDowngraded);
    }
}
