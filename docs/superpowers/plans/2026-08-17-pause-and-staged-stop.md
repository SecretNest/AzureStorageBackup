# Pause and staged stop — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a stop stop the stages whose in-flight work is worth finishing and abandon the ones whose is not, and add a real Pause that holds the run in memory instead of tearing it down.

**Architecture:** Two halves that share one mechanism. The staged stop is one extra token in an existing linked source — `feeding` already covers exactly the prober and the compressor. Pause extends `PauseGate`, already the gate every worker passes, with a second reason to be closed that has no self-heal timer and never downgrades; four producing loops gain one gate check each.

**Tech Stack:** C# / .NET, xUnit, React + TypeScript, vitest. Integration tests run against Azurite.

## Global Constraints

- **Everything written into the repository is English** — code, comments, commit messages, docs. (Conversation with the user stays Chinese.)
- **Commit messages end with:** `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`
- **Integration tests need Azurite**: `npx azurite --skipApiVersionCheck`. Without it 189 tests skip silently and a green run means nothing. **If integration tests suddenly take minutes instead of seconds, Azurite's data directory has bloated — `pkill -f azurite`, `rm -rf` its location, restart. A bloated store times tests out in a way indistinguishable from a code bug.**
- **Backend suite baseline on `main` is 1241 passed, 0 failed, 0 skipped.** Frontend is `npx tsc -b` (not `--noEmit`, which passes vacuously) plus 59 vitest tests.
- **Do not change** index or journal formats, or `StagingArea`'s quota semantics.
- **`RunStatus` does not gain a `Paused` member.** A paused run is genuinely still `Running`; paused-ness is carried by `PauseInfo.Source`.
- Spec: `docs/pause-and-staged-stop-design.md`.

## File Structure

| File | Responsibility |
|---|---|
| `backend/src/AzureStorageBackup.Api/Services/PauseGate.cs` | Gains a second closure reason (user), its source on `PauseInfo`, and a pass-through wait. |
| `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs` | One extra token on `feeding`; four gate checks at the tops of the producing loops. |
| `backend/src/AzureStorageBackup.Api/Services/BackupRunner.cs` | `Pause`/`Resume` entry points beside the existing `RetryNow`. |
| `backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs` | `POST /{id}/pause`, `POST /{id}/resume`. |
| `backend/src/AzureStorageBackup.Api/Models/BackupConfigDtos.cs` | `PauseInfo`'s source on the wire. |
| `frontend/src/lib/pauseDisplay.ts` | **New.** Pure function deciding what a pause renders as — the file that carries the tests, per this repo's convention that components are not unit-tested. |
| `frontend/src/pages/BackupConfigsPage.tsx` | Pause/Resume buttons and the paused rendering. |
| `frontend/src/api/backupConfigs.ts` | `pause`/`resume` calls, `PauseInfo.source`. |

---

### Task 1: A stop that stops the right stages

The whole of this task is one argument, and a test that would have caught its absence. `feeding` (`BackupOrchestrator.cs:800`) is linked to `working` and `downstreamGone` and used by exactly the prober and the compressor; the uploaders use `working`. Adding `StopToken` — which `RequestStop` fires for every stop kind (`BackupRunControl.cs:141`) — makes Suspend and Finish-current-files abandon the two stages whose in-flight work is discarded anyway, while the uploaders finish the volume in hand and journal it.

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs:800`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/CompressionContinuityTests.cs`

**Interfaces:**
- Consumes: `BackupRunControl.StopToken` (`BackupRunControl.cs:74`), already public.
- Produces: no new API.

- [ ] **Step 1: Write the failing test**

Add to `CompressionContinuityTests.cs`, following that file's existing conventions (`[SkippableFact]` + `Skip.IfNot`, the `Build`/`WaitUntil`/`BlockingUploader` helpers as they actually exist — check their current signatures rather than assuming):

