using System.Text.Json;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class PauseGateTests
{
    /// <summary>
    /// PauseSource's numbers are the wire contract, and until now nothing held them to it. PauseInfo goes to the
    /// browser as itself — no DTO projects the enum into a string the way BackupRunResponse does for Status and
    /// SuspendReason — and this application registers no JsonStringEnumConverter, so it serialises as a number and
    /// <c>frontend/src/api/backupConfigs.ts</c> mirrors those numbers by hand.
    /// <para>
    /// Every other assertion in this file names the members symbolically and would follow a renumbering in
    /// silence, all the way to a browser that reads <c>source: 1</c> and draws the wrong half of the pause UI.
    /// This one is deliberately written in literals on both sides of the boundary: the values, and the JSON they
    /// actually produce. The second half is the one that catches a converter being registered globally later.
    /// </para>
    /// </summary>
    [Fact]
    public void PauseSource_Crosses_The_Wire_As_The_Numbers_The_Frontend_Mirrors()
    {
        Assert.Equal(0, (int)PauseSource.TransientError);
        Assert.Equal(1, (int)PauseSource.User);

        var info = new PauseInfo("Paused by the user.", DateTimeOffset.UnixEpoch, null, 0, PauseSource.User);
        // JsonSerializerDefaults.Web is what ASP.NET's own result serialisation uses, and nothing in Program.cs
        // overrides it.
        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"source\":1", json, StringComparison.Ordinal);
    }

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
    /// <see cref="PauseGate.Snapshot"/> reports the same two facts as the two properties, which is the whole
    /// reason it exists: the response that carries them to the browser must not read them one after the other,
    /// because those are two acquisitions of the lock and a Pause or Resume landing in between yields a pair that
    /// was never true.
    /// <para>
    /// Atomicity itself is not what this pins — a race is not a thing a test can assert without measuring luck.
    /// What it pins is the mistake that would make the single read pointless: deriving "the user is holding it"
    /// from <c>Current.Source</c>. In the composed state below, the state this whole pair exists for, that
    /// derivation answers <c>false</c> while the operator's hold is standing.
    /// </para>
    /// </summary>
    [Fact]
    public void One_Read_Reports_Both_Halves_Of_A_Composed_Pause()
    {
        using var gate = new PauseGate(
            schedule: [TimeSpan.FromHours(1)], steady: TimeSpan.FromHours(1), patience: TimeSpan.FromHours(1));

        Assert.Equal((null, false, false), gate.Snapshot());

        var trouble = gate.WaitAsync(new IOException("network down"), CancellationToken.None);
        gate.PauseByUser();

        var (current, byUser, _) = gate.Snapshot();
        Assert.True(byUser, "the operator's hold is standing and one read of the gate has to say so");
        Assert.Equal(PauseSource.TransientError, current!.Source);   // ...even though the backoff owns Current
        Assert.False(trouble.IsCompleted);   // released by the gate's Dispose
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

    /// <summary>
    /// "Paused" on screen used to mean only that the hold was up. It went up the instant the button was pressed,
    /// while the volumes on the wire and the file under 7z ran on for as long as they took — minutes on a slow
    /// link — and the operator was told the run was held when it plainly was not. Settled is the other half:
    /// the hold stands <b>and</b> nothing is in hand any more, so nothing will move until Resume.
    /// </summary>
    [Fact]
    public void Settled_Means_The_Hold_Stands_And_Nothing_Is_In_Hand()
    {
        using var gate = new PauseGate();
        Assert.False(gate.IsSettled, "a run nobody paused is not settled into a pause");

        var work = gate.BeginWork();
        Assert.True(gate.PauseByUser());
        Assert.False(gate.IsSettled, "a volume on the wire is in hand; the hold has not taken effect yet");

        work.Dispose();
        Assert.True(gate.IsSettled, "the last piece in hand landed and nothing else can start");

        gate.ResumeByUser();
        Assert.False(gate.IsSettled, "settled is a fact about a standing hold, and there is none");
    }

    /// <summary>
    /// A worker that parks in the middle of its item — an uploader between two volumes of one file — has stopped
    /// moving, and must not keep "Pausing…" on screen for as long as the file has volumes left.
    /// </summary>
    [Fact]
    public async Task A_Worker_Parked_Mid_Item_Is_Not_In_Hand()
    {
        using var gate = new PauseGate();
        using var work = gate.BeginWork();
        gate.PauseByUser();

        var parked = gate.ParkAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(parked.IsCompleted);
        Assert.True(gate.IsSettled, "the only worker is parked at the gate");

        gate.ResumeByUser();
        await parked.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(gate.IsSettled, "resumed: the worker is back at work and the hold is down");
    }

    /// <summary>Parking at an open gate mid-item costs nothing and leaves the worker counted as at work.</summary>
    [Fact]
    public async Task Parking_Mid_Item_At_An_Open_Gate_Returns_At_Once()
    {
        using var gate = new PauseGate();
        using var work = gate.BeginWork();
        await gate.ParkAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        gate.PauseByUser();
        Assert.False(gate.IsSettled, "the worker never left its item");
    }

    /// <summary>
    /// The failure path parks too: a volume that failed under a standing hold waits at the gate for the backoff
    /// and the hold both to lift, and while it waits it is as still as one parked on purpose.
    /// </summary>
    [Fact]
    public async Task A_Failure_Wait_Under_A_User_Pause_Is_Not_In_Hand()
    {
        using var gate = new PauseGate(
            schedule: [TimeSpan.FromMilliseconds(10)], steady: TimeSpan.FromMilliseconds(10),
            patience: TimeSpan.FromHours(1));
        using var work = gate.BeginWork();
        gate.PauseByUser();

        var waiting = gate.WaitAsync(new IOException("blip"), CancellationToken.None);
        await Task.Delay(100);   // the backoff timer has fired by now; the hold keeps the worker parked
        Assert.False(waiting.IsCompleted);
        Assert.True(gate.IsSettled);

        gate.ResumeByUser();
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(gate.IsSettled);
    }

    /// <summary>
    /// A worker blocked on something other than the gate — the compressor waiting for staging room that only an
    /// upload can free, and no upload is coming while the hold stands — steps out of the count for the wait.
    /// </summary>
    [Fact]
    public void Idling_Inside_An_Item_Steps_Out_Of_The_Count()
    {
        using var gate = new PauseGate();
        using var work = gate.BeginWork();
        gate.PauseByUser();
        Assert.False(gate.IsSettled);

        using (gate.Idle())
            Assert.True(gate.IsSettled, "blocked on room nobody will free is as still as parked");
        Assert.False(gate.IsSettled, "back from the wait, the item is in hand again");
    }

    /// <summary>The three halves the browser draws from come out of one read, for the reason Snapshot exists at all.</summary>
    [Fact]
    public void Snapshot_Reports_Settled_Beside_The_Other_Two()
    {
        using var gate = new PauseGate();
        var work = gate.BeginWork();
        gate.PauseByUser();
        var (current, byUser, settled) = gate.Snapshot();
        Assert.Equal(PauseSource.User, current!.Source);
        Assert.True(byUser);
        Assert.False(settled);

        work.Dispose();
        Assert.True(gate.Snapshot().Settled);
    }

    /// <summary>Linux's process state letter from /proc, for asserting a process really is stopped ('T') or not.</summary>
    private static string ProcState(System.Diagnostics.Process p)
    {
        // "pid (comm) S ..." — the comm may contain spaces, so read past its closing parenthesis.
        var stat = File.ReadAllText($"/proc/{p.Id}/stat");
        return stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[0];
    }

    private static async Task<string> WaitForStateAsync(System.Diagnostics.Process p, params string[] any)
    {
        var state = "";
        for (var i = 0; i < 200; i++)
        {
            state = ProcState(p);
            if (any.Contains(state))
                return state;
            await Task.Delay(10);
        }
        return state;
    }

    private static System.Diagnostics.Process Sleeper() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;

    /// <summary>
    /// The pause used to let the file under 7z finish. Now the hold stops the process where it is, and a stopped
    /// process is not what keeps the run "pausing": it is in some loop's hand, but nothing in that hand is moving.
    /// Resume lets it run again, and the run is back to unsettled.
    /// </summary>
    [SkippableFact]
    public async Task The_Hold_Stops_An_Attached_Process_And_Counts_It_Out_Of_Hand()
    {
        Skip.IfNot(ProcessHold.Supported, "process stopping is Linux-only");
        using var gate = new PauseGate();
        using var proc = Sleeper();
        try
        {
            using var work = gate.BeginWork();
            using var attached = gate.Processes.Attach(proc);
            Assert.Equal("S", await WaitForStateAsync(proc, "S"));

            gate.PauseByUser();
            Assert.Equal("T", await WaitForStateAsync(proc, "T"));
            Assert.True(gate.IsSettled, "the file under 7z is stopped; nothing in hand is moving");

            gate.ResumeByUser();
            Assert.Equal("S", await WaitForStateAsync(proc, "S"));
            Assert.False(gate.IsSettled);
        }
        finally
        {
            proc.Kill();
        }
    }

    /// <summary>A 7z that starts while the hold stands is stopped the instant it is attached, before it gets going.</summary>
    [SkippableFact]
    public async Task A_Process_Attached_Under_A_Standing_Hold_Is_Stopped_At_Once()
    {
        Skip.IfNot(ProcessHold.Supported, "process stopping is Linux-only");
        using var gate = new PauseGate();
        gate.PauseByUser();
        using var work = gate.BeginWork();
        Assert.False(gate.IsSettled);

        using var proc = Sleeper();
        try
        {
            using var attached = gate.Processes.Attach(proc);
            Assert.Equal("T", await WaitForStateAsync(proc, "T"));
            Assert.True(gate.IsSettled);
        }
        finally
        {
            proc.Kill();
        }
    }

    /// <summary>
    /// A downgrade — Suspend, Stop, the shutdown path, patience — releases the process before anything is cancelled.
    /// The compressor feeding it is blocked on a pipe only that process drains; left stopped, the wind-down would
    /// wait on a writer that never returns.
    /// </summary>
    [SkippableFact]
    public async Task A_Downgrade_Lets_A_Stopped_Process_Run_Again()
    {
        Skip.IfNot(ProcessHold.Supported, "process stopping is Linux-only");
        using var gate = new PauseGate();
        using var proc = Sleeper();
        try
        {
            using var work = gate.BeginWork();
            using var attached = gate.Processes.Attach(proc);
            gate.PauseByUser();
            Assert.Equal("T", await WaitForStateAsync(proc, "T"));

            gate.Downgrade();
            Assert.Equal("S", await WaitForStateAsync(proc, "S"));
            Assert.False(gate.Processes.IsHeld);
        }
        finally
        {
            proc.Kill();
        }
    }

    /// <summary>
    /// What the estimate subtracts: the time the hold stood with nothing in hand — "Paused", not "Pausing…". A
    /// volume still landing is real work time, and so is a piece stopped only after the hold went up; the clock
    /// runs from the moment the last piece settles to the moment Resume lets the first one move.
    /// </summary>
    [Fact]
    public void Held_Time_Runs_Exactly_While_Settled()
    {
        var now = 0L;
        using var gate = new PauseGate { Clock = () => now };
        Assert.Equal(0, gate.HeldMs);

        var work = gate.BeginWork();
        now = 1000;
        gate.PauseByUser();
        now = 2000;
        Assert.Equal(0, gate.HeldMs);   // pausing: a volume is still on the wire

        work.Dispose();                 // it landed: paused
        now = 5000;
        Assert.Equal(3000, gate.HeldMs);

        gate.ResumeByUser();
        now = 9000;
        Assert.Equal(3000, gate.HeldMs);   // resumed at 5000; nothing since counts

        gate.PauseByUser();             // nothing in hand: settled on the spot
        now = 10000;
        Assert.Equal(4000, gate.HeldMs);

        using (gate.BeginWork())        // the wrap-up's re-run, say: in hand under the hold
        {
            now = 12000;
            Assert.Equal(4000, gate.HeldMs);
        }
        now = 13000;
        Assert.Equal(5000, gate.HeldMs);
    }

    /// <summary>A transient-error backoff is not a pause the operator chose: its time stays on the estimate's clock.</summary>
    [Fact]
    public async Task Held_Time_Ignores_A_Backoff_Nobody_Pressed_Pause_For()
    {
        var now = 0L;
        using var gate = new PauseGate(
            schedule: [TimeSpan.FromSeconds(30)], steady: TimeSpan.FromSeconds(30),
            patience: TimeSpan.FromMinutes(10)) { Clock = () => now };
        var waiting = gate.WaitAsync(new IOException("blip"), default);
        for (var i = 0; i < 200 && gate.Current is null; i++)
            await Task.Delay(5);

        now = 60000;
        Assert.Equal(0, gate.HeldMs);
        gate.Downgrade();
        Assert.False(await waiting);
    }
}
