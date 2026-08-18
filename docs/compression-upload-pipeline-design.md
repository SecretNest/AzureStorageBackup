# Splitting probe, compression and upload into three stages

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

### 1. Three stages, two queues

```
DiffWorkQueue ──> [prober × 1] ──> probedQueue ──> [compressor × 1] ──> stagedQueue ──> [uploaders × (UploadConcurrency + 1)]
```

| | prober (1) | compressor (1) | uploader (UploadConcurrency + 1) |
|---|---|---|---|
| takes from | `DiffWorkQueue` | `probedQueue` | `stagedQueue` |
| does | dedup probe of single-file items; settles the hits outright. Packs pass straight through. | 7z, move to pool, post-compression re-verify, re-queue/demote changed members | dedup resolve, upload every volume, `RecordPackAsync`, journal, `onItem` |
| bound by | one disk | one CPU (the compression lock is global anyway) | upload slots, network |
| blocks on | nothing but its own read | `WaitForRoomAsync` only | upload slots, network |

**Why the prober is one worker and not several.** The probe is disk-bound: on a candidate hit it reads
the whole file to derive its content identity (`BackupOrchestrator.cs:1599-1622`). Concurrency does not
make a disk faster, and on spinning media or a NAS share it makes it slower by turning one sequential
read into several competing seeks. The reason to give it its own stage is not parallelism — it is
**overlap**: while the prober reads the next item, the compressor is working on the previous one, so the
CPU stops waiting on a hash and the disk stops waiting on 7z.

Today's six workers each run probe → compress → upload end to end, so six probes can be in flight at
once — but all six then queue behind the same global compression lock, and the throughput ceiling is the
lock, not the probes. Trading six competing readers for one reader that overlaps the compressor is
therefore a gain twice over: the disk does sequential work, and neither stage idles waiting for the other.

**What stays on the compressor.** The pack path's post-compression re-verification
(`BackupOrchestrator.cs:2015-2055`) checks the archive that was just produced, so it belongs with the
thing that produced it. It normally costs one `stat` per member; only a member changed mid-compression
forces a rehash, which is rare. Moving it would need a fourth stage to buy very little.

The compressor's only blocking point is the staging quota. That is the entire point of the change:
**`StagedLimitBytes` becomes the sole backpressure on the compression side**, and the pool fills to
the configured ceiling instead of to whatever the worker pool happens to allow.

It is a ceiling on the compression stage, not a hard ceiling on the pool — see §3 and "What this does
not do": an uploader recompressing after a failed upload deliberately steps over it.

The visible consequence is that `N objects waiting for the archive slot` starts appearing in the
in-flight line during steady state. That line is not a warning — it is the evidence that the limit
is binding, and its absence today is the bug.

The uploader count stays at `UploadConcurrency + 1`. The extra consumer is what keeps the volume
gate's hand-off from stalling at item boundaries (`VolumeBlobIO.cs:143-167`), and that reasoning is
untouched by this split.

`stagedQueue` needs no depth limit of its own. Its depth is bounded in bytes by the staging pool,
which is the bound the user configured; adding a second, item-count bound would reintroduce a
constraint that fires before the one they set.

`probedQueue` **does** need one, and it is small: 128 entries, `FullMode.Wait`. An earlier draft of
this design said its entries "own nothing", which is true of staging quota and false of memory. What
they own is the `WorkItem` itself, and `DiffWorkQueue` exists precisely to keep those off the heap —
it caps the consumer backlog at 2,000 items / 64 MB and spills the remainder to a file
(`Program.cs:137-138`), a cap this document names as the precondition of the whole spill design.
Unbounded, this channel relays out of that bounded queue at one head-read per item while the
compressor consumes at 7z speed, two rates orders of magnitude apart, so it drains the spill file
back into memory in its entirety — on exactly the runs the spill was built for. At this repository's
measured scale (500,000 index entries, ~60-character paths, 64-character hashes),
`WorkItem.EstimatedBytes` (`DiffWorkQueue.cs:31-39`) puts that at roughly 170 MB of live objects, on
a NAS.

The bound costs nothing, because what this stage buys is **overlap, not buffering**: one or two items
of lookahead delivers all of it. It does add a second blocking hand-off — a full `probedQueue` stops
the prober, and only the compressor frees a slot — which §6 covers along with the first.

### 2. `StagedUnit` and the four release paths

A queue entry owns two things that must be handed back exactly once:

- the `StagedItem` — pool quota plus volume files on disk;
- for single files, the dedup reservation, with later same-content arrivals blocked on it.