```csharp
/// <summary>
/// A suspend must not wait for work it is about to throw away. The probe reads a whole candidate file
/// to derive a content identity that is persisted nowhere, and the compressor's output goes into a
/// queue the suspend tail drains — so finishing either is pure cost, paid in a stretch where the
/// operator is watching a progress bar that has stopped meaning anything. Only the upload in flight is
/// worth finishing, because only it can be journalled.
/// </summary>
[SkippableFact]
public async Task A_Suspend_Does_Not_Wait_For_The_Feeding_Stages()
{
    Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
    Skip.IfNot(SevenZip(), "7z executable not available");

    // Enough incompressible bytes that a compression is always in progress when the suspend lands.
    for (var i = 0; i < 12; i++)
        WriteFile($"f{i}.bin", FileSize);

    var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var factory = new BlobClientFactory(TestSecrets.Reader);
    var name = RandomName("cont");
    var (orchestrator, staging, request) = Build(
        new BlockingUploader(block.Task, new BlobUploader(factory)),
        stagingLimit: 200_000_000, uploadConcurrency: 2, container: name);

    var journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
    await using var control = new BackupRunControl(journals, configId: 1, runId: "staged-stop");

    var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
    try
    {
        var run = orchestrator.RunAsync(request, progress: null, ct: default, control: control);
        await WaitUntil(
            () => staging.StagedBytes > 2L * FileSize, TimeSpan.FromSeconds(60),
            () => $"the pipeline never got going; staged={staging.StagedBytes}.");

        var pressed = System.Diagnostics.Stopwatch.StartNew();
        control.RequestStop(StopKind.Suspend);
        block.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        pressed.Stop();

        // The bound is the point: without the StopToken link the run has to finish compressing whatever
        // 7z had in hand, which for these files is seconds, and on a real backup is minutes. With it,
        // the feeding stages are cancelled and only the released upload has to drain.
        Assert.True(pressed.Elapsed < TimeSpan.FromSeconds(20),
            $"suspend took {pressed.Elapsed.TotalSeconds:F1}s — the feeding stages were not cancelled.");
        Assert.Equal(0, staging.StagedBytes);
    }
    finally
    {
        await container.DeleteIfExistsAsync();
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj \
  --filter "FullyQualifiedName~A_Suspend_Does_Not_Wait_For_The_Feeding_Stages"
```

Expected: FAIL on the elapsed-time assertion. If it passes, the fixture is not producing a compression in flight at the moment of the stop — raise `FileSize` or the file count and say so, rather than relaxing the bound.

- [ ] **Step 3: Link the stop token**

```csharp
// BackupOrchestrator.cs:800 — the comment above it explains why the two feeding stages have their own
// token; extend it to say why a stop belongs in the same category as a dead downstream.
using var feeding = CancellationTokenSource.CreateLinkedTokenSource(
    working.Token, downstreamGone.Token, control?.StopToken ?? CancellationToken.None);
```

Add to the existing comment block above it:

```
// A stop belongs here for the same reason a dead downstream does: what these two stages have in hand
// is worth nothing once the run is winding down. The probe's content identity is persisted nowhere and
// the compressor's archive goes to a queue DrainQueues is about to release, so finishing either only
// delays the stop. The uploaders stay on `working` — a volume that completes can be journalled, and
// that is the one piece of in-flight work a suspend should wait for.
// StopNow is unaffected: it fires _abort → working, which cancels the uploads too.
```

- [ ] **Step 4: Run the test, then the full suite**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj \
  --filter "FullyQualifiedName~A_Suspend_Does_Not_Wait_For_The_Feeding_Stages"
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: the new test passes; the suite reports **1242 passed, 0 failed, 0 skipped** (1241 + 1). Watch `BackupCancelModesTests`, `GracefulSuspendTests` and `BackupResumeTests` — they pin the stop semantics this touches.

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs \
        backend/tests/AzureStorageBackup.Api.Tests/CompressionContinuityTests.cs
git commit -m "$(cat <<'EOF'
fix: let a stop abandon the stages it is about to discard

Suspend used to read a large file to the end just to compute a content
identity nothing persists, and to finish a compression whose output the
suspend tail then drains. No token was cancelled for anything but StopNow,
so every stage ran its current item to completion regardless of whether
that completion meant anything.

The line was already drawn: `feeding` covers exactly the prober and the
compressor. Linking it to StopToken abandons those two while the uploaders,
on `working`, finish the volume in hand and journal it.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: `PauseGate` learns a second reason to be closed

