# Run progress and reporting

A 3 TB backup runs for over a day. Facing the screen, the user has exactly one question: **is it
working, or is it hung?**

That question cannot be answered by "N objects processed". During Uploading an item first goes
through 7-Zip — a 100 MB pack can take tens of seconds — and during that window not one byte is on
the wire, which from the outside is indistinguishable from a hang. So the whole design lands on one
requirement:

> **Every silent stretch lasting minutes must have a segment on screen that says what it is doing.**

The converse holds too: every number on screen must survive the question "where on the pipeline does
this belong?" A number that cannot answer it will actively manufacture misreadings.

## Where the state comes from

**Server-authoritative, memory only.** Progress survives a refresh, not a restart.

> **Rationale.** Persisting it would leave a percentage frozen halfway through a run that is already
> dead, and would then require a whole "mark orphans at startup" pass to clean up an illusion of its
> own making. The runner and the busy tracker are both in-memory singletons cleared with the process,
> so no inconsistency is possible. Suspend/resume later let the **run** continue across a restart,
> but the figures still are not persisted: what resumes is the work, not the bar.

**Scheduled runs take the same path as the UI button.** Ownership of the busy lock is expressed **by
method choice, not by a boolean**:

```csharp
/// UI: take the busy lock and fire-and-forget. Returns the existing state if already running.
public BackupRunState Start(int configId)

/// Scheduler: the caller **already holds** the busy lock for this (account, container).
public Task<BackupRunState> RunTrackedAsync(int configId, CancellationToken ct)
```

> **Rationale.** Get a boolean wrong once and you either refuse to run or leave the lock unheld —
> neither shows up at compile time. A method name cannot be got wrong the same way. This came from a
> real defect: the scheduler bypassed the runner entirely and passed `null` as the progress callback,
> so scheduled backups registered no run state at all — and scheduled backups are the normal case.

The scheduler additionally has to check for failure and throw: the runner swallows exceptions rather
than propagating them, and without that check a failure is recorded as a success.

**Polling, not SSE or WebSocket.** The list refreshes every 5 seconds from local SQLite; while
anything is active the client polls the one matching status endpoint once per second, and sends
nothing at all when everything is idle. A long-lived connection brings reconnection and reverse-proxy
compatibility problems, and the cost here is negligible.

> The polling loop must not live inside a closure. The original defect was a `while` loop inside the
> click handler, so it only ran if the button was pressed in that browser session — refreshing the
> page lost it, even though the server knew all along that the run was alive.

## Stages and their units

Each stage counts a **different kind of thing**. Sharing one word makes it look like packing did not
work — a backup that packs 46,624 files into 4,995 archives is reporting both numbers correctly.

| Stage | Unit | One unit is |
|---|---|---|
| Scanning | entries | a filesystem entry found by the walk; the total is unknown until the scan ends |
| Diffing | files | a source file, whether or not it later gets packed |
| Uploading / Restoring | objects | a stored object: one pack archive, or one single-file blob |
| Cloud (check) | objects | one `HEAD` per pack, not per file |
| Verifying (check) | objects | a pack downloaded, extracted and re-hashed |
| Local (check) | files | an index entry |
| Listing (check) | blobs | a blob in the container, orphan or not |
| Writing index | volumes | one transfer of one 64 MB index volume: up, or back down for verification. A single-blob index is three transfers (temp up, verify down, commit up) |

> **The listing stage is named for the work, not for the quarry.** It counts every blob it lists on
> the way to subtracting the reference set, so the number it reports is the container's size. Called
> `Orphans`, a six-figure container read as six figures of garbage — right number, right unit, wrong
> heading. The orphan count itself is not a progress figure at all; it is in the check report.

**Completion is computed from source bytes, not from the item count.** One item can be a 6.8 GB
single file or a pack of several hundred 5 KB files, and counting them equally is meaningless —
measured, the count read 75% while the bytes were at 31%. Only when the byte total is unknown
(Scanning, Diffing) does it fall back to the count.

## The item ledger

```
processed + preparing + queued + waitingOnArchive
    + awaitingCompression + awaitingUpload + uploading ≡ total
```

