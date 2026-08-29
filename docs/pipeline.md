# The compression and upload pipeline

Three concurrent stages between the diff and the cloud, connected by two queues:

```
DiffWorkQueue ──> [prober × 1] ──> probedQueue ──> [compressor × 1] ──> stagedQueue ──> [uploaders × (UploadConcurrency + 1)]
```

| | prober (1) | compressor (1) | uploader (`UploadConcurrency + 1`) |
|---|---|---|---|
| takes from | `DiffWorkQueue` | `probedQueue` | `stagedQueue` |
| does | dedup probe of single-file items, settling hits outright; packs pass straight through | 7z, move to the pool, post-compression re-verify, re-queue or demote changed members | dedup resolve, upload every volume, record the pack, write the journal, settle the item |
| bound by | one disk | one CPU | upload slots, network |
| blocks on | nothing but its own read | `WaitForRoomAsync` only | upload slots, network |

## Why the stages are cut this way

**Compression is globally serial.** `StagingArea`'s compression lock is a `SemaphoreSlim(1, 1)` on a
DI singleton shared across every backup, so the compression side needs exactly one worker — a pool
would be a queue in front of a lock that admits one.

**The prober is one worker, not several.** The probe is disk-bound: on a candidate hit it reads the
whole file to derive a content identity. Concurrency does not make a disk faster, and on spinning
media or a NAS share it makes it slower by turning one sequential read into several competing seeks.
The reason it gets its own stage is not parallelism but **overlap** — while the prober reads the next
item, the compressor works on the previous one, so the CPU stops waiting on a hash and the disk stops
waiting on 7z.

**The uploader count stays at `UploadConcurrency + 1`.** The extra consumer is what keeps the volume
gate's hand-off from stalling at item boundaries.

> **Rationale — what this shape replaced, and why nothing smaller would do.** One worker used to own
> an item for its entire life: compression, staging, every volume of the upload, and the settle. With
> six workers, once six items were in their upload phase no worker was left to reach the compression
> stage and compression stopped dead. Measured symptom: the in-flight line showed `0 preparing` and
> `0 waiting for the archive slot` while 23 items sat queued. The user-visible consequence was that
> **`StagedLimitBytes` did nothing** — the worker pool saturated long before the staging pool did, so
> setting the limit to 2 GB, 10 GB or 40 GB produced identical behaviour and the field in Settings
> was decorative. Enlarging the pool does not fix it: any extra worker eventually enters its own
> item's upload phase and is consumed the same way. A reserved-worker scheme fails identically — the
> reserved worker, once it finishes compressing, still has to deliver its own item. As long as one
> worker owns an item end to end, no arrangement of pool sizes guarantees a compressor.

The visible consequence of the split is that `N objects waiting for the archive slot` appears during
steady state. That line is not a warning — it is the evidence that the configured limit is binding.

## The queues

Both depths are on screen — `N objects waiting for the compressor`, and `N objects waiting for
uploading (M volumes on the staging disk, X)`, which folds this queue in with everything else on the
staging disk that has nothing on the wire — and each carries its own term in the item ledger; see
[progress-display.md](progress-display.md). Which of the two is deep says which stage is the
bottleneck, so keeping them apart is worth a term each.

**`stagedQueue` has no depth limit of its own.** Its depth is bounded in bytes by the staging pool,
which is the bound the operator configured; a second item-count bound would fire before the one they
set. Three entry kinds escape even that, owning no archive at all — a dedup hit, a resume hit, a raw
in-place item — so on a store-only workload this queue can hold the whole dataset.

**`probedQueue` is bounded at 9 entries**, `FullMode.Wait`.

> **Rationale.** Its entries own no staging quota, but they own the `WorkItem` itself — and
> `DiffWorkQueue` exists precisely to keep those off the heap, capping the backlog at 2,000 items /
> 64 MB and spilling the rest to a file. Unbounded, this channel relays out of that bounded queue at
> one head-read per item while the compressor consumes at 7z speed — two rates orders of magnitude
> apart — so it drains the spill file back into memory in its entirety, on exactly the runs the spill
> was built for. At this repository's measured scale (500,000 index entries, ~60-character paths)
> that is roughly 170 MB of live objects, on a NAS. The bound costs nothing, because what this stage
> buys is overlap, not buffering: one or two items of lookahead delivers all of it.
>
> **Nine, specifically, is a display decision.** This depth is shown verbatim as
> `N objects waiting for the compressor`, and a deep one reads as "compression is the bottleneck" when
> most of the time it is the opposite: the compressor is held by staging-pool backpressure because the
> uploaders are behind, and the backlog piles up here as a *symptom* of the wire being slow. A number
> that cannot exceed ten cannot tell that lie loudly. The cost is the flip side of the same coin —
> nine items of lookahead does not cover the prober stalling on one large single file's full-content
> dedup read, so the compressor can briefly run dry where a deeper queue would have carried it.