The gate stays one gate. What changes is that it can be held closed by the user as well as by trouble, and that the two compose: pressing Pause does not stop the volume already on the wire, so that upload can still fail and add the other reason on top.

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/PauseGate.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/PauseGateTests.cs`

**Interfaces:**
- Produces:
  - `public enum PauseSource { TransientError, User }`
  - `PauseInfo` gains `PauseSource Source` as its last positional member.
  - `public void PauseByUser()`
  - `public void ResumeByUser()`
  - `public Task WaitIfPausedAsync(CancellationToken ct)`

- [ ] **Step 1: Write the failing tests**

Append to `PauseGateTests.cs` (read its existing fixtures first — it already constructs gates with short schedules and patience):

```csharp
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
```

- [ ] **Step 2: Run them to verify they fail**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj \
  --filter "FullyQualifiedName~PauseGateTests"
```

Expected: compile errors — `PauseByUser`, `ResumeByUser`, `WaitIfPausedAsync` and `PauseSource` do not exist.

- [ ] **Step 3: Implement**

```csharp
/// <summary>Why the gate is closed. The two compose; see PauseGate's remarks.</summary>
public enum PauseSource
{
    /// <summary>A worker hit a transient error. Self-heals on a timer, and downgrades if patience runs out.</summary>
    TransientError,

    /// <summary>The user pressed Pause. No timer, no patience, and it never downgrades on its own.</summary>
    User,
}

public sealed record PauseInfo(
    string Reason, DateTimeOffset Since, DateTimeOffset? NextRetryAt, int Failures, PauseSource Source);
```

Add the field and the three methods to `PauseGate`:

```csharp
/// <summary>Held closed by the user, independently of any trouble. See the remarks on ReleaseLocked.</summary>
private bool _pausedByUser;
private DateTimeOffset _userPausedSince;

/// <summary>
/// The user pressed Pause: hold the gate with no timer and no patience.
/// <para>
/// If trouble already has the gate closed, this only records the second reason — the workers are
/// already parked on the same signal, and the trouble's own timer must not release them while this
/// reason stands (see <see cref="ReleaseLocked"/>).
/// </para>
/// </summary>
public void PauseByUser()
{
    lock (_lock)
    {
        if (_downgraded || _pausedByUser)
            return;
        _pausedByUser = true;
        _userPausedSince = DateTimeOffset.UtcNow;
        if (_release is null)
        {
            _release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _current = UserPauseInfo();
        }
    }
}

/// <summary>
/// The user pressed Resume. Only lifts the user's own hold: if trouble is still keeping the gate shut,
/// the workers stay parked and the UI goes back to reporting the trouble, which is correct — the run is
/// not ready to proceed just because the operator is.
/// </summary>
public void ResumeByUser()
{
    lock (_lock)
    {
        if (!_pausedByUser)
            return;
        _pausedByUser = false;
        // _timer is non-null exactly while a transient-error pause is running its backoff.
        if (_timer is null)
            ReleaseLocked(true);
        else
            _current = _current! with { Source = PauseSource.TransientError };
    }
}

/// <summary>
/// Pass through an open gate, park at a closed one. This is the call at the top of each producing loop.
/// <para>
/// Deliberately not <see cref="WaitAsync"/>: that one means "I failed, count it against the patience"
/// and opens the gate itself. Arriving at a gate must register no failure and must never be able to
/// trigger a downgrade — otherwise merely looping would consume the run's patience.
/// </para>
/// <para>
/// The released value is ignored. A false means the gate downgraded, and the caller learns what to do
/// about that from the stop intent it checks straight afterwards, not from here.
/// </para>
/// </summary>
public async Task WaitIfPausedAsync(CancellationToken ct)
{
    ct.ThrowIfCancellationRequested();
    Task<bool> release;
    lock (_lock)
    {
        if (_release is null)
            return;
        release = _release.Task;
    }
    await release.WaitAsync(ct);
}

private PauseInfo UserPauseInfo() =>
    new("Paused by the user.", _userPausedSince, NextRetryAt: null, Failures: 0, PauseSource.User);
```

`OpenLocked`'s `_current` assignment gains the source:

```csharp
_current = new PauseInfo(cause.Message, now, now + delay, _failures, PauseSource.TransientError);
```