**This identity is the skeleton of the design, not decoration.** If it holds, every item at every
instant belongs to exactly one segment on screen. If it does not, the item belonging to none of them
is precisely the one that is stuck — and it can only be found by lining up several screenshots and
doing subtraction. That trap has been stepped in twice.

| Term | Unit | Meaning |
|---|---|---|
| `processed` | items | finished and settled |
| `preparing` | items | holding the **global compression lock**. One lock, so always 0 or 1 |
| `waitingOnArchive` | items | picked up by a worker, queuing behind that same lock |
| `queued` | items | still in the queue, not picked up |
| `awaitingCompression` | items | probed, parked in `probedQueue` waiting for the single compressor |
| `awaitingUpload` | items | past compression, parked in `stagedQueue` with no uploader on them yet |
| `uploading` | items | everything past staging: in flight, waiting on a resource, or checking |

`uploading` is `inWork - inStaging - awaitingCompression - awaitingUpload`, and deliberately **not** a
`BeginUpload`/`EndUpload` pair.

> **Rationale.** The pair only starts counting at the upload call, while the stretch between
> "compressed" and there still contains checking and reservation coordination — an item can sit there
> for minutes. Using it as the measure makes the ledger fail exactly when it matters most, and a
> ledger that fails there is the whole reason the term exists. The subtraction folds that gap in, so
> the identity does not depend on any call site.

### The two hand-off queues

The run is `prober → compressor → uploaders`, and each arrow is a channel. An item parked in one is
**claimed but idle** — no thread is doing anything to it, it is waiting for the next stage to have
room — so each channel gets its own term rather than being folded into a neighbour.

| Queue | Term | Depth | What a large reading means |
|---|---|---|---|
| `probedQueue` | `awaitingCompression` | bounded, 9 | little on its own — see below |
| `stagedQueue` | `awaitingUpload` | unbounded | the wire is the bottleneck |

**The cap of nine is itself a display decision**, and it is why this term's large reading says so little.
Sitting at the cap reads as "compression is the bottleneck" when most of the time it is the opposite:
the compressor is held by staging-pool backpressure because the uploaders are behind, and the backlog
piles up here as a *symptom* of the wire being slow. A number that cannot exceed ten cannot tell that
lie loudly. The reasoning behind the depth is in [pipeline.md](pipeline.md).

`stagedQueue` has no depth limit because whatever owns an archive is already bounded in bytes by the
staging pool, which is the limit the operator set. Three entry kinds own no archive — a dedup hit, a
resume hit, a raw in-place item — and those are bounded by nothing, so on a store-only workload the
compressor can queue the whole dataset while the uploaders trickle. A five-figure reading here is the
pipeline working as designed.

> **Rationale.** Neither term is its neighbour. `queued` means "nobody has picked this up", which is
> false for a probed item — the prober read it and settled its content identity. `waitingOnArchive`
> means "inside the staging area, queued on the global compression lock", which is also not it: an
> item awaiting compression has not reached the staging area, because the compressor is one worker
> and takes one item at a time.
>
> Folded into `uploading` (as they were, when `uploading` was plain `inWork - inStaging`) they become
> the display's "starting upload", since that tier is whatever in `uploading` cannot say what it is
> waiting on. The identity still balanced — the entries were banked, just in the wrong term — so the
> only visible symptom was on screen: `24 objects starting upload` climbing all run and never coming
> back down, with nothing on the wire. Balancing is therefore necessary but not sufficient, and the
> integration case that pins the identity also pins that parked entries are counted as parked.
>
> The subtraction was correct when it was written: one worker owned an item end to end, so
> "in hand and not in staging" did mean "past compression, on its way to the wire". Splitting the run
> into three stages added two states that satisfy the same subtraction while being neither.

Counting is per **item** while a channel holds **entries**, and one item can have several entries
queued at once — a pack pool splits into groups and the compressor dispatches them one after another.
So `WorkShare` takes the reading when the item's first entry parks and gives it back when the first
one is picked up: from that moment an uploader is working on the item, which is the further-along of
the two states. Counting entries would subtract more items than exist and break the identity.

### Subdividing `uploading`

These tiers are **subsets** of `uploading`, not new segments, so the identity needs no change. The
display subtracts them when computing "starting upload", or one item gets reported twice.

