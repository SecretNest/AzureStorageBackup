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
cancellation path, partial output is cleaned by `MoveToStaged`'s catch, and the queues are released by
`DrainQueues` as they already are. The uploaders finish the volume in hand and write the journal,
which is the only in-flight work worth finishing.

`StopNow` is unchanged: it fires `_abort` → `working`, which cancels everything including uploads.

### 2. `PauseGate` learns a second reason to be closed

The gate stays one gate — a worker should not have to ask *why* it is parked — but it grows a second
way to be closed and a source on what it reports:

```csharp
public enum PauseSource { TransientError, User }

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

### 4. What the operator sees

`RunStatus` is **unchanged** — the run is genuinely still Running, it is merely holding. Paused is
expressed by `PauseInfo.Source == User`, and the frontend renders that as a paused run:

```
Paused 23 minutes ago · holding 4.2 GB of staging
[Resume] [Suspend] [Stop]
```

The staging figure is not decoration. A paused run keeps its compressed output on disk — that is what
makes Resume cheap — and that quota is process-wide, so a run paused overnight holds it overnight.
There is deliberately **no timeout**: an automatic downgrade would turn a pause into a suspend exactly
when the operator is not watching, which is the opposite of what they asked for. The cost is stated on
screen instead, and the operator decides.

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

**It does not make Pause instant.** See §3.

**It does not add Pause to restore, check or repair.** Those runs are short; the machinery is not
worth duplicating until one of them is measured to need it.

**It does not bound how long a pause may hold the staging quota.** By decision, see §4.

## Tests

- **Suspend stops the feeding stages at once.** With a slow probe in progress, a suspend returns
  without waiting for that read to finish, and the run still journals the volume that was on the wire.
- **Pause holds all four loops.** After a pause, `processed` stops advancing and the staged pool stops
  growing; both resume after `ResumeByUser`.
- **Pause preserves work.** Items compressed before the pause are uploaded after the resume without
  being compressed a second time.
- **The two reasons compose.** A transient failure during a user pause leaves the gate closed after
  `ResumeByUser`, and the reported source is the transient error.
- **Paused → Suspend.** From a paused run, a suspend completes and writes a resumable journal.
- **A user pause never downgrades.** Held far longer than the gate's patience, the run stays Running
  rather than auto-suspending.