## The staging area

Two directories:

- **compress-temp** — 7z's output target.
- **staged-temp** — completed volume sets, moved here whole and read from here by the uploader.

Compression writes into compress-temp and **moves** the finished volume set into staged-temp, so a
volume can never be modified mid-compression. A completed upload deletes from staged-temp
immediately. Both directories are emptied at startup — the process has just started, so by definition
nothing there belongs to a live run, and leftovers are never reused.

`StagedLimitBytes` (2 GB by default, changeable at runtime from Settings) is the backpressure. `HasRoom` gates on current pool occupancy
against the limit, with the per-run quota split across live leases, and it is checked **before** the
compression lock is taken so a run waiting for space does not pin the lock.

That ordering is also why the wait has its own progress column, `N objects waiting for staging room`,
rather than sharing the archive lock's. The two are consecutive inside `StageCoreAsync` and look
identical from outside — nothing of ours is packing, somebody is waiting — but the lock ends when a
producer lets go while this one ends only when an **upload** frees space, and the archive-lock entry's
documented reading ("another run holds it, go and stop that one") is wrong for every item parked here.
It is registered only on a real wait; with room to spare `WaitForRoomAsync` returns before touching the
tracker, so the common path costs no progress events. See `docs/progress-display.md`.

One item starting from zero is allowed to overshoot the limit.

> **Rationale.** Otherwise a file whose output is larger than the whole allowance could never be
> backed up at all.

### A leaked staging debt is permanent

The quota lives on an in-memory singleton, so leaking it once keeps that space booked until the
process restarts — and since it gates output for every run, enough leaks stall compression
process-wide. Everything that takes quota therefore hands it back through exactly one owner
(`StagedHandoff`), whose disposal releases the archive and fails the dedup reservation, idempotently.

Four paths must reach that disposal:

1. **Normal completion** — the uploader releases per volume and disposes the owner at the end.
2. **Exception** — the uploader's `finally`.
3. **Stop** — the prober stops taking work and both queues are drained. `probedQueue` entries hold no
   quota and only need the item ledger settled; `stagedQueue` entries hold an archive each and must
   be disposed. Entries already claimed by an uploader run to completion.
4. **Pause-gate downgrade** — the suspend-and-exit path drains and disposes the queue *before*
   releasing the staging seat.

## Upload failure recompresses in place

An uploader that needs a recompression takes the compression lock itself and reruns the attempt,
rather than handing the item back to the compressor. The pack id is taken outside the retry unit and
never changes, so a recompression overwrites the same volume family.

> **Rationale — why not return it to the compressor.** That would make the two stages mutually
> dependent: the compressor blocked on pool space, the uploaders blocked behind a compressor that
> cannot drain. Recompression is an exception path, not steady state, and the global compression lock
> keeps it from ever running concurrently with the compressor — so the cost is that the compressor
> waits out one recompression.

**And that recompression does not queue for room.** `StagingArea.StageWithoutBackpressureAsync` is
the entry point uploaders use: same files, same accounting, same global lock, only the wait for room
skipped.

> **Rationale — the deadlock this breaks.** Everything in the pool is released by an uploader, so an
> uploader waiting for room is waiting for itself. The sequence: a network outage trips every
> in-flight upload at once, each uploader disposes its archive and parks at the pause gate; the
> compressor never sees a network error, so it carries on until the pool is exactly at
> `StagedLimitBytes` and parks — *this is the headline feature working as designed* — leaving the
> pool held entirely by queue entries no uploader owns; the gate then releases every waiter together,
> all of them ask for room, and none has any. Nothing can break it, because every release path needs
> an uploader that is not blocked. Suspend and "finish current files" hang along with the run, and
> the only ways out are Stop now or restarting the container.
>
> The overshoot this permits is bounded: each uploader holds at most one archive, so the ceiling is
> exceeded by at most `UploadConcurrency + 1` archives, and only while uploads are failing. An
> operator sizing the staging volume should leave that much headroom; the backend does not validate
> free space and fails the backup outright if the disk fills.

**The compression stage must never reach that entry point.** Its waiting for room *is* the
backpressure the whole split exists to make binding.

## A stage that is gone must release the one feeding it

Splitting one loop into three turned every hand-off into a place where an upstream stage waits on a
resource only the downstream stage returns. The rule:

> No stage may block on a resource whose only source is a stage that can, in turn, be blocked on it.