| Tier | Unit | Waiting for |
|---|---|---|
| `waitingOnPeer` | items | the first uploader of identical content to finish its **whole item** |
| `waitingOnSlot` | **volumes** | a slot on the global upload gate |
| `checking` | items | checking — a local read, or the family's cloud label listing: pushes no upload bytes |

What the `checking` stretches share is that they **emit no progress event at all**, while the
heartbeat only runs when a transfer is in flight. Without reporting them the screen shows a
motionless `1 object starting upload` for minutes — neither starting nor uploading:

| Site | Why it is slow |
|---|---|
| the dedup probe's whole-file read | a few GB on a NAS is tens of seconds |
| `stat` every member before packing | up to twenty thousand members in one pack |
| the post-compression recheck | as above, and a changed member means a full read |
| reading the family's label listing | a metadata listing round trip, registered only for multi-volume families — it replaced the old leftover-clearing deletes, and it is what the per-volume skip decides from |

> **Why not fold it into `preparing`.** That term is defined as "holding the global compression
> lock", and "always 0 or 1" is an invariant the code relies on. Mixing disk-reading work in breaks
> it, and the screen could no longer tell compressing from reading.

> **Why it publishes unthrottled.** During these stretches the caller produces no events at all, so a
> publish swallowed by the 200 ms throttle gets no later compensation from the caller. The heartbeat
> now covers the stretch (it runs on work in hand, not on a stream being open), so this is no longer
> the only thing standing between the operator and a frozen line — but it still buys immediacy the
> timer cannot: entering the stretch shows up at once instead of up to a second later, which is what
> makes a short check distinguishable from a stall. The cost is negligible: registration happens per
> item, not per volume.

**All four sites must pair in `try/finally`.** One missed pairing leaves that column stuck at an
inflated number for the rest of the run — which is exactly how `BeginPacking` was caught out once in
this project.

### `preparing = 0` with someone waiting means another run holds the lock

The compression lock lives on a DI singleton, so production does not run concurrently **across
backups** either — but `preparing` is tracked per backup. With two backups running, the other one
holds the lock, all of this backup's threads queue behind it, and this backup's `preparing` is 0.

| `preparing` | `waitingOnArchive` | Meaning |
|---|---|---|
| 1 | >0 | the lock is your own; the queue is moving |
| **0** | **>0** | **another run holds it** — you can go stop that one |

> Before `waitingOnArchive` was split out of `queued`, the screen showed ten thousand queued items
> and nothing that could say "this backup is blocked by another run". Once split, the diagnosis is
> free and the lock holder never has to be exposed.

`waiting for the archive slot` names one of the two global gates; the other is the volumes half of
`waiting for uploading` — one gate for production, one for upload. It deliberately avoids "compress":
a store-only pack does not compress and a raw passthrough never runs 7z, yet all three take that lock.

### A full pool is not a lock held elsewhere

Inside the staging area an item can be parked on two entirely different things, and `StageCoreAsync`
waits them out in order: first the pool's byte ceiling (`WaitForRoomAsync`, outside the lock), then
the lock itself. Both used to report as `waiting for the archive slot`, which made the table above
**actively wrong** — a run whose pool is full shows `preparing = 0` with someone waiting, reads as
"another run holds it", and sends the operator off to stop a backup that is not in the way.

| Entry | Ends when | What it points at |
|---|---|---|
| `N waiting for the archive slot` | a producer lets the lock go | a **producer** — possibly another backup's, which you can stop |
| `N waiting for staging room` | an **upload** frees pool space | the **wire**; compression is being throttled on purpose |

The second is the far commoner of the two: an upload-bound run fills `StagedLimitBytes` and parks
there for most of its life, which is the backpressure working exactly as designed. Reported under the
first entry's name, the single state a healthy run spends its time in wore the diagnosis of a
pathological one.

The wait is registered **only when there is really no room** — `WaitForRoomAsync` returns early when
the pool has space, before touching the tracker. Registering unconditionally would force two publishes
per item for a wait that never happened, the same trade the upload gate makes when it finds itself
free. `StageWithoutBackpressureAsync` skips the wait by construction, so an uploader recompressing what
it has to resend can only ever queue on the lock.

The item identity gains a name and no imbalance, since the split is arithmetic — the two still add up
to "inside staging, without the lock":