And `ReleaseLocked` learns that a user hold outranks a timer:

```csharp
private void ReleaseLocked(bool proceed)
{
    _timer?.Cancel();
    _timer?.Dispose();
    _timer = null;

    // A user pause outlives the trouble that happened to coincide with it. Releasing here would let a
    // backoff timer — or a Retry now aimed at the trouble — cancel a pause the user never lifted.
    // proceed: false is a downgrade, which must pierce everything: it is how Suspend reaches a parked worker.
    if (proceed && _pausedByUser)
    {
        _current = UserPauseInfo();
        return;
    }

    _current = null;
    var tcs = _release;
    _release = null;
    tcs?.TrySetResult(proceed);
}
```

- [ ] **Step 4: Run the tests, then the full suite**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj \
  --filter "FullyQualifiedName~PauseGateTests"
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: **1247 passed, 0 failed, 0 skipped** (1242 + 5). The `PauseInfo` constructor gained a member, so every construction site must be updated — the compiler will find them.

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/PauseGate.cs \
        backend/tests/AzureStorageBackup.Api.Tests/PauseGateTests.cs
git commit -m "$(cat <<'EOF'
feat: let the user hold the pause gate closed

PauseGate was already the gate every worker passes, but only on the way out
of a transient error — with a self-heal timer and a patience that
downgrades the run. A user pause needs neither, and must never downgrade.

The two reasons compose, because pressing Pause does not stop the volume
already on the wire: the gate is closed while either holds, and lifting one
does not release the other. A backoff timer firing under a standing user
pause now leaves the gate shut and reports the user as the reason.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: The four gates, and the run-level entry points

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs` (four loops), `backend/src/AzureStorageBackup.Api/Services/BackupRunner.cs` (beside `RetryNow` at `:442`)
- Test: `backend/tests/AzureStorageBackup.Api.Tests/CompressionContinuityTests.cs`

**Interfaces:**
- Consumes: `PauseGate.WaitIfPausedAsync`, `PauseByUser`, `ResumeByUser` (Task 2).
- Produces:
  - `public bool Pause(int configId)` on `BackupRunner` — false when no run is live.
  - `public bool Resume(int configId)` on `BackupRunner` — same.

- [ ] **Step 1: Write the failing test**

```csharp
/// <summary>
/// Pause holds every producing loop, and holds the work rather than discarding it — that is the whole
/// difference from Suspend. A resumed run must not recompress what it had already staged.
/// </summary>
[SkippableFact]
public async Task Pause_Holds_The_Pipeline_And_Resume_Picks_It_Up()
{
    Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
    Skip.IfNot(SevenZip(), "7z executable not available");

    for (var i = 0; i < 12; i++)
        WriteFile($"f{i}.bin", FileSize);

    var factory = new BlobClientFactory(TestSecrets.Reader);
    var name = RandomName("cont");
    var (orchestrator, staging, request) = Build(
        uploader: null, stagingLimit: 200_000_000, uploadConcurrency: 2, container: name);

    var journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
    await using var control = new BackupRunControl(journals, configId: 1, runId: "pause-hold");

    var seen = new List<StageProgress>();
    var progress = new Progress<BackupProgress>(p =>
    {
        if (p.Detail is { Stage: "Uploading" } d)
            lock (seen) seen.Add(d);
    });

    var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
    try
    {
        var run = orchestrator.RunAsync(request, progress, ct: default, control: control);
        await WaitUntil(
            () => { lock (seen) return seen.Any(s => s.Processed > 0); }, TimeSpan.FromSeconds(60),
            () => "the run never processed an item before the pause.");

        control.Gate.PauseByUser();
        // One item per stage may still be in hand; let them land, then take the reading.
        await Task.Delay(3000);
        int processedAtPause;
        lock (seen) processedAtPause = seen[^1].Processed;

        await Task.Delay(3000);
        int processedLater;
        lock (seen) processedLater = seen[^1].Processed;
        Assert.Equal(processedAtPause, processedLater);
        Assert.Equal(PauseSource.User, control.Gate.Current!.Source);

        control.Gate.ResumeByUser();
        var result = await run.WaitAsync(TimeSpan.FromMinutes(3));
        Assert.Equal(1, result.Version);
        // Held, not discarded: every item is uploaded exactly once across the pause.
        Assert.Equal(12, result.UploadedFiles);
    }
    finally
    {
        await container.DeleteIfExistsAsync();
    }
}
```

Check `BackupRunResult`'s actual member names before writing the last assertion — use whatever it calls "files uploaded in this run"; if there is no such member, assert on `seen[^1].Processed == seen[^1].Total` instead and say so in your report.

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj \
  --filter "FullyQualifiedName~Pause_Holds_The_Pipeline"
```

