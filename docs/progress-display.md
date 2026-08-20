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
| `checking` | items | local checking: pushes no bytes, waits on nothing |

What the `checking` stretches share is that they **emit no progress event at all**, while the
heartbeat only runs when a transfer is in flight. Without reporting them the screen shows a
motionless `1 object starting upload` for minutes — neither starting nor uploading:

| Site | Why it is slow |
|---|---|
| the dedup probe's whole-file read | a few GB on a NAS is tens of seconds |
| `stat` every member before packing | up to twenty thousand members in one pack |
| the post-compression recheck | as above, and a changed member means a full read |
| clearing leftover cloud volumes | network round trips, noticeable with many volumes |

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
| `waitingToUploadBytes` | on disk and **queued**: archives parked for an uploader, plus volumes queuing at the gate |
| `stagedBytes` | on disk and in an **uploader's hands**: the unsent tail of what is on the wire, plus peer waiters |

The last two plus `checkingBytes` plus the sent part of the in-flight streams reconstruct the staging
pool exactly — they are precisely what the snapshot subtracts from it.

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

Volumes with a null owner — the download side, repair paths bypassing the gate — never enter this
ledger. Under-reporting a volume is preferable to creating an entry nobody owns and nobody can
delete; that is precisely the shape of the old drift.

### The pool is booked early, so the screen has to split it

Pool occupancy is booked the moment production moves into staging, and **that is correct**: it is the
backpressure gate and it needs "how much is on the disk right now". Booking it a second later risks
blowing the temp disk. But that one number covers four completely different states, and the screen has
to take them apart or it will pair a total with the counts of one part.

**Archives still being checked come out first.** At the moment of booking, the archive still has to
pass the post-compression recheck, and a multi-volume one must first clear leftover cloud volumes. If
the recheck finds a member changed, **the whole archive is discarded and recompressed** — not one byte
goes up. Calling that ready to send over-promises, so the `checking` tier has a byte side.

**The upload-side queue comes out next, measured rather than inferred.** `waitingToUploadBytes` is
booked per queue **entry** as it parks (`EnterUploadQueue` / `LeaveUploadQueue`) and per **volume** as
it queues at the gate (`BeginWait(Slot, bytes)`), so it describes exactly the set the `N volumes + N
objects waiting for uploading` entry counts — and it is what goes in that entry's parentheses.

> **Rationale.** The pool total used to be printed beside those counts as `N ready to upload`, and read
> as an equality it was not one: the unsent tail of every volume on the wire sat inside it, so the
> number said the queue was fatter than it was. Nothing on screen could be used to correct it either —
> a reader cannot turn "118 objects" into bytes.
>
> It does not divide by the count, and must not be expected to. Two of the three entry kinds in that
> queue own no archive at all (a dedup hit, a resume hit, a raw in-place item), so a store-only run can
> queue five figures of objects against almost no bytes. That pairing is the answer to "should I worry
> about the temp disk": no — the queue is deep in items, not in bytes.

**What is left is `stagedBytes`**, and it means "in the uploaders' hands": the unsent tail of each
in-flight volume, plus the archives of items parked on a peer or not yet past their first volume. It
sits beside the in-flight count, which is mostly what it is.

The subtractions never overlap. Checking happens entirely before the first volume takes off, so one
archive can never be hit by both "in flight" and "being checked"; an entry leaves the hand-off channel
before its uploader queues a single volume; and a volume is registered in flight only once the gate has
let it through, which is the same instant its slot wait ends.

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
In flight: +3.400 GB on the cloud · 5 volumes uploading · 2 volumes + 118 objects (9.201 GB) waiting for uploading · 1 object waiting for staging room · 4,365 objects queued
```

The second line runs **backwards along the timeline**, with counts and bytes interleaved at the point
each belongs to. An item's forward order is:

```
queued → waiting for the compressor → waiting for staging room → waiting for the archive slot
       → preparing → [archive lands on disk] → checking files
       → waiting for uploading (objects half) → starting upload
       → waiting on peer → waiting for uploading (volumes half) → uploading
       → on the cloud → settled (first line)
```

**The two upload-side waits are printed as one entry**, `N volumes + N objects (X) waiting for
uploading`, and the units are what keep them apart: volumes are queuing at the global upload gate
(which queues per volume) and have an uploader on them; objects are compressed and claimed but not yet
picked up by any uploader at all. The bytes in the parentheses are measured on exactly those two waits.

> **Rationale.** As two entries they read as the same thing counted twice. The whole distinction rested
> on `an upload slot` versus `an uploader` — one word, carrying a stage boundary — and it did not carry
> it. The units were already doing that work, so the merge costs nothing a reader was actually using.
> The one thing it did cost was adjacency to the bytes those objects hold, and the parentheses buy that
> back with a number that is now the queue's own rather than the whole pool's.

**Bytes ride with the stage that owns them** rather than forming stages of their own: the queue's are
in its parentheses, checking's are the entry below its count, and what the uploaders are holding sits
beside the in-flight count as `X in the uploaders' hands`.

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