```
processed + preparing + queued + waitingOnRoom + waitingOnArchive
          + awaitingCompression + awaitingUpload + uploading ≡ total
```

## The byte ledger

Every byte is in at most one segment. If they do not add up, this line is worse than nothing.

| Segment | Meaning |
|---|---|
| `workDone / workTotal original` | completed and total **source** bytes, pre-compression |
| `transferredBytes uploaded` | bytes this run actually pushed, post-compression and post-encryption |
| `unfinishedItemBytes` | already in the cloud, but the item they belong to has not settled |
| `checkingBytes` | compressed and on disk, but still in checking — not cleared to upload |
| `waitingToUploadBytes` | on disk with **not one byte on the wire**, whoever happens to own it |
| the in-flight volumes | on disk and moving; stated per stream in the in-flight list as `sent / total` |

Those three reconstruct the staging pool exactly — they are precisely what the snapshot subtracts
from it. The in-flight term is each volume's **whole** size, not the part already sent: the file
stays on the disk in full until its transfer completes and the per-volume release deletes it.

A fraction only means something when both sides share a unit, which is why the first is measured in
source bytes: the compressed total does not exist until compression has run. The ratio of `uploaded`
to original can legitimately exceed 100% — store-only plus encryption makes AES wrapping and archive
headers larger than the input, and that is exactly worth telling the user.

### Why credit per item rather than accumulate per volume

`uploaded` has to be read next to the **source bytes**, which are credited per item. Accumulating per
volume makes the numerator climb through the tens of minutes a large item takes while the denominator
sits still, so the percentage structurally overshoots 100% — measured at 112%, falling back to 99%
once that item completed. The larger the file the worse it gets, and it has nothing to do with the
compression ratio.

Per-item readings are also immune to two other biases: retransmitted bytes are not double-counted
(the cloud still holds one copy), and a dedup hit is not counted as sent.

### `unfinished` must have an owner

Crediting per item pushes completed volumes out of `uploaded` — but those bytes **really are in the
cloud** and must not vanish from the screen, or a large multi-volume item would make them disappear
for tens of minutes.

Originally this was a monotonically increasing scalar, reconciled at settle time by subtracting the
item-level delta. On the happy path both sides balance exactly. **On the failure path they do not**: a
family that uploads a few volumes and then throws has already added them, while the settle never
runs; the retry then subtracts only the attempt that succeeded, and the first attempt's bytes are
stranded for the rest of the run — measured at 2 GB on a 3 TB backup at a moment when not a single
byte was in flight.

It is now a ledger keyed by **blobRef**, and each entry has a life:

| Action | When | Effect |
|---|---|---|
| `BeginUpload(owner)` | before the upload | opens the entry. **Opening resets** — a retry reuses the key, wiping the previous attempt |
| `EndItem(volume)` | each volume completes | adds to the family it belongs to |
| `ConfirmUpload(owner)` | after the item's own accounting | records that the cloud acknowledged the whole family |
| `SetTransferred(total)` | item settles | **deletes** confirmed entries outright, rather than subtracting a number |
| `EndUpload(owner)` | `finally` | discards anything never confirmed |

**The key is blobRef, not the ticket.** A ticket is drawn fresh on every upload call, so a retry gets
a new one and could never find the entry it left behind. blobRef survives a retry on both paths: a
single file's ref is its content address, and a pack's id is drawn *outside* the retry unit.

**Confirm and end are separate** rather than clearing unconditionally in `finally`: between the family
completing and the item settling there is still index and journal writing, and clearing there would
make those bytes vanish between two segments for a moment when they are demonstrably in the cloud.

Volumes with a null owner — the download side — never enter this ledger. Repair's volumes carry
owners and ride the very same gate and window as the backup's, yet they stay out too, for a cleaner
reason: only `BeginUpload` opens an entry, repair never calls it, and `EndItem` books into an entry
that exists rather than inventing one. Under-reporting a volume is preferable to creating an entry
nobody owns and nobody can delete; that is precisely the shape of the old drift.

### The pool is booked early, so the screen has to split it

Pool occupancy is booked the moment production moves into staging, and **that is correct**: it is the
backpressure gate and it needs "how much is on the disk right now". Booking it a second later risks
blowing the temp disk. But that one number covers three completely different states, and the screen has
to take them apart or it will pair a total with the counts of one part.