Both are wrapped in one disposable owner (`StagedHandoff`) whose `Dispose` calls `staging.Release(staged)`
and `res.Fail(...)`, idempotently — the same shape, and for the same reason, as the `using`-scoped
`StagingArea.Hold` it replaces. That one has since been deleted: once the archive's lifetime stopped ending
inside the method that produced it, a scope guard had nothing left to scope.

Four paths must reach that `Dispose`:

1. **Normal completion** — the uploader releases per volume as it goes and disposes the owner at the
   end (already how it works today).
2. **Exception** — the uploader's `finally`.
3. **Stop** — the prober stops taking new work and both queues are drained. `probedQueue` entries hold
   no staging quota (nothing has been compressed yet), so draining them only has to settle the item
   ledger; `stagedQueue` entries hold an archive each and must be disposed. Entries already claimed by
   an uploader run to completion. This matches the current
   promise, "finish the current item, then stop" (`BackupOrchestrator.cs:727-730`), with "current
   item" now meaning the ones uploaders hold. Work compressed but not yet claimed is discarded — the
   local CPU spent on it is lost, no bytes reached the cloud, and nothing is left in the container.
4. **Pause-gate downgrade** — the suspend-and-exit path drains and disposes the queue before
   releasing the staging seat. Without this the run suspends while still holding up to the full
   limit, which is precisely the failure `PauseGate.cs:23-27` warns about, made larger by this change
   because the queue can now hold that much.

### 3. Upload failure recompresses in place, and that recompression does not queue for room

An uploader that needs a recompression takes the compression lock itself and reruns the attempt,
rather than handing the item back to the compressor.

The alternative — a return path into the compressor — would make the two stages mutually dependent
(compressor blocked on pool space, uploaders blocked behind a compressor that cannot drain) and buy
nothing. Recompression is an exception path, not steady state; the global compression lock keeps it
from ever running concurrently with the compressor, so the cost is that the compressor waits out one
recompression. Steady-state continuity, which is what this rework is for, is unaffected.

Mechanically this means `AttemptAsync`'s closure (`members`, `packId`, the pre-compression stat
snapshot) travels with the queue entry so the uploader can rerun it.

**The thing that argument missed.** Deciding *where* the recompression runs is only half the
question; the other half is what it is allowed to wait for. Staging that archive means going through
`StagingArea.StageAsync`, whose first act is `WaitForRoomAsync` — and everything in that pool is
released by an uploader. So an uploader waiting there is waiting for itself, and the state the
paragraph above rejects for the compressor is precisely the state it creates for the uploaders,
one door further along:

1. Any systemic upload failure — a network outage is the ordinary case — trips every in-flight
   upload at once. Each uploader disposes its archive (its bytes go back) and parks at the pause
   gate.
2. The compressor never sees a network error, so it carries on until the pool is at
   `StagedLimitBytes` and parks in `WaitForRoomAsync`. **This is the headline feature of this whole
   document working exactly as designed** — and it leaves the pool held entirely by queue entries no
   uploader owns.
3. The gate releases every waiter together, by construction (`PauseGate` keeps one signal for all of
   them). All `max(2, N+1)` uploaders re-enter their retry and ask for room. None of them has any.

Nothing can break it: `ReleaseFile`/`Release` are only reached from a volume upload, a `Handoff`
disposal or a discarded compression, each of which needs an uploader that is not blocked. §6's
`downstreamGone` does not fire, because it triggers on the uploaders being *gone* and these are alive.
The gate cannot downgrade, because nobody is at it. And `RequestStop` only fires the abort token for
`StopNow`, so **Suspend and "finish current files" hang along with the run** — the operator's HTTP
request never returns and the only ways out are "Stop now" or restarting the container.

This is a regression the split introduces rather than an old bug: before it, the pool could only ever
hold what ≤6 workers had staged, and a worker that was retrying had already released its own archive
while its siblings went on releasing volume by volume.

**So: staging on an uploader thread skips the wait for room.** `StagingArea` grows one new entry
point for it, `StageWithoutBackpressureAsync` — same files, same accounting, same global compression
lock, only `WaitForRoomAsync` skipped — and `StageAsync` keeps the exact semantics every existing
caller relies on. Three call sites use it, and they are exactly the three places an uploader
compresses: the single-file closure's `pending ?? StageBlobAsync`, `AttemptAsync`'s
`pending ?? CompressGroupAsync`, and the stranded-member tail's standalone `ProcessPackAsync` (which
compresses *and* uploads there — see "What this does not do").

Why this is the right shape rather than a wider one:

- **The overshoot is bounded and is a trade `HasRoom` already makes.** That test deliberately lets an
  item starting from zero begin even when its output is bound to exceed the allowance, because
  otherwise a file larger than the allowance could never be backed up at all. A retrying uploader is
  replacing an archive this pool has already admitted once, and holds at most one at a time, so the
  ceiling is exceeded by at most `N+1` archives before the ordinary gate binds again.