The compressor's one blocking point is the staging quota, returned only by a live uploader. The
prober's is a slot in `probedQueue`, freed only by the compressor. A resource nobody is left to
return is a wait with no end: the run never finishes, the busy lock is never released, and not one
word of the error that killed the downstream stage surfaces.

So the two feeding stages run on a token of their own, cancelled when the stage in front of them is
gone. Two rules make it behave:

- **The trigger is the last uploader leaving, not the first one faulting.** One uploader dying on a
  permanently refused blob is an ordinary failure, and the rest of the run still gets compressed,
  uploaded and journalled by its siblings — which is how it behaved when one worker owned an item end
  to end, and a resumable run's journal is worth more than an early exit.
- **The feeding stages treat that cancellation as "leave quietly", not as an error.** They come
  before the uploaders in the consumer list, so a cancellation faulted out of them is the one
  `Task.WhenAll` would surface, masking the failure that caused it. A run auto-suspended this way
  must end as a suspension, or it is recorded as canceled, no suspend mark is written, and the next
  startup never resumes it.

The other half of that rule — an uploader blocked on the quota it is itself supposed to release — is
what the bypass above answers.

## Changed members stay on the compression side

Whether a pack member changed during compression is settled before the upload, so the whole tail that
handles it — recomputing the hash, writing the index override, re-queueing into a later group, or
demoting to a single file past the attempt limit — stays in the compressor and never crosses the
queue. Only recording the pack, the verbose log and the item callback follow the upload.

> **Rationale.** This keeps the mutable per-item state (the group queue, the attempt counter)
> single-threaded inside one invocation, which is what it already assumes.

## The raw route: uploading from where the file already is

A single-file blob that is **store-only, unencrypted and fits in one volume** has stored bytes
identical to its source bytes, so nothing is produced at all:

```
stat → read once (hash only, no copy) → resolve the address → upload from the source path
     → stat again → unchanged? done.  changed? undo and fall back.
```

Two reads, no write, and nothing charged to the staging quota. The compression lock is deliberately
**not** taken — it serialises production into the temp area and this route produces nothing; holding
it would make every raw file block every other run's compression for the length of a full-file read.

> **Rationale — what the copy used to be for.** The copy was not there to save a read; it was there
> to **fix the content**. The hash was computed while copying, so the bytes hashed and the bytes
> stored were the same set by construction — and that matters because a data blob's address *is* its
> content. If the bytes reaching the cloud are not the bytes that produced the hash, the container
> holds an object whose name contradicts its content, and nothing downstream would notice: dedup,
> restore and check all read the index, and the index agrees with the name.

### The guard

What replaces the copy is a bracket: length and mtime are `stat`ed before the hashing read begins and
again once the upload is over, however it ended. Any movement in either means the upload did not
necessarily send what was hashed.

**The window is not short, and the guard does not assume it is.** It compares two `stat`s taken
around the whole stretch rather than racing it: a file that has not moved between them did not move,
however long it took. On a store-only run that stretch is genuinely long — a raw item charges nothing
to the staging pool, so nothing bounds how far the compressor may run ahead of the uploaders, and for
the tail of a large run the hash-to-upload distance is hours. Length only changes how *often* the
guard trips.

When it trips, the blob that was just written is **deleted** — it is named for a hash its content may
no longer match — and the item is retried through the copying route, which is immune because it
uploads a snapshot. One retry, then the ordinary transient-error machinery takes over.

> **Rationale — the asymmetry is deliberate.** The fast path is optimistic and verified; the fallback
> is pessimistic and unconditional. A file being rewritten during its own backup is rare, so paying a
> copy for every raw file to pre-empt it is not worth it — and never noticing would be unacceptable.

**The test runs on both endings of the upload, not only the returning one.** "The upload threw" does
not mean "nothing was committed": Azure acknowledges a commit over a connection that can die on the
way back, which is the routine NAS-to-Azure blip. But the take-back happens **only when the source
moved** — an upload that failed with the file exactly as it was hashed leaves nothing that cannot be
vouched for, and deleting it regardless would throw away a correct object that the retry's if-missing
upload would otherwise skip.

> **Rationale — what an orphan of this kind turns into.** The retry re-hashes the moved source and
> lands on a *different* address, so the first object is orphaned rather than overwritten, and nothing
> sweeps it afterwards. A later run that legitimately produces that same hash then claims the address,
> finds the single-volume path clearing nothing, and is told "already there" by the if-missing upload
> without a byte being read. The index records `data/{H}` as holding `H`, and it does not.