Expected: FAIL — `processedLater` exceeds `processedAtPause`, because nothing checks the gate yet.

- [ ] **Step 3: Add the four gate checks**

Each producing loop gains the same two lines at the top of its body. The order matters and is the same in all four: **check the stop intent, then the gate, then check the stop intent again** — a worker released by a Suspend must not take another item, and the intent is set before the gate is opened (see the spec's §5).

`ProbeLoopAsync` (`BackupOrchestrator.cs:868`), inside `while (await work.DequeueAsync(feeding.Token) is { } item)`, before `uploadTracker.BeginWork()`:

```csharp
if (control is { Stop: not StopKind.None })
    break;
// Hold here while the user has the gate closed. The check below repeats because a release can mean
// "the user resumed" or "a stop is on its way" — see PauseGate.WaitIfPausedAsync.
if (control is not null)
    await control.Gate.WaitIfPausedAsync(feeding.Token);
if (control is { Stop: not StopKind.None })
    break;
```

`CompressLoopAsync` (`:1118`) and `UploadLoopAsync` (`:1167`): the same shape at the top of each `await foreach` body, with `feeding.Token` and `working.Token` respectively. Both already have a stop check; put the gate between the existing one and a repeat of it.

`OnChangeAsync` (`:1301`), at the top, using `stopProducing.Token`:

```csharp
// The diff passes the gate too: "pause the backup" means the disk stops being read at all, not merely
// that the pipeline stops draining.
if (control is not null)
    await control.Gate.WaitIfPausedAsync(token);
```

- [ ] **Step 4: Add the runner entry points**

Beside `RetryNow` (`BackupRunner.cs:442`), following its shape exactly:

```csharp
/// <summary>Hold the run where it is: each stage finishes the item in hand and then parks. Nothing is
/// discarded and nothing is flushed — the run stays alive, holding its staging quota, until Resume.
/// A process restart loses it, which is why this does not replace Suspend.</summary>
public bool Pause(int configId) { /* find the live run's control as RetryNow does; Gate.PauseByUser(); */ }

/// <summary>Lift a user pause. If a transient error is also holding the gate, the run stays parked on
/// that and the UI keeps reporting it.</summary>
public bool Resume(int configId) { /* same lookup; Gate.ResumeByUser(); */ }
```

Read `RetryNow`'s body and mirror it — it already resolves a live run to its `BackupRunControl` and returns false when there is none.

- [ ] **Step 5: Run the test, then the full suite**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj \
  --filter "FullyQualifiedName~Pause_Holds_The_Pipeline"
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: **1248 passed, 0 failed, 0 skipped**.

- [ ] **Step 6: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs \
        backend/src/AzureStorageBackup.Api/Services/BackupRunner.cs \
        backend/tests/AzureStorageBackup.Api.Tests/CompressionContinuityTests.cs
git commit -m "$(cat <<'EOF'
feat: hold every producing loop at the pause gate

WithPauseAsync wraps an item's failure path; a pause needs its entry path,
so the diff, the prober, the compressor and the uploaders each check the
gate at the top of their loop. The stop intent is checked on both sides of
that wait: a worker released by a Suspend must not take another item.

The diff is included on purpose — "pause the backup" means the disk stops
being read at all, not merely that the pipeline stops draining.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Endpoints and the wire format

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs:328` (beside `retry-now`), `backend/src/AzureStorageBackup.Api/Models/BackupConfigDtos.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupRunEndpointsTests.cs`

**Interfaces:**
- Consumes: `BackupRunner.Pause` / `Resume` (Task 3), `PauseSource` (Task 2).
- Produces: `POST /api/backup-configs/{id}/pause`, `POST /api/backup-configs/{id}/resume`; `source` on the pause object in the run-status response.

- [ ] **Step 1: Write the failing test**

Add to `BackupRunEndpointsTests.cs`, matching how that file already exercises `retry-now`:

```csharp
/// <summary>Pausing a configuration with nothing running is a conflict, not a silent success — the
/// operator pressed a button and is owed an answer about why nothing happened.</summary>
[Fact]
public async Task Pausing_Nothing_Is_A_Conflict()
{
    using var factory = new TestWebAppFactory();
    var client = await factory.AuthenticatedClientAsync();
    var id = await CreateConfigAsync(client);

    var response = await client.PostAsync($"/api/backup-configs/{id}/pause", null);

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
}
```

Check this file's actual helpers for creating a config and an authenticated client; the names above are illustrative of the shape, not guaranteed.

- [ ] **Step 2: Run it to verify it fails**

Expected: 404, because the route does not exist.

- [ ] **Step 3: Add the endpoints**

```csharp
// A real pause: every stage finishes the item in hand and parks, and the run stays alive holding its
// staging quota. Distinct from Suspend, which tears the run down and makes the next start re-diff and
// re-probe everything. Mirrors retry-now's shape: both reach into a live run's gate.
group.MapPost("/{id:int}/pause", (int id, BackupRunner runner) =>
    runner.Pause(id)
        ? Results.NoContent()
        : Results.Conflict(new { error = "No backup is running." }));

group.MapPost("/{id:int}/resume", (int id, BackupRunner runner) =>
    runner.Resume(id)
        ? Results.NoContent()
        : Results.Conflict(new { error = "This backup is not paused." }));
```

Then carry `Source` on whatever DTO maps `PauseInfo` onto the run-status response — the compiler will point at it once `PauseInfo` has the extra member. Serialise it as the enum name (`"User"` / `"TransientError"`), matching how the other enums on that response are already written.

- [ ] **Step 4: Run the test, then the full suite**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: **1249 passed, 0 failed, 0 skipped**. `AnonymousEndpointInventoryTests` exists to catch new routes that forgot authentication — if it fails, the new routes are outside the authenticated group and must move inside it.

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs \
        backend/src/AzureStorageBackup.Api/Models/BackupConfigDtos.cs \
        backend/tests/AzureStorageBackup.Api.Tests/BackupRunEndpointsTests.cs
git commit -m "$(cat <<'EOF'
feat: expose pause and resume

Both mirror retry-now: reach into a live run's gate, and answer a conflict
rather than a silent success when there is no run to act on. The run-status
response carries which reason holds the gate, so the UI can tell a pause
the operator can lift from one only the network can.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: The frontend

This repository does not unit-test components; logic that deserves a test is extracted into `frontend/src/lib/` as a pure function and tested there (`stageLines.ts`, `runSummary.ts`, `interruptedNotice.ts` are the precedents). Follow that.

**Files:**
- Create: `frontend/src/lib/pauseDisplay.ts`, `frontend/src/lib/pauseDisplay.test.ts`
- Modify: `frontend/src/api/backupConfigs.ts`, `frontend/src/pages/BackupConfigsPage.tsx`

**Interfaces:**
- Consumes: `source` on the pause object (Task 4).
- Produces:
  - `export type PauseSource = 'TransientError' | 'User'`
  - `export function pauseDisplay(pause: PauseInfo | null): { label: string; canResume: boolean; canRetryNow: boolean } | null`

- [ ] **Step 1: Write the failing tests**

```typescript
import { describe, expect, test } from 'vitest'
import { pauseDisplay } from './pauseDisplay'
import type { PauseInfo } from '../api/backupConfigs'

const pause = (source: PauseInfo['source'], reason = 'network down'): PauseInfo =>
  ({ reason, since: '2026-08-17T10:00:00Z', nextRetryAt: null, failures: 1, source }) as PauseInfo

describe('pauseDisplay', () => {
  test('a run that is not paused renders nothing', () => {
    expect(pauseDisplay(null)).toBeNull()
  })

  /**
   * The two reasons offer different actions, which is the whole point of carrying the source: a
   * transient-error pause is waiting on a timer the user can skip, and a user pause is waiting on the
   * user. Offering "Retry now" for a pause nobody is retrying would be nonsense.
   */
  test('a user pause offers Resume and not Retry now', () => {
    const d = pauseDisplay(pause('User'))!
    expect(d.canResume).toBe(true)
    expect(d.canRetryNow).toBe(false)
    expect(d.label).toBe('Paused')
  })

  test('a transient-error pause offers Retry now and not Resume', () => {
    const d = pauseDisplay(pause('TransientError'))!
    expect(d.canResume).toBe(false)
    expect(d.canRetryNow).toBe(true)
    expect(d.label).toContain('network down')
  })

  /**
   * An older backend sends no source. Treating that as a user pause would put a Resume button on a
   * run nobody paused; the transient-error reading is the safe default because it is what every pause
   * meant before this field existed.
   */
  test('a pause with no source reads as a transient error', () => {
    const d = pauseDisplay({ ...pause('User'), source: undefined } as unknown as PauseInfo)!
    expect(d.canRetryNow).toBe(true)
    expect(d.canResume).toBe(false)
  })
})
```

- [ ] **Step 2: Run them to verify they fail**

```bash
cd frontend && npx vitest run src/lib/pauseDisplay.test.ts
```

Expected: module not found.

- [ ] **Step 3: Implement**

```typescript
import type { PauseInfo } from '../api/backupConfigs'

/**
 * What a paused run renders as, and which actions it offers.
 *
 * The gate can be held closed for two reasons and they call for different buttons: a transient-error
 * pause is counting down a timer the operator may skip, while a user pause is waiting on the operator
 * and has no timer at all. Rendering one as the other puts a button on screen that cannot do anything.
 *
 * A pause with no source at all comes from a backend older than this field. It reads as a transient
 * error because that is what every pause was before — assuming the other way would offer Resume on a
 * run nobody paused.
 */
export function pauseDisplay(
  pause: PauseInfo | null,
): { label: string; canResume: boolean; canRetryNow: boolean } | null {
  if (!pause)
    return null
  if (pause.source === 'User')
    return { label: 'Paused', canResume: true, canRetryNow: false }
  return { label: `Paused — ${pause.reason}`, canResume: false, canRetryNow: true }
}
```

- [ ] **Step 4: Wire the API and the buttons**

In `backupConfigs.ts`: add `source?: PauseSource` to `PauseInfo`, export the `PauseSource` union, and add the two calls beside the existing `retryNow`:

```typescript
pause: (id: number) => api.post(`/backup-configs/${id}/pause`),
resume: (id: number) => api.post(`/backup-configs/${id}/resume`),
```

In `BackupConfigsPage.tsx`, in `RunStatus`'s running branch: a **Pause** button when the run is not paused, and when `pauseDisplay(...)` returns a value, its label plus **Resume** (when `canResume`), **Retry now** (when `canRetryNow`), and the existing Suspend and Stop. Show what the pause is holding beside the label using the staged figure already on the detail — the same number the in-flight line calls "ready to upload":

```
Paused · holding 4.2 GB of staging
```

That figure is not decoration: a paused run keeps its compressed output on disk, and that quota is
process-wide. There is deliberately no timeout, so the cost is stated instead.

- [ ] **Step 5: Verify and commit**

```bash
cd frontend && npx tsc -b && npx vitest run
```

Expected: `tsc -b` silent (it is a build, not `--noEmit`, which passes vacuously), **63 tests passed** (59 + 4).

```bash
git add frontend/src/lib/pauseDisplay.ts frontend/src/lib/pauseDisplay.test.ts \
        frontend/src/api/backupConfigs.ts frontend/src/pages/BackupConfigsPage.tsx
git commit -m "$(cat <<'EOF'
feat: pause and resume from the backups page

The two pause reasons offer different actions — a transient-error pause is
counting down a timer the operator can skip, a user pause is waiting on the
operator — so the decision is a pure function with its own tests rather
than a condition buried in the row.

The paused row states what it is holding. A paused run keeps its compressed
output on disk and that quota is process-wide, and there is deliberately no
timeout, so the cost belongs on screen where the operator can weigh it.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: Paused → Suspend

The transition the spec promises and nothing yet proves: from a paused run, a suspend must reach the parked workers and produce a resumable journal.

**Files:**
- Test: `backend/tests/AzureStorageBackup.Api.Tests/CompressionContinuityTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-5. No production code should need to change; if it does, that is a finding about the design — report it rather than editing to fit.

- [ ] **Step 1: Write the test**

```csharp
/// <summary>
/// Paused is not a dead end. Suspend from it must reach workers parked at the gate — which is why
/// Downgrade pierces a user pause — and leave a journal the next run can pick up.
/// </summary>
[SkippableFact]
public async Task A_Paused_Run_Can_Still_Be_Suspended()
{
    Skip.IfNot(AzuriteReachable(), "Azurite is not running on 127.0.0.1:10000");
    Skip.IfNot(SevenZip(), "7z executable not available");

    for (var i = 0; i < 12; i++)
        WriteFile($"f{i}.bin", FileSize);

    var factory = new BlobClientFactory(TestSecrets.Reader);
    var name = RandomName("cont");
    var (orchestrator, staging, request) = Build(
        uploader: null, stagingLimit: 200_000_000, uploadConcurrency: 2, container: name);

    var journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
    await using var control = new BackupRunControl(journals, configId: 1, runId: "paused-suspend");

    var container = factory.CreateServiceClient(AzuriteAccount()).GetBlobContainerClient(name);
    try
    {
        var run = orchestrator.RunAsync(request, progress: null, ct: default, control: control);
        await WaitUntil(
            () => staging.StagedBytes > 0, TimeSpan.FromSeconds(60),
            () => "the pipeline never got going before the pause.");

        control.Gate.PauseByUser();
        await Task.Delay(2000);

        // Intent first, then release — a worker woken before the intent is set would take another item.
        control.RequestStop(StopKind.Suspend);
        control.Gate.ReleaseNow();

        await Assert.ThrowsAnyAsync<BackupSuspendedException>(
            () => run.WaitAsync(TimeSpan.FromMinutes(2)));
        Assert.Equal(0, staging.StagedBytes);
    }
    finally
    {
        await container.DeleteIfExistsAsync();
    }
}
```

Note: `RequestStop` already calls `Gate.Downgrade()` (`BackupRunControl.cs:140`), so the explicit `ReleaseNow` above may be redundant — check while implementing. If it is, drop it and say so; if it is not, that means `Downgrade` is not reaching the parked workers and it is a real finding.

- [ ] **Step 2: Run it, then the whole suite**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj \
  --filter "FullyQualifiedName~A_Paused_Run_Can_Still_Be_Suspended"
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: **1250 passed, 0 failed, 0 skipped**.

- [ ] **Step 3: Commit**

```bash
git add backend/tests/AzureStorageBackup.Api.Tests/CompressionContinuityTests.cs
git commit -m "$(cat <<'EOF'
test: pin that a paused run can still be suspended

Paused must not be a dead end. This is the assertion behind Downgrade
piercing a user pause: a suspend has to reach workers parked at the gate,
and leave a journal the next run can pick up.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-review notes

**Spec coverage.** §1 staged stop → Task 1. §2 `PauseGate`'s second reason → Task 2. §3 four gates → Task 3. §4 what the operator sees, and the two endpoints → Tasks 4 and 5. §5 Paused → Suspend/Stop → Task 6 (Suspend; Stop shares the mechanism and the same `Downgrade` path). "What this does not do" needs no task — a restart losing a pause is a consequence of it being memory state, and the absence of a timeout is the absence of code.

**Where this plan is likely to be wrong.** Three places, flagged rather than hidden: `BackupRunner.Pause`/`Resume`'s bodies are described as "mirror `RetryNow`" rather than spelled out, because the live-run lookup is that method's and copying it blind is worse than reading it; `BackupRunResult`'s member for "files uploaded" is named from memory in Task 3 and must be checked; and Task 6's explicit `ReleaseNow` may be redundant given `RequestStop` already downgrades — the step says to check rather than assume, and says what it means if the check goes the other way.

**Suite counts.** 1241 → 1242 (T1) → 1247 (T2) → 1248 (T3) → 1249 (T4) → 1250 (T6), plus frontend 59 → 63 (T5).