- **Widening the trigger instead ("fire `downstreamGone` when every live uploader is parked on the
  quota") does not work.** The uploaders wait on `working`, not on the feeding token, so cancelling
  the feeding stages would not release them; and it would end the compression side — and with it the
  run — over what is very often a blip the gate was about to ride out.
- **Handing the retry back to the compressor does not work either**, for the reason above: it is the
  same deadlock with the two stages swapped.

The compression stage must never reach the new entry point. Its waiting for room *is* the
backpressure this document exists to make binding.

### 4. Changed members stay on the compression side

Because `changedMembers` is settled before the upload (see Starting point), the whole tail that
handles them — recomputing the hash, writing the index override, re-queueing into a later group, or
demoting to a single file past the attempt limit (`BackupOrchestrator.cs:2126-2178`) — stays in the
compressor and does not cross the queue. Only `RecordPackAsync`, `LogFileAsync` and `onItem` follow
the upload.

This keeps the mutable per-item state (`queue`, `attempts`) single-threaded inside one
`ProcessPackAsync` invocation, which is what it already assumes.

### 5. Progress accounting balances, and one column reads wider than it used to

`BeginWork` moves to where the **prober** takes an item — the first stage, so that an item is counted as
in-hand for its whole journey across both queues; `EndWork` stays after the upload settles, or fires in
whichever earlier stage the item stops at (a probe that settles a dedup hit, a drained queue, a failure).
The identity `processed + preparing + queued + waitingOnArchive + uploading ≡ total`
(`StageProgress.cs:916-924`) continues to hold, because `uploading` is defined as
`inWork - _inStaging` — everything in hand that is not currently inside `StageAsync`. No new column is
needed.

**But `uploading` no longer means "past staging".** `_inStaging` is only incremented once `StageAsync`
is entered, so an item that has been probed and is sitting in `probedQueue` — up to 128 of them,
nothing about it compressed yet — is counted in `uploading` too. That is user-visible: the in-flight
line renders it as `N objects starting upload` (`stageLines.ts:93,139`), and the figure is inflated by
the depth of the first queue. Before the split there was nowhere for an item to be in-hand-but-not-in-
staging, so the two readings coincided; now they do not. The identity is unaffected — the term is
still exactly "in hand, not in staging" — and fixing the *label* belongs with the other in-flight-line
work noted at the end of this document.

### 6. A stage that is gone must release the one feeding it

Splitting one loop into three turned every hand-off into a place where an upstream stage waits on a
resource that **only the downstream stage returns**. The compressor's one blocking point is the
staging quota, and that comes back only from a live uploader — volume by volume as it sends, or in
one go when it disposes a dropped entry's archive. The prober's is a slot in `probedQueue`, and only
the compressor frees one. A resource nobody is left to return is a wait with no end: the run never
finishes, `SettleAsync(consumers)` waits on it forever, the busy lock is never released, and not one
word of the error that killed the downstream stage ever surfaces.

**"Only a live uploader returns it" is a stronger constraint than it first reads**, and the rest of
this section only covers half of it. The obvious half is a dead upload side, and the token below is
the answer to that. The other half is that **the uploaders wait on that same quota themselves**, on
the three paths where an uploader compresses (§3), and an uploader parked there cannot release
anything either. A live-but-blocked upload side is invisible to everything here: the token's trigger
is the last uploader *leaving*. §3 has the sequence, and the resolution — uploader-side staging skips
the wait for room — belongs there, next to the recompression it protects. What matters at this level
is the rule the two halves share:

> No stage may block on a resource whose only source is a stage that can, in turn, be blocked on it.

The token below enforces it for "the source is gone". §3 enforces it for "the source is the same
thread".

Neither `working` nor `control.Stop` covers it. `working` is linked only to `ct` and
`control.AbortToken`, and a pause-gate downgrade sets no stop kind (only `RequestStop` calls
`Gate.Downgrade()`). The worst case is therefore the auto-suspend feature working exactly as
designed: when patience runs out **every** uploader throws `BackupSuspendedException` out of
`WithPauseAsync` at once, while the compressor sails on, because 7z and the local disk never raise
the transient errors the gate reacts to. Before the split all six workers were uploaders too, so they
all left and released their own quota on the way out, and the run suspended cleanly.

So the two feeding stages run on a token of their own, cancelled when the stage in front of them is
gone. Two rules make it behave:

- **The trigger is the last uploader leaving, not the first one faulting.** One uploader dying on a
  permanently refused blob is an ordinary failure, and the rest of the run still gets compressed,
  uploaded and journalled by its siblings — which is how it behaved when one worker owned an item end
  to end, and a resumable run's journal is worth more than an early exit. The compressor cancels the
  token for the prober in its own `finally`, by the same argument one stage further up.
- **The feeding stages treat that cancellation as "leave quietly", not as an error** — the same way
  the diff treats `stopProducing`. They come before the uploaders in the `consumers` list, so a
  cancellation faulted out of them is the one `Task.WhenAll` would surface, masking the failure that
  caused it. A run auto-suspended this way must end as `BackupSuspendedException`, or it is recorded
  as canceled, no suspend mark is written, and the next startup never resumes it.

## What this does not do

**It does not keep 7z busy 100% of the time.** The post-compression re-verification stays on the
compressor (see §1), and while it stats members — or, rarely, rehashes a changed one — 7z idles. The
guarantee is narrower still than that, because two live paths cross the seam in each direction, and
both are exceptional rather than steady state:

- **A pack member demoted to a single file uploads on the compressor's thread.** When a member grows
  past `SingleFileThresholdBytes` while its group is being compressed, `ProcessPackAsync`
  (`BackupOrchestrator.cs:2687-2690`) hands it to `HandleBlobAsync` → `PlaceBlobAsync`, which
  compresses *and* uploads it inline — behind the volume gate and behind the network — with the
  compression stage stopped for the duration. It needs a member to change size mid-compression, so it
  is rare; keeping it where it is keeps `queue`/`attempts` single-threaded inside one
  `ProcessPackAsync` invocation (§4), which is the reason the demotion is not worth moving.
- **The stranded-member tail compresses on an uploader's thread.** When a group's retry excludes
  members the handed-over pass had kept, the uploader runs a standalone `ProcessPackAsync` for them
  (`:1104`), with no dispatch — so that call compresses as well as uploads. It needs a transient
  upload failure *and* a member rewritten inside the gate's wait window. This is one of the three
  places §3's quota bypass applies.

**It does not make `StagedLimitBytes` a hard ceiling on the pool.** It is a ceiling on the
*compression stage*. An uploader that recompresses after a failed upload bypasses the quota wait
deliberately (§3), because waiting there is what deadlocked the pipeline. Each uploader holds at most
one archive at a time, so the overshoot is bounded by `UploadConcurrency + 1` archives — and only
while uploads are failing, which is to say only when the run is already degraded. An operator sizing
the staging volume should leave that much headroom above the configured limit; the backend does not
validate free space, and `StagingArea` fails the backup outright if the disk fills.

So the honest form of the guarantee is: **in steady state the compression stage is never blocked by
the upload stage and never waits on the probe's disk reads**; on two exception paths, one stage does
the other's work for the length of one item; and on the retry path the pool may exceed the configured
limit by up to `UploadConcurrency + 1` archives.

**It does not make the probe faster in isolation.** One prober reads no quicker than one prober did
before; what changes is that its reads now overlap compression instead of competing with five siblings
for the same disk and then queuing behind the same lock. A run that is purely dedup hits — resuming a
large backup, say — is bound by that single disk either way.

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
- **Stop releases everything queued.** After a stop with both queues non-empty,
  `StagingArea.StagedBytes` returns to zero, the staged temp directory is empty, and no reservation
  waiter is left hanging.
- **Downgrade releases everything queued.** Same assertions on the pause-gate downgrade path.
- **Upload failure still recompresses the same pack id.** Existing pack-retry coverage must keep
  passing through the uploader-driven path.
- **The accounting identity holds across the queue.** Extend the existing ledger assertions to a run
  with entries sitting in `stagedQueue`.
- **An auto-suspend that kills every uploader does not strand the compressor** (§6). A pool small
  enough to fill against incompressible source, an uploader that refuses everything, and patience at
  zero: the run has to end as `BackupSuspendedException`, with the suspend mark on disk and the pool
  back at zero. Note the pool size — every other test in this suite runs a 200 MB pool over a few
  megabytes of zeroes, which is why the suite could not see this class of bug at all.
- **Every uploader retrying at once against a full pool still finishes** (§3). Four numbers, and all
  four are load-bearing: a pool small enough to fill, **incompressible** source, one injected failure
  per uploader, and a backoff long enough that all of them are out of circulation while the
  compressor fills the pool — a failure is instantaneous and compressing is not, so while any
  uploader is awake the queue drains as fast as it fills and the pool never grows. The suite had both
  halves of this hazard and never the two together: the existing retry test runs a 200 MB pool over
  zeroes, and the auto-suspend test above has the full pool but zero patience, so the gate downgrades
  on the first failure and the retry path is never entered.
- **A pack retry does not store a member the first pass already re-queued** (§4). Blip on the
  **first** group of a pool that was cut in two by a changed member, with that member settling
  afterwards, and assert the first group's pack holds only the members that pass kept.