**Archives still being checked come out first**, in both units. At the moment of booking, the archive
still has to pass the post-compression recheck, and a multi-volume one first reads its family's label
listing — the per-volume skip-or-overwrite decision is made from it, nothing is deleted up front.
If the recheck finds a member changed, **the whole archive is discarded and recompressed** —
not one byte goes up. Calling that ready to send over-promises, so the `checking` tier has a byte side,
and its volumes come out alongside its bytes or the two waiting figures contradict each other.

**The in-flight volumes come out next, whole.** Their files really do lie in the pool in full until the
transfer completes, but what is left is meant to say "nothing of this is moving", and the unsent tail of
a volume on the wire is already on screen, per stream, as that stream's `sent / total`.

**What is left is `waitingToUploadBytes`**, with `waitingToUploadVolumes` beside it: everything on the
staging disk that has not one byte on the wire. Ownership plays no part — an archive parked in the
hand-off channel and one an uploader claimed ten minutes ago are the same thing to an operator.

> **Rationale.** Why ownership plays no part in this figure is set out under
> [the display](#the-display-two-lines-one-timeline), where the entry it feeds is described.
>
> The object count comes from the item ledger rather than from counting archives on disk, and must not
> be expected to divide into the bytes. Three of the entry kinds own no archive at all (a dedup hit, a
> resume hit, a raw in-place item), so a store-only run can queue five figures of objects against almost
> no bytes. That pairing is the answer to "should I worry about the temp disk": no — the queue is deep in
> items, not in bytes. Counting archives on the disk instead would erase exactly those objects.
>
> Peer waiters are excluded from the object count (they have their own entry naming the reason) while
> their archives still count in the two figures above, whose basis is "on the disk, not on the wire"
> rather than "which object owns it".

The subtractions never overlap. Checking happens entirely before the first volume takes off, so one
archive can never be hit by both "in flight" and "being checked". Only volumes that came out of the pool
are subtracted from it: the raw in-place route uploads the user's own file, never staged and never
charged, so it is excluded from the in-flight subtraction by an explicit flag.

## Speed: the clock only runs while something is on the wire

> **Speed = throughput over the time when at least one stream is open on the wire.**

- No transfer in flight (compressing, queuing, waiting for a slot) → that time does not enter the
  denominator.
- A transfer in flight but no bytes moving → that time **does** enter it, pulling the speed down so
  the stall becomes visible.

> **Rationale — what a wall-clock window did instead.** The real rhythm is "compress for tens of
> seconds, send for a few", so a 10-second rolling window over wall-clock timestamps changed meaning
> with the length of the pause. A gap under 10 s left samples on both sides in the window, so the
> byte-free stretch diluted the speed. A gap over 10 s triggered no sample at all, so on resume the
> old samples aged out as a batch, that tick reported 0, and the next window covered only what came
> after — pure wire speed with the compression entirely uncounted. What the user saw was "the speed
> is jumpy": same wire, same transfers, the number bouncing between half and full speed with the
> occasional 0. Meanwhile a dead network produced **no event at all**, so the display froze on the
> last number before the stall — the one case that most needs an alarm was the one you could not see.

The predicate already exists: in-flight registration always happens after acquiring a gate slot, so
"how many streams are on the wire" is exact. The tracker maintains a **virtual timeline** that only
advances during active stretches, and both the sample queue and the window are measured on it.
Mutating the active set and toggling the clock happen inside the same critical section.

> The rejected alternative — keep wall-clock timestamps and subtract idle intervals inside the window
> — needs an idle-interval table plus handling of intervals cut in half by the window edge:
> equivalent to the virtual clock and far more complex.

**When reading this number**: the virtual clock is **frozen** while nothing is in flight, so the
speed does not decay to zero during a silence — it holds "the speed during the most recent stretch of
uploading". The `nothing on the wire right now` beside it already states that nothing is moving.

**The heartbeat**: a stall produces no events, so a 1-second timer does one publish. It runs **while
the stage holds work** — not merely while a stream is open. Two guards are not optional — the callback
first checks whether the stage is complete (a finished stage must not get a late extra snapshot, and
disposal cannot recall an already-queued callback), and it wraps in `try/catch`, because it runs on a
thread-pool timer thread where no caller can catch anything and an escaping exception takes down the
process by default. Progress reporting has no business bringing down a running backup.

> **Why work in hand, not a stream on the wire.** The two are not the same, and the gap is not small:
> a dedup hit, a resume hit and a raw in-place item all settle without a byte going over the wire, so
> a run whose remaining work is mostly hits can spend hours holding dozens of items with nothing in
> flight. Gated on the stream, the stage published nothing for that whole stretch and the UI went on
> displaying the snapshot taken the instant the last volume finished — its queue depths, its byte
> columns, its `nothing on the wire right now`, every one of them a photograph. Measured in the field
> as `+66.8 MB on the cloud · 24 objects starting upload` motionless for hours, disappearing whenever
> a transfer started (a live snapshot finally replaced it) and returning to identical figures when
> that transfer ended.
>
> The premise it rested on was that pure compression has nothing new to report. That was true of a
> much smaller record; `preparing`, the staged and checking byte columns and every queue depth all
> move throughout compression. A frozen line reads as a hang, and hides one.

**A frozen clock skips the sample, not the publish.** Every sample taken while the virtual clock is
stopped carries the same timestamp, so time-based eviction can never remove it: the queue would fill
with duplicates until the `MaxSamples` backstop began dropping from the head, and the first ones
dropped are exactly the pre-freeze samples carrying a real span — collapsing the reading from "the
last speed seen on the wire" to 0. The sample says nothing anyway, since the byte total cannot move
with no stream open. Skipping it is what lets the heartbeat keep publishing through a freeze without
corrupting the window.

The virtual clock applies to Uploading, Restoring and Verifying — the three stages that genuinely
report as they transfer. Everything else leaves it off, or the clock would sit at 0 forever and the
speed would be permanently 0.

On the download side the progress callback is a **factory**, not a single instance: the SDK reports
bytes cumulative *within one call*, and turning cumulative into delta is done against that instance's
own baseline. Sharing one across volumes would mis-credit the next volume starting from zero.

## ETA

ETA deliberately does **not** use the speed figure. It extrapolates from
`elapsed × remaining work ÷ completed work`, i.e. an average over the whole run.

> **Rationale.** Same rhythm, same reason: during compression the speed window holds no bytes at all,
> so an ETA derived from it would disappear entirely and then reappear as an implausibly small number
> the moment compression ended. Those tens of seconds are part of the remaining time, and a
> whole-run average includes them by construction.

The denominators keep growing until the diff finishes, so the ETA — like the percentage — is withheld
until the total settles. Extrapolating from a growing denominator makes the remaining time shrink and
then bounce back.

## The display: two lines, one timeline

The first line is what has **settled**; the second is what has not. The dividing line is a question
you can actually answer: **can this number still go backwards?**

```
Uploading: 6,676 of 11,004 objects · 1.728 TB / 2.728 TB original (62%) · 1.728 TB uploaded (100% of original) · 13.3 MB/s · ~1d 10h left
In flight: +3.400 GB on the cloud · 5 volumes uploading · 118 objects waiting for uploading (2,043 volumes on the staging disk, 9.201 GB) · 1 object waiting for staging room · 4,365 objects queued
```

The second line runs **backwards along the timeline**, with counts and bytes interleaved at the point
each belongs to. An item's forward order is:

```
queued → waiting for the compressor → waiting for staging room → waiting for the archive slot
       → preparing → [archive lands on disk] → checking files
       → waiting for uploading → starting upload → waiting on peer → uploading
       → on the cloud → settled (first line)
```

**The whole upload-side wait is one entry**, `N objects waiting for uploading (M volumes on the staging
disk, X)`: one population — everything on the staging disk with not one byte on the wire — stated three
ways. The volume count is dropped when it equals the object count, since one volume per object means
nothing was split and the word would carry no information; only that term goes, the size stays. The
bytes are dropped when zero, which is a real state rather than a rounding artefact — and with both gone
the parentheses go with them.

Nothing on the wire is in any of the three, so this entry and `5 volumes uploading` never overlap.

> **Why the volumes say where they were measured.** The two counts have different sources — objects
> comes from the item ledger, volumes and bytes are measured off the staging pool — and a bare
> `362 objects (119 volumes, 6.467 GB)` puts them in a ratio the reader will try to form. The only
> available reading of "fewer volumes than objects" is that several objects were merged into one volume,
> and that never happens: an object's volumes live in its own directory and belong to it alone. What the
> gap actually means is the opposite — the objects owning **no archive at all** (a dedup hit, a resume
> hit, a raw in-place item) never reached the disk to be counted, so they are in the left number and not
> the right one. On a store-only or raw-heavy run those are the majority, which is exactly when the pair
> is on screen. Four words name the basis and the question does not arise.

> **Rationale.** This was two entries, split by **ownership**: had an uploader thread picked the archive
> up yet? That is an implementation detail nobody at the screen can act on, and side by side the two read
> as the same thing counted twice.
>
> The real damage was what neither of them counted. An uploader walks its archive's volumes through a
> sliding window of `UploadConcurrency + 1`; a volume outside that window has no task open for it, so it
> is in no queue and was in no number — while being most of the bytes. Measured in the field:
> `8.268 GB in the uploaders' hands · 19 volumes (1.855 GB) waiting for uploading`, which invited exactly
> the wrong question ("only 19 volumes, so what is the 8 GB?"). One entry over one population, with the
> volume count included, answers it before it is asked.

**Bytes ride with the stage that owns them** rather than forming stages of their own: the wait's are in
its parentheses, checking's are the entry below its count, and the bytes actually on the wire are in the
in-flight list, per stream, as each stream's `sent / total`.

> **Why they must be interleaved.** With counts on one line and bytes on another, each was ordered by
> its own logic and nothing on screen placed `+2.0 GB on the cloud` next to `100 MB` on disk.
> The natural reading — "one item, two thirds uploaded, stuck before the last volume" — is wrong in
> every part: they are different items at different points, and volumes never stall between one
> another. This misreading really happened, and took a long conversation to unpick.

The `In flight:` prefix is load-bearing. Without it, `1.7 TB uploaded` on the first line and
`+3.4 GB on the cloud` on the second become another puzzle: both are bytes already in the cloud, so
why two lines?

Hard constraints on wording:

- Every entry has the shape `N <unit> <present participle>`, with no preposition.
- **The unit must be spelled out.** Upload registers per **volume** and everything else per **item**;
  without units, adding the two kinds together exceeds the total — measured `5,346 + 5 + 1,031 =
  6,382 > 6,378`, the extra 4 being "5 volumes − 1 item".
- `nothing on the wire right now` means *this instant*, not "not yet" — mid-run the line above
  already shows terabytes transferred.
- The percentage after `uploaded` must carry `of original`: a bare `(95%)` reads as upload progress,
  and the same line already contains a real progress percentage.
- **GB and TB carry three decimals, KB and MB one** (`formatBytes`, frontend only — the backend's
  `ByteSize.Human` writes the summary text and keeps one). A single decimal at the GB level moves in
  ~100 MB steps, so on a slow link the figure sits still while real progress happens; the digits are
  what show the number is alive. The zeros are padded rather than trimmed, so a number being watched
  never changes width under the eye. Below a GB one decimal already resolves to ~100 KB.

The string assembly lives in one module. **The entire difficulty of these two lines is order and
wording**, and once a string is inside JSX there is nowhere left to assert it — a wrong order raises
no error, it just regrows the "which of these two comes first?" question on screen.

### When the front of the pipeline has stopped

A run that is winding down, or held at the pause gate, is still a **running** run: it goes on reporting
progress, and the entries describing what is on the staging disk and on the wire go on moving — that is
exactly why a suspend can take minutes. But the two entries at the front of the timeline stop dead, and
their own wording promises motion the row directly above has just denied:

```
Suspending…
In flight: 1 volume uploading · 33 volumes (7 objects, 3.073 GB) waiting for uploading · 4,374 objects left for the next run
```

```
Paused
In flight: 1 volume uploading · 33 volumes (7 objects, 3.073 GB) waiting for uploading · 4,374 objects held by the pause
```

`queued` and `waiting for the compressor` collapse into **one** entry, because the hold makes them one
population — not started, and not going to be while it stands. The thing that told them apart, whether
the prober had read the item yet, decides nothing once nothing is consuming either queue.

The two words are not interchangeable. A wind-down **abandons** that queue for this run; a pause merely
**holds** it. `left for the next run` is true of every stop kind and not only of Suspend: `SettleStopAsync`
flushes the journal before it returns whatever the kind, and a `Canceled` run's row offers Resume off that
journal (`showsInterruptedNotice`). What a stop discards is the index version, not the work already done.

Where the boundary falls is not a display choice — it is the one place in the engine where the hold is
checked. The compressor's loop tests the stop intent and then the pause gate **before** it does anything
to the item it has just taken (`BackupOrchestrator`, the probed-queue loop); everything the line reports
downstream of that point is already past the check. So `waiting for staging room`, `waiting for the
archive slot`, `preparing`, `checking files` and the whole upload-side wait keep their own wording and
keep moving, and replacing them would take away the only numbers that answer *why is this still going?*

> **Rationale.** Under a wind-down the old line was worse than stale. `queued` is a subtraction —
> `enqueued − processed − inWork` — and draining the probed channel releases each item's share **without**
> marking it processed, so abandoned work migrates into that number and it **climbs** while the run winds
> down. An operator watching `Suspending…` was being told that 4,365 objects were queued and rising, as if
> a queue nothing was consuming were merely deep.

The hold is not in the snapshot and cannot be: the backend goes on counting the queue truthfully, and only
the row knows nothing is draining it. It comes from the two facts the server already reports —
`stopRequested` and the pause the warn line renders — so it survives a tab switch exactly as the buttons
do. A stop **outranks** a pause rather than being an alternative to it: `RequestStop` downgrades the pause
gate on its way past, so from that moment the gate can never hold anyone again and the run is winding down
whatever `pause` still says.

**The in-flight list** gives each concurrent transfer its own line with its own progress. The header
says *parallel* explicitly: seeing two or three names at once suggests parallel compression, which
never happens. The list is **never truncated** — its length is already bounded by the concurrency
setting, and the stuck transfer is usually the one that would have been folded away.

## The run summary

```
Files: 3 new, 1 modified, 12 deleted (4.7 GB)
Data: 1.2 GB changed at source → 310 MB uploaded
```

The operation log, the webhook notification and the Backups page are deliberate mirrors of each
other, so a figure added to one is added to all in the same shape.

**Deleted bytes are parenthesised right after the count**, where they read as "the size of *those*
files".

> **Rationale.** Twelve deleted files can be twelve empty log stubs or twelve 400 GB disk images, and
> the count alone was the one item on that line with no sense of scale attached — `new` and
> `modified` at least have `changed at source` standing behind them.

**What that number is not**: it is the size the deleted files *had at the source*, not space freed in
the cloud. Older versions still reference that content until retention retires them; what the cloud
actually gave back is the `freed` figure on the retention line. The two are different quantities and
must never be added together. It is kept out of the `Data` line on purpose — that line tracks one
thing, what changed at source versus what went over the wire, and deleted bytes are neither.

A zero makes its item disappear, and that extends to the parenthesis: a round that deleted twelve
empty files renders as plain `12 deleted`, not `12 deleted (0 B)`.

Symlinks carry length 0 — their content is the target string — so deleting one contributes nothing,
which is correct.

**An older backend sends no such field**, and the frontend then shows the count alone and **must
not** substitute 0. "Deleted nothing worth mentioning" is a claim, and nothing here supports it.

## Known limitations

- Progress is not persisted and resets to zero on restart. Suspend/resume continues the **run**, but
  the bar is redrawn from zero.
- Polling means a steady trickle of background requests while the page is open — negligible, but the
  only continuously consuming part of this design.
- **Check loses its report on refresh.** The server lacks queryable state; it is not that the client
  fails to ask. See [check-restore-repair.md](check-restore-repair.md).
- `checking` does not cover recording a pack plus its per-member verbose logging, which is only slow
  with verbose logging on. That stretch shows as a silence with no name.
- Speed freezes rather than decaying during a silence; read it together with `nothing on the wire
  right now` beside it.

## See also

- [pipeline.md](pipeline.md) — the stages these figures describe, and why the upload gate sorts by item age
- [run-lifecycle.md](run-lifecycle.md) — what the pause and suspend states look like on screen
- [web-ui.md](web-ui.md) — where these lines are rendered