**A same-content peer is failed transiently, not fatally.** Another file in the same run with
byte-identical content uploads nothing — it parks on this item's dedup reservation, and when the
guard trips that reservation is failed, because it must never be handed an address that has just been
deleted. The exception it receives is classified transient, which is both what the peer needs (retry,
and upload the content itself, since nobody else will) and what the situation is.

**A take-back that cannot be done is said out loud.** The delete is retried under the same bounded
policy the cleanup path uses, and if it still fails the address goes to the operation log and out
through the `UnrecoverableError` channel, naming the blob and the file. Nothing else in the system
will ever find that object.

### What stays as it was

Encrypted, compressed and volume-split blobs keep copying — their stored bytes are not the source
bytes. Packs are unaffected. The journal, the index and the dedup tables see the same values either
way: the record describes content, and the content is identical.

## The volume upload gate

Volumes are uploaded concurrently and in any order, arbitrated by `(ticket, volume)`: a family takes
one ticket when it starts uploading, lower tickets win, and within a family volumes go in order.

> **Rationale — why by item age rather than first-come-first-served.** With `UploadConcurrency + 1`
> consumers and globally serialised compression, a bare semaphore produced a steady state of "one
> item compressing, N uploading", each getting about one stream: **N items half-finished at once**.
> That is not merely untidy — an item is journalled and cleared only once its whole volume set is
> confirmed, so "how many items are simultaneously half-finished" is exactly how much work an
> interruption throws away. Arbitrating by item age brings it down to typically one or two, and
> throughput is unaffected because the slots stay saturated: whatever the oldest item cannot use goes
> to the next one immediately.

The sliding window is `MaxParallelPerItem × 2`: half of it is the family's share of the wire, and half
is its **relief line** on the gate — one queued volume per slot it holds. Without a relief line, every
volume changeover leaks a slot to a newer item, because the slot is handed over synchronously inside
`Release` while this family's next volume cannot queue until its `WhenAny` continuation runs.

> **Rationale — why one spare volume was not enough.** The relief line used to be a single volume, a
> "baton", on the reasoning that the changeover crack is microseconds wide against a volume upload
> measured in seconds. That is true of the crack and irrelevant to the leak, because releases do not
> arrive one at a time. Volumes are equal-sized and share one link, so a family's in-flight set starts
> together and **finishes together**: up to `MaxParallelPerItem` releases inside one instant, with every
> changeover continuation still queued behind them on the thread pool. The baton caught the first and
> the rest of the wave went to the newer item — its volume 1 first, since the lowest volume within a
> ticket wins. On screen: a file that had only just finished preparing put its `(1/xxx)` in front of
> everything the previous file had left to send, on every wave. A wave can never be wider than the
> slots the family holds, so a relief line that deep closes it outright.

The window governs which volumes are **queued**, not which exist: compression has already written the
whole volume set to the pool before any of it is uploaded, and each volume is released from the pool as
it lands. So the depth costs a few more waiters and nothing on disk.

Not every volume reaches the gate at all: when the family's cloud listing proves an existing volume
identical — identity label and length match, plus a downloaded-bytes proof on the repair path — the
volume is **skipped** before it ever asks for a slot, releasing its staging file on the spot. A
resumed run's salvaged volumes therefore cost the gate nothing and tick through instantly; see
[volume-identity.md](volume-identity.md).

## Where this stops short

**7z is not busy 100% of the time.** The post-compression re-verification stays on the compressor, so
while it stats members — or rarely rehashes a changed one — 7z idles. Two live paths also cross the
seam, both exceptional rather than steady state:

- **A pack member demoted to a single file uploads on the compressor's thread**, behind the volume
  gate and behind the network, with the compression stage stopped for the duration. It needs a member
  to change size mid-compression.
- **The stranded-member tail compresses on an uploader's thread**, when a group's retry excludes
  members the handed-over pass had kept. It needs a transient upload failure *and* a member rewritten
  inside the gate's wait window.

So the honest form of the guarantee: **in steady state the compression stage is never blocked by the
upload stage and never waits on the probe's disk reads**; on two exception paths one stage does the
other's work for the length of one item; and on the retry path the pool may exceed the configured
limit by up to `UploadConcurrency + 1` archives.

**The probe is no faster in isolation.** One prober reads no quicker than one prober did before; what
changed is that its reads overlap compression instead of competing with five siblings for the same
disk. A run that is purely dedup hits — resuming a large backup — is bound by that single disk either
way.

## See also

- [backup-engine.md](backup-engine.md) — the stages either side of this one
- [content-identity.md](content-identity.md) — what the prober computes and looks up
- [run-lifecycle.md](run-lifecycle.md) — the pause gate every stage passes through
- [progress-display.md](progress-display.md) — how these stages are rendered
