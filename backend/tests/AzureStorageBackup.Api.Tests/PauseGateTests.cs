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
}
