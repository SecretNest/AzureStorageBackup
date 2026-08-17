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

    /// <summary>
    /// The mirror image of <see cref="Trouble_Clearing_Leaves_A_User_Pause_Standing"/>, and at least as likely in
    /// production: the run is <em>already</em> backing off when the operator presses Pause. That ordering takes
    /// PauseByUser's other branch — the gate is closed already, so the pause records itself on top of the existing
    /// closure instead of creating one — and it is the trouble's own timer, started before the hold existed, that
    /// has to find the hold when it fires and release nobody.
    /// </summary>
    [Fact]
    public async Task Pausing_On_Top_Of_A_Backoff_Holds_The_Gate_When_The_Timer_Fires()
    {
        using var gate = new PauseGate(
            schedule: [TimeSpan.FromMilliseconds(150)], steady: TimeSpan.FromMilliseconds(150),
            patience: TimeSpan.FromSeconds(30));

        var trouble = gate.WaitAsync(new IOException("blip"), CancellationToken.None);
        Assert.Equal(PauseSource.TransientError, gate.Current!.Source);

        gate.PauseByUser();   // inside the backoff: opening the gate is synchronous, so this lands first
        var parked = gate.WaitIfPausedAsync(CancellationToken.None);   // a stage reaching the gate in the meantime

        await Task.Delay(500);   // the 150 ms backoff has long since fired
        Assert.False(trouble.IsCompleted, "a backoff timer must not release a gate the user is holding");
        Assert.False(parked.IsCompleted);
        Assert.True(gate.IsPausedByUser);

        // Once the backoff is spent the user is the whole reason, and the gate says so: no countdown, no
        // Retry-now to offer. The failure that happened is still reported rather than zeroed away.
        Assert.Equal(PauseSource.User, gate.Current!.Source);
        Assert.Null(gate.Current.NextRetryAt);
        Assert.Equal(1, gate.Current.Failures);

        gate.ResumeByUser();
        Assert.True(await trouble.WaitAsync(TimeSpan.FromSeconds(5)));
        await parked.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(gate.Current);
    }

    /// <summary>
    /// The patience clock measures "nothing recovered despite retrying". A user pause fabricates that evidence,
    /// because under the hold nobody is permitted to retry at all.
    /// <para>
    /// The scene: Pause is pressed, then an upload that was already on the wire fails. Its backoff fires and
    /// self-suppresses under the hold — leaving no timer to re-check patience — and much later a second in-flight
    /// upload fails. That second failure's <c>OpenLocked</c> used to read the stale trouble clock and suspend the
    /// run while the operator was away, which is exactly what design §4 promises can never happen.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_Failure_Under_A_User_Pause_Cannot_Auto_Suspend_The_Run()
    {
        using var gate = new PauseGate(
            schedule: [TimeSpan.FromMilliseconds(20)], steady: TimeSpan.FromMilliseconds(20),
            patience: TimeSpan.FromMilliseconds(100));

        gate.PauseByUser();
        var first = gate.WaitAsync(new IOException("blip"), CancellationToken.None);

        // Long enough for the 20 ms backoff to have fired and self-suppressed under the hold, and for the
        // 100 ms patience to have "expired" several times over while no worker was allowed to try anything.
        await Task.Delay(400);
        Assert.False(first.IsCompleted, "the user's hold still parks the first worker");

        var second = gate.WaitAsync(new IOException("still down"), CancellationToken.None);

        Assert.False(gate.IsDowngraded, "a user pause must never downgrade the run");
        Assert.False(second.IsCompleted, "the second failure must park too, not be told to suspend");

        // And the run really does carry on when the operator comes back — the downgrade was not merely deferred.
        await Task.Delay(200);   // let the second failure's backoff fire and self-suppress as well
        gate.ResumeByUser();
        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>
    /// The other half of the same coin: a pause must not spend the patience budget it never used. Pause overnight
    /// after one failure, press Resume, and the first retry fails — the run has to get a backoff, not an instant
    /// downgrade, because that retry is the first chance it has been given since the hold went up.
    /// </summary>
    [Fact]
    public async Task Resuming_Gives_The_Run_Its_Full_Patience_Back()
    {
        using var gate = new PauseGate(
            schedule: [TimeSpan.FromMilliseconds(150)], steady: TimeSpan.FromMilliseconds(150),
            patience: TimeSpan.FromMilliseconds(250));

        var trouble = gate.WaitAsync(new IOException("blip"), CancellationToken.None);
        gate.PauseByUser();   // inside the backoff: opening the gate is synchronous, so this lands first

        await Task.Delay(700);   // the backoff fired under the hold; the patience window is long past
        Assert.False(trouble.IsCompleted, "the user's hold outranks the backoff timer");

        gate.ResumeByUser();
        Assert.True(await trouble.WaitAsync(TimeSpan.FromSeconds(5)));

        // The retry the operator just authorised fails. This is the run's first actual retry, so it deserves
        // the full patience window; before the fix the clock had been running throughout the pause.
        var retry = gate.WaitAsync(new IOException("blip"), CancellationToken.None);
        Assert.False(gate.IsDowngraded, "the pause must not have consumed the patience budget");
        Assert.True(await retry.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(gate.IsDowngraded);
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

    /// <summary>
    /// A standing user hold has to be knowable even while <see cref="PauseGate.Current"/> is reporting a
    /// transient-error backoff, because Current carries one source and the backoff wins it. Pressing Pause during
    /// a backoff would otherwise be invisible for up to one steady interval — five minutes by default — and the
    /// frontend, which renders paused-ness from the pause it is given, would show a paused run as "stuck,
    /// retrying in 4:37" with a Retry-now button, i.e. as if the Pause had done nothing at all.
    /// </summary>
    [Fact]
    public void A_User_Pause_Is_Visible_While_A_Backoff_Is_Running()
    {
        using var gate = new PauseGate(
            schedule: [TimeSpan.FromHours(1)], steady: TimeSpan.FromHours(1), patience: TimeSpan.FromHours(1));
        Assert.False(gate.IsPausedByUser);

        var trouble = gate.WaitAsync(new IOException("network down"), CancellationToken.None);
        gate.PauseByUser();

        Assert.True(gate.IsPausedByUser, "the operator pressed Pause and nothing has lifted the hold");

        // The other half of the truth is left where it was on purpose: the run IS also in a backoff, and both
        // facts are now readable. Which of them to show, and how, is the frontend's problem.
        Assert.Equal(PauseSource.TransientError, gate.Current!.Source);
        Assert.NotNull(gate.Current.NextRetryAt);
        Assert.False(trouble.IsCompleted);   // released by the gate's Dispose; nothing here needs to await it
    }

    /// <summary>
    /// A downgrade ends the user's hold along with everything else — it is the run agreeing to suspend, not a
    /// pause any more. Inert while nothing could observe the flag; a trap the moment <c>IsPausedByUser</c> exists,
    /// because a suspended run would go on claiming to be paused.
    /// </summary>
    [Fact]
    public void A_Downgrade_Ends_The_User_Hold()
    {
        using var gate = new PauseGate();
        gate.PauseByUser();
        Assert.True(gate.IsPausedByUser);

        gate.Downgrade();

        Assert.True(gate.IsDowngraded);
        Assert.False(gate.IsPausedByUser, "a downgraded gate must not go on claiming a user hold");
    }

    /// <summary>
    /// A downgraded gate refuses the hold, and says so. Both calls used to return nothing at all, and the endpoint
    /// above them reported 204 whatever happened — so pressing Pause on a run that was winding down (a Suspend or a
    /// Stop, whose run stays <c>Running</c> for as long as the upload in hand takes, or a patience auto-suspend)
    /// told the operator the run was held while nothing whatsoever had happened.
    /// </summary>
    [Fact]
    public void A_Downgraded_Gate_Refuses_To_Be_Paused_Or_Resumed()
    {
        using var gate = new PauseGate();
        gate.Downgrade();

        Assert.False(gate.PauseByUser(), "a run that is winding down cannot be held");
        Assert.False(gate.IsPausedByUser);
        Assert.False(gate.ResumeByUser(), "there is no hold on a downgraded gate to lift");
    }

    /// <summary>
    /// The two ordinary answers, which are what make the refusal above mean something: a gate that can be held
    /// answers true, and it answers true again to a second press — the hold the operator asked for is standing,
    /// which is the question they asked. Resume answers false when nobody is holding the run.
    /// </summary>
    [Fact]
    public void Pausing_And_Resuming_Report_Whether_The_Hold_Stands()
    {
        using var gate = new PauseGate();

        Assert.False(gate.ResumeByUser(), "nobody has pressed Pause");
        Assert.True(gate.PauseByUser());
        Assert.True(gate.PauseByUser(), "pressing Pause twice leaves the run held, which is a success");
        Assert.True(gate.ResumeByUser());
        Assert.False(gate.ResumeByUser(), "the hold was already lifted");
    }
}
