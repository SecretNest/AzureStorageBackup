# Pause, and a staged stop that stops the right stages

## The problem

Two complaints from the same root: "stop" is one word for work whose value differs by stage.

**Suspend finishes work it is about to throw away.** Press Suspend while a large file is being probed
and the run reads that file to the end before stopping — computing a content identity that is not
persisted anywhere, so the next resume computes it again. The same is true of a compression in
progress: the archive it produces goes into `stagedQueue`, and the suspend tail drains that queue and
releases it. Only the third stage's in-flight work is worth finishing — an upload that completes can
be written to the journal, and the next run skips it.

The cause is that no token is cancelled for Suspend or Finish-current-files: `RequestStop` fires
`_abort` only for `StopNow` (`BackupRunControl.cs:141-143`), so every stage runs its current item to
completion regardless of whether that completion means anything.

**Suspend is not a pause.** Resume is a full recovery workflow: it re-scans, re-diffs, and re-probes,
because the journal's reuse test needs a content identity and a content identity can only be had by
reading the file (see `PlaceBlobAsync`'s `Resume.FindBlob` call). Suspending to step away for ten
minutes therefore costs a whole diff pass and a whole probe pass. There is no way to say "hold
everything where it is, I'll be back".

## Starting point

Everything this needs already exists in some form:

- **`PauseGate`** is already the gate every worker passes through — but only on the way out of a
  transient error, with a self-heal timer and a patience that downgrades the run to auto-suspend
  (`PauseGate.cs:29-55`). Its `WaitAsync(Exception cause, CancellationToken)` means "I hit trouble,
  park me"; there is no "park me because I was told to".
- **`feeding`** (`BackupOrchestrator.cs:800`) is a token linked to `working` and `downstreamGone` and
  used by exactly the two stages whose in-flight work is worthless at stop time — the prober and the
  compressor. The uploaders use `working`. The split this design needs is already drawn.
- **`BackupRunControl.StopToken`** (`:74`) fires for every stop kind, `AbortToken` (`:75`) only for
  `StopNow`.
- **`/retry-now`** (`BackupConfigEndpoints.cs:328`) is the precedent for an endpoint that reaches into
  a live run's gate; `PauseGate.ReleaseNow` is what it calls.
- The frontend already has a `PauseInfo` type and a `pause` field on the run status
  (`backupConfigs.ts:291,341`), and `RunStatus` renders a pause today.

## Design

### 1. A staged stop, in one line

```csharp
// BackupOrchestrator.cs:800
using var feeding = CancellationTokenSource.CreateLinkedTokenSource(
    working.Token, downstreamGone.Token, control?.StopToken ?? default);
```

Adding `StopToken` to the link makes Suspend and Finish-current-files cancel the prober and the
compressor while leaving the uploaders on `working`. 7z is killed by `SevenZipCli`'s existing
cancellation path, its partial output is deleted by `SevenZipCompressor`'s own catch, and the queues are
released by `DrainQueues` as they already are. The uploaders finish the volume in hand and write the journal,
which is the only in-flight work worth finishing.

`StopNow` is unchanged: it fires `_abort` → `working`, which cancels everything including uploads.

Two corrections found while implementing this, both now in the code. The cleanup is **not**
`MoveToStaged`'s catch — that one only fires when the move itself fails after a compression already
succeeded. It is `SevenZipCompressor`'s, and the pack path did not have one at all: it cleaned up only
on the "7z dropped a member" exit code, so a cancelled pack compression left its volumes behind. That
was unreachable in practice while interrupting a compression took `StopNow`; this change makes it
ordinary. And the cleanup both paths did have matched only *finished* volumes (`name.7z.001`), while a
cancelled 7z leaves `name.7z.001.tmp` — it renames each volume only once complete. So the file a
cancellation actually produces was precisely the one the cleanup could not see.

### 2. `PauseGate` learns a second reason to be closed

The gate stays one gate — a worker should not have to ask *why* it is parked — but it grows a second
way to be closed and a source on what it reports:

```csharp
// The numbers are the wire contract, mirrored in frontend/src/api/backupConfigs.ts. PauseInfo goes to the
// browser as itself — no DTO projects the enum into a string the way BackupRunResponse does for Status and
// SuspendReason — and no JsonStringEnumConverter is registered, so this serialises as a number, exactly as
// CloudState/LocalState already do from the same position.
public enum PauseSource { TransientError = 0, User = 1 }

public sealed record PauseInfo(
    string Reason, DateTimeOffset Since, DateTimeOffset? NextRetryAt, int Failures,
    PauseSource Source);

public void PauseByUser();      // close the gate; no timer, no patience, never downgrades
public void ResumeByUser();     // reopen, unless a transient-error reason still holds it closed
public Task WaitIfPausedAsync(CancellationToken ct);   // pass through when open, park when closed
```

The two reasons can coexist: pressing Pause does not stop the volume already on the wire, and that
upload can still fail. The gate is closed while **either** reason holds, and `ResumeByUser` clears
only the user's. A resume that finds the error reason still standing leaves the gate closed and the
UI showing the transient-error pause, which is correct — the run is not ready to proceed.

The patience clock belongs to the transient-error reason alone, and the user's hold stops it. Patience
means "the run kept retrying and nothing ever recovered"; while the hold stands no worker is permitted to
retry anything, so the clock would be reading an operator's coffee break as a network that never came
back. Two consequences, both load-bearing:

- A failure that lands during a pause parks its worker and starts its backoff, but can never downgrade
  the run — `PatienceExhausted` answers no while the hold is up. Without this, a paused run auto-suspends
  itself while the operator is away, which §4 promises can never happen.
- `ResumeByUser` clears the clock and the failure count, exactly as `Retry now` does. The retry the
  operator has just authorised is the first one the run has been allowed since the hold went up, so it
  gets the full patience window and a backoff ladder starting at its first step, rather than finding the
  budget already spent by a pause during which nothing was tried.

`WaitIfPausedAsync` is deliberately not `WaitAsync`: the existing method means "I failed, count it
against the patience". Passing through a gate must not register a failure, and must not be able to
trigger a downgrade.

### 3. Four gates, at the top of each loop

`WithPauseAsync` wraps an item's *failure* path. Pause needs the *entry* path, so each producing loop
gains one `await control.Gate.WaitIfPausedAsync(token)` at the top:

| loop | file:line (before this change) | token |
|---|---|---|
| the diff's `OnChangeAsync` | `BackupOrchestrator.cs:1301` | `stopProducing` |
| prober (`ProbeLoopAsync`) | `:868` | `feeding` |
| compressor (`CompressLoopAsync`) | `:1118` | `feeding` |
| uploader (`UploadLoopAsync`) | `:1167` | `working` |

The diff is included because "pause the backup" means the disk stops being read at all, not just that
the pipeline stops draining.

Granularity is "finish the item in hand, then hold". A press of Pause therefore takes effect within
one item per stage — worst case, the time to compress one large file. The alternative (abort mid-item
and re-queue) would need 7z killed, partial output deleted and the item re-enqueued, and the item
would be recompressed from scratch next time; that trades a real recompression for a few seconds of
responsiveness.

**One gate means these four also park on a transient-error pause**, which is a change in how the
pipeline behaves during a network blip and not only during a Pause. Before, an upload that hit trouble
parked only itself (`WithPauseAsync`), while the compressor went on filling the staging pool and the
diff went on reading the disk; now every producing loop stops at its next item boundary until the
backoff releases them — up to one steady interval, five minutes by default. That follows from §2's
decision that a worker should not have to ask *why* it is parked, and it is mostly an improvement:
compressing more output that cannot be uploaded only fills a pool that is process-wide, and it is the
pool filling up behind parked uploaders that produced the retry deadlock
`StagingArea.StageWithoutBackpressureAsync` exists to break. It is stated here because it is invisible
in the code — `WaitIfPausedAsync` deliberately does not name a reason — and because the effect is real:
`BackupPauseGateIntegrationTests.Every_uploader_retrying_at_once_against_a_full_pool_still_finishes`
assembles its scene by letting the compressor fill the pool while both uploaders sit out a backoff, and
that no longer happens (measured: with the compressor's gate removed the case passes 3/3 in ~15s; with
it, 1/3 in ~35s, its scene guard reporting a pool one archive short of the ceiling).

### 4. What the operator sees

`RunStatus` is **unchanged** — the run is genuinely still Running, it is merely holding. Paused is
expressed by `PauseInfo.Source == User` **or** `PauseGate.IsPausedByUser`, and the second one is not
redundant: `PauseInfo` carries a single source, and a pause pressed while a transient-error backoff is
running leaves the backoff reporting itself until its timer fires — up to one steady interval, five
minutes by default. Rendering from the source alone would show such a run as "stuck, retrying in 4:37",
with a Retry-now button, as if the Pause had done nothing. The two facts are kept separate rather than
having the pause overwrite the trouble, because a run really can be both, and whichever the screen
chooses to lead with, the other is still there to say. The frontend renders it as a paused run:

```
Paused 23 minutes ago · holding 4.2 GB of staging
[Resume] [Suspend] [Stop]
```

The staging figure is not decoration. A paused run keeps its compressed output on disk — that is what
makes Resume cheap — and that quota is process-wide, so a run paused overnight holds it overnight.
There is deliberately **no timeout**: an automatic downgrade would turn a pause into a suspend exactly
when the operator is not watching, which is the opposite of what they asked for. The cost is stated on
screen instead, and the operator decides. The gate's own patience is the other route to an automatic
downgrade, and §2 closes it: it does not run while the hold stands.

Two endpoints, following `/retry-now`'s shape:

```
POST /backup-configs/{id}/pause     → PauseGate.PauseByUser()
POST /backup-configs/{id}/resume    → PauseGate.ResumeByUser()
```

### 5. Paused → Suspend, and Paused → Stop

Both are "set the stop intent, then open the gate". The parked workers wake, see the intent, and take
the paths that already exist — Suspend drains and flushes the journal, Stop purges according to its
kind. No new wind-down logic.

Ordering matters: set the intent **before** opening the gate, or a worker can wake, see no intent, and
take another item.

## What this does not do

**A paused run does not survive a process restart.** Pause is memory state, which is exactly why it is
cheaper than Suspend. After a restart the run is gone and its journal is on disk, so the configuration
shows an interrupted run with a Resume button — the Suspend outcome. Pause therefore does not replace
Suspend, and the shutdown path must still suspend rather than pause.

What the hold *does* survive as is the **reason** written next to the journal. A shutdown suspends every
live run as `ShuttingDown`, and `AutoResumeService.PickResumableAsync` restarts exactly the configs whose
every volume says `ShuttingDown` — so without more than that, Pause → pull the new image → restart would
leave the backup running again, the hold gone and no record it ever existed. On a NAS the container
restart *is* the routine upgrade path, so that is not a corner case but the first thing that happens to an
operator who pauses and then updates. `BackupRunner.RequestStop` therefore records a suspension that
catches a standing user hold as `UserRequested`, whatever the caller asked for, and auto-resume leaves
those alone: the operator comes back to an interrupted run with a Resume button, which is the outcome the
paragraph above promises. The reason has to be decided there because that is the only point where the stop
request and the run's gate are both in hand, and it has to be read before `BackupRunControl.RequestStop`,
which downgrades the gate and thereby ends the hold.

**It does not make Pause instant.** See §3.

**It does not add Pause to restore, check or repair.** Those runs are short; the machinery is not
worth duplicating until one of them is measured to need it.

**It does not bound how long a pause may hold the staging quota.** By decision, see §4.

## Tests

- **Suspend stops the feeding stages at once.** With a slow probe in progress, a suspend returns
  without waiting for that read to finish, and the run still journals the volume that was on the wire.
- **Pause holds all four loops** — and each gate has to be pinned by an observable that only *it* can move,
  or three of the four ride free on the fourth. Each case builds its backlog first and makes the work
  available after the hold is already standing, because a producing stage left to run is normally blocked
  on an empty input or on backpressure rather than on its gate, and a gate nothing was going to pass is
  invisible. What each measures: upload calls (the uploader's), probe calls and staged bytes (the prober's
  and the compressor's), a settled total (the diff's).
- **Pause preserves work.** Items compressed before the pause are uploaded after the resume without
  being compressed a second time.
- **The two reasons compose.** A transient failure during a user pause leaves the gate closed after
  `ResumeByUser`, and the reported source is the transient error.
- **Paused → Suspend.** From a paused run, a suspend completes and writes a resumable journal.
- **A user pause never downgrades.** Held far longer than the gate's patience, the run stays Running
  rather than auto-suspending.
- **A pause is not undone by a restart.** A shutdown that catches a paused run marks its journal
  `UserRequested`, and the startup auto-resume declines it; an unpaused run in the same shutdown is still
  marked `ShuttingDown` and is still picked back up.
- **Pause answers honestly.** On a run that is already winding down — where the gate is downgraded and can
  never hold anyone again — the endpoint is a conflict, not a 204.
