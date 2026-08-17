# Splitting compression and upload into two stages

## The problem

A single backup, upload concurrency 5, staging limit set to 10 GB. The in-flight line reads:

```
In flight: +4.1 GB on the cloud · 5 volumes uploading · 4 volumes waiting for an upload slot
           · 4.5 GB ready to upload · 23 objects queued
```

There is no `object preparing` and no `objects waiting for the archive slot` in that line. Both are
absent for the same reason: **nothing was inside `StagingArea.StageAsync` at that instant**.
`preparing` counts items holding the compression lock (`StageProgress.cs:885`) and
`waitingOnArchive` counts items that entered staging but have not got the lock
(`StageProgress.cs:895`). Zero on both means the 7z stage was not merely blocked — it was empty,
while 23 items sat in the queue waiting for it.

The cause is the shape of the consumer loop. `ConsumeAsync` (`BackupOrchestrator.cs:721-744`) takes
an item and holds its worker for the item's entire life — compression, staging, every volume of the
upload, and the settle — inside one `await RunItemAsync`. There are `UploadConcurrency + 1` workers
(`BackupOrchestrator.cs:753`), six by default. Once six items are in their upload phase, no worker
is left to reach `StageAsync`, and compression stops until one of them finishes. The staged pool can
only drain during that stretch, which is what the second snapshot caught: `+4.1 GB on the cloud`
fell to `+100.0 MB` as a large item settled, a worker came free, `1 object preparing` reappeared,
and `ready to upload` had meanwhile ticked down from 4.5 GB to 4.0 GB.

The consequence the user actually sees is that **`StagedLimitBytes` does nothing**. The worker pool
saturates long before the pool does, so the staging limit is never the binding constraint. Setting
it to 10 GB, 2 GB, or 40 GB produces identical behaviour; the knob in Settings
(`SettingsPage.tsx:185`) is decorative.

Enlarging the worker pool does not fix this, and the reason is worth stating because it is the
argument for the whole rework: any extra worker eventually enters its own item's upload phase and is
consumed the same way. A reserved-worker scheme fails for the same reason — the reserved worker,
once it finishes compressing, has to deliver its own item to the cloud. As long as one worker owns
an item from compression through upload, no arrangement of pool sizes guarantees a compressor.

## Starting point

Facts the design has to hold to, all of them load-bearing:

- **Compression is globally serial.** `StagingArea._compressLock` is a `SemaphoreSlim(1, 1)` on a
  singleton shared across every backup (`StagingArea.cs:20`, `Program.cs:77-88`). So the compression
  side needs exactly one worker; a pool would be a queue in front of a lock that admits one.
- **Backpressure already exists and is already correct.** `HasRoom` (`StagingArea.cs:117-119`) gates
  on current pool occupancy against the limit, with the per-run quota split across live leases. It
  is checked before the compression lock is taken, deliberately (`StagingArea.cs:204-218`), so a run
  waiting for space does not pin the lock. Nothing here needs to change — it just needs to become
  the binding constraint.
- **The retry unit for a pack is "compress this group + upload it".** `AttemptAsync`
  (`BackupOrchestrator.cs:1978-2084`) is re-entrant by design: the pack id is taken outside it and
  never changes, so a recompression overwrites the same volume family
  (`BackupOrchestrator.cs:1936-1940`). An upload failure therefore has to be able to trigger a
  recompression.
- **The journal write, `RecordPackAsync` and `onItem` sit outside the retry unit**
  (`BackupOrchestrator.cs:2092-2124`), after the cloud has confirmed. They must stay exactly there.
- **`changedMembers` is decided before the upload.** The post-compression re-verification
  (`BackupOrchestrator.cs:2015-2055`) determines it; the upload result contributes nothing to it.
  The code returns it alongside the upload result only as a convenience.
- **A single-file item carries a dedup reservation across the upload.** `ResolveAsync`
  (`BackupOrchestrator.cs:1559`) hands back a reservation that later arrivals with identical content
  are blocked on; `res.Complete` / `res.Fail` (`1572`, `1577`) are driven by the upload outcome.
- **A leaked staging debt is permanent.** The quota lives on a singleton in memory; leaking it once
  keeps that space booked until the process restarts, and since it gates output for every run, enough
  leaks stall compression process-wide (`StagingArea.cs:270-286`). `PauseGate` documents the same
  hazard from the other end: a downgraded run must release its staging seat or it "blocks every
  parallel backup completely" (`PauseGate.cs:23-27`).
- **Grouping is finished before enqueue.** Cross-directory packs are sealed on the diff side
  (`BackupOrchestrator.cs:898-941`), so the consumer side has no ordering constraint between items
  and a single-threaded compressor may take work strictly in queue order.

## Design

### 1. Two stages, one queue

```
DiffWorkQueue ──> [compressor × 1] ──> stagedQueue ──> [uploaders × (UploadConcurrency + 1)]
```

| | compressor (1) | uploader (UploadConcurrency + 1) |
|---|---|---|
| takes from | `DiffWorkQueue` | `stagedQueue` |
| does | dedup probe, 7z, move to pool, post-compression re-verify, re-queue/demote changed members | dedup resolve, upload every volume, `RecordPackAsync`, journal, `onItem` |
| blocks on | `WaitForRoomAsync` only | upload slots, network |

The compressor's only blocking point is the staging quota. That is the entire point of the change:
**`StagedLimitBytes` becomes the sole backpressure on the compression side**, and the pool fills to
the configured ceiling instead of to whatever the worker pool happens to allow.

The visible consequence is that `N objects waiting for the archive slot` starts appearing in the
in-flight line during steady state. That line is not a warning — it is the evidence that the limit
is binding, and its absence today is the bug.

The uploader count stays at `UploadConcurrency + 1`. The extra consumer is what keeps the volume
gate's hand-off from stalling at item boundaries (`VolumeBlobIO.cs:143-167`), and that reasoning is
untouched by this split.

`stagedQueue` needs no depth limit of its own. Its depth is bounded in bytes by the staging pool,
which is the bound the user configured; adding a second, item-count bound would reintroduce a
constraint that fires before the one they set.

### 2. `StagedUnit` and the four release paths

A queue entry owns two things that must be handed back exactly once:

- the `StagedItem` — pool quota plus volume files on disk;
- for single files, the dedup reservation, with later same-content arrivals blocked on it.

Both are wrapped in one disposable owner whose `Dispose` calls `staging.Release(staged)` and
`res.Fail(...)`, idempotently — the same shape as the existing `StagingArea.Hold`
(`StagingArea.cs:287-296`), and for the same reason it exists.

Four paths must reach that `Dispose`:

1. **Normal completion** — the uploader releases per volume as it goes and disposes the owner at the
   end (already how it works today).
2. **Exception** — the uploader's `finally`.
3. **Stop** — the compressor stops taking new work and disposes every entry still sitting in
   `stagedQueue`; entries already claimed by an uploader run to completion. This matches the current
   promise, "finish the current item, then stop" (`BackupOrchestrator.cs:727-730`), with "current
   item" now meaning the ones uploaders hold. Work compressed but not yet claimed is discarded — the
   local CPU spent on it is lost, no bytes reached the cloud, and nothing is left in the container.
4. **Pause-gate downgrade** — the suspend-and-exit path drains and disposes the queue before
   releasing the staging seat. Without this the run suspends while still holding up to the full
   limit, which is precisely the failure `PauseGate.cs:23-27` warns about, made larger by this change
   because the queue can now hold that much.

### 3. Upload failure recompresses in place

An uploader that needs a recompression takes the compression lock itself and reruns the attempt,
rather than handing the item back to the compressor.

The alternative — a return path into the compressor — would make the two stages mutually dependent
(compressor blocked on pool space, uploaders blocked behind a compressor that cannot drain) and buy
nothing. Recompression is an exception path, not steady state; the global compression lock keeps it
from ever running concurrently with the compressor, so the cost is that the compressor waits out one
recompression. Steady-state continuity, which is what this rework is for, is unaffected.

Mechanically this means `AttemptAsync`'s closure (`members`, `packId`, the pre-compression stat
snapshot) travels with the queue entry so the uploader can rerun it.

### 4. Changed members stay on the compression side

Because `changedMembers` is settled before the upload (see Starting point), the whole tail that
handles them — recomputing the hash, writing the index override, re-queueing into a later group, or
demoting to a single file past the attempt limit (`BackupOrchestrator.cs:2126-2178`) — stays in the
compressor and does not cross the queue. Only `RecordPackAsync`, `LogFileAsync` and `onItem` follow
the upload.

This keeps the mutable per-item state (`queue`, `attempts`) single-threaded inside one
`ProcessPackAsync` invocation, which is what it already assumes.

### 5. Progress accounting is unchanged

`BeginWork` moves to where the compressor takes an item; `EndWork` stays after the upload settles.
The identity `processed + preparing + queued + waitingOnArchive + uploading ≡ total`
(`StageProgress.cs:916-924`) continues to hold, because `uploading` is defined as
`inWork - _inStaging` — items past staging but not settled — which is exactly what a queue entry
plus an in-flight upload is. No column changes meaning, and no new column is needed.

## What this does not do

**It does not keep 7z busy 100% of the time.** The compressor also runs the dedup probe, which reads
a whole candidate file end to end (`BackupOrchestrator.cs:1599-1622`), and the post-compression
re-verification, which stats every member and may rehash a changed one
(`BackupOrchestrator.cs:2015-2055`). Both are local I/O on the compression worker, and 7z idles
through them. The guarantee this design makes is narrower and should not be overstated: **the
compression stage is never blocked by the upload stage**. Pushing the probe off the compressor too
would need a third stage and is not worth it until measurement says otherwise.

**It does not change how much gets uploaded in parallel.** `VolumeUploadGate`, its per-volume
accounting and its item-age arbitration are untouched.

**It does not address the in-flight line's blind spot.** `N objects starting upload` is suppressed
whenever any volume is in flight (`stageLines.ts:139`), which is why the state described at the top
of this document could not be read off the screen directly. Worth fixing, separately.

## Tests

- **Compression proceeds while every uploader is busy.** With a stalled upload backend and upload
  concurrency N, after N+1 items are in their upload phase the pool keeps growing and `preparing`
  stays non-zero. This is the regression the whole change exists for, and it fails on today's code.
- **The staging limit binds.** With a slow upload backend, pool occupancy converges on
  `StagedLimitBytes` and `waitingOnArchive` becomes non-zero — versus today, where it stays at zero
  and occupancy plateaus wherever the worker pool leaves it.
- **Stop releases everything queued.** After a stop with a non-empty `stagedQueue`,
  `StagingArea.StagedBytes` returns to zero, the staged temp directory is empty, and no reservation
  waiter is left hanging.
- **Downgrade releases everything queued.** Same assertions on the pause-gate downgrade path.
- **Upload failure still recompresses the same pack id.** Existing pack-retry coverage must keep
  passing through the uploader-driven path.
- **The accounting identity holds across the queue.** Extend the existing ledger assertions to a run
  with entries sitting in `stagedQueue`.
