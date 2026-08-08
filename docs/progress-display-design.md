# Run progress display

> Merged from five rounds: progress visibility (2026-07-26), the upload speed clock (07-28),
> the gap between "compressed" and "sending" (07-31), the `checking` tier and the archive-lock
> wait (08-06 / 08-07), and unfinished-byte ownership with the two lines merged (08-09).
> Organised **by topic** rather than by date: each section owns one figure, and the reasoning
> and the traps behind it stay with that figure.

## 1. What this has to answer

A 3 TB backup runs for over a day. Facing the screen, the user has exactly one question: **is it working, or is it hung?**

That question cannot be answered by "N objects processed". During Uploading an item first goes through 7-Zip — a 100 MB pack can take tens of seconds — and during that window not one byte is on the wire. From the outside that is indistinguishable from a genuine hang. So the whole design lands on one requirement: **every silent stretch lasting minutes must have a segment on screen that says what it is doing.**

The converse holds too: every number on screen must survive the question "where on the pipeline does this belong?". A number that cannot answer it will actively manufacture misreadings — §5.2 is exactly that, misread for a long time.

## 2. Where the state comes from

### 2.1 Server-authoritative, memory only

Progress lives **in memory only: it survives a refresh, not a restart**. Persisting it would leave a percentage frozen halfway through a run that is already dead, and would then require a whole "mark orphans at startup" pass to clean up an illusion of its own making. `BackupRunner` and `BackupBusyTracker` are both in-memory singletons, cleared with the process, so no inconsistency is possible.

> Suspend/resume (see [backup-suspend-resume-design.md](backup-suspend-resume-design.md)) later lets the **run itself** continue across a restart, but the progress figures still are not persisted — what resumes is the work, not the bar.

### 2.2 Scheduled tasks take the same path

One of the earliest defects: the scheduler bypassed `BackupRunner` and called `BackupOrchestrator.RunAsync(request, null, ct)` directly — that second argument is the progress callback, and it was `null`. So scheduled backups never registered any run state at all, and scheduled backups are the normal case.

The fix routes the scheduler and the UI button down the **same path**, which makes progress, the busy lock and error handling consistent by construction. Ownership of the busy lock is then expressed **by method choice, not by a boolean**:

```csharp
/// UI: take the busy lock and fire-and-forget. Returns the existing state if already running.
public BackupRunState Start(int configId)

/// Scheduler: the caller **already holds** the busy lock for this (account, container).
public Task<BackupRunState> RunTrackedAsync(int configId, CancellationToken ct)
```

Get a boolean wrong once and you either refuse to run or leave the lock unheld — neither shows up at compile time. A method name cannot be got wrong the same way. The scheduler additionally has to check `Failed` and throw: the runner swallows exceptions rather than propagating them, and without that check a failure is recorded as a success.

### 2.3 Polling, not SSE or WebSocket

The list refreshes every 5 seconds (a pure local SQLite query). While anything is active, the client polls **the one** status endpoint matching that activity once per second, and sends nothing at all when everything is idle. A long-lived connection brings reconnection and reverse-proxy compatibility problems, and the cost here is negligible.

The polling loop must not live inside a closure — the original defect was a `while` loop inside `run()` that only started if the button was clicked in that browser session. Refreshing the page lost it, even though the server knew all along that the run was alive.

### 2.4 Check is the exception

`POST /backup-configs/{id}/check` is **synchronous**: it holds the HTTP request until the report is produced, with no runner and no matching `GET`. So on a refresh, a check in progress has no queryable state on the server. The badge still shows `Checking` (from the in-memory busy tracker), but the report is lost. Fixing it means reshaping check into the same runner pattern repair uses.

## 3. Stages and their units

Each stage counts a **different kind of thing**. Sharing one word makes it look like packing did not work — a backup that packs 46,624 files into 4,995 archives is reporting both numbers correctly.

| Stage | Unit | What one unit is |
|---|---|---|
| Scanning | `entries` | A filesystem entry found by the walk. The total is unknown until the scan ends, so it reads `N entries so far` |
| Diffing | `files` | A source file, whether or not it later gets packed |
| Uploading / Restoring | `objects` | A stored object: one pack archive, or one single-file blob |
| Cloud (check) | `objects` | A stored object — one `HEAD` per pack, not per file |
| Verifying (check) | `objects` | A pack that gets downloaded, extracted and re-hashed |
| Local (check) | `files` | An index entry, i.e. a source file |
| Orphans (cleanup) | `blobs` | A blob in the container |

Completion is computed from **source bytes**, not from the item count: one item can be a 6.8 GB single file or a pack of several hundred 5 KB files, and counting them equally is meaningless (measured: the count reads 75% while the bytes are at 31%). Only when the byte total is unknown — Scanning and Diffing report no byte workload — does it fall back to the count.

## 4. The item ledger: one identity

```
processed + preparing + queued + waitingOnArchive + uploading ≡ total
```

**The identity is the skeleton of this design, not decoration.** If it holds, every item at every instant belongs to exactly one segment on screen. If it does not, the item belonging to none of them is precisely the one that is stuck — and it can only be found by lining up several screenshots and doing subtraction. That trap has been stepped in twice.

### 4.1 The terms

| Term | Unit | Meaning |
|---|---|---|
| `processed` | items | Finished and settled |
| `preparing` | items | Holding the **global compression lock**, producing volume files. There is one lock, so this is always 0 or 1 |
| `waitingOnArchive` | items | Picked up by a worker, queuing behind that same lock |
| `queued` | items | Still in the queue, not picked up by any worker |
| `uploading` | items | Everything past the staging stage: in flight, waiting on a resource, or doing local checking |

`uploading` is `inWork - _inStaging` (items in hand minus items still in staging) and deliberately **not** `_inUpload` (the `BeginUpload`/`EndUpload` pair): the latter only starts counting at `UploadStagedBlobAsync`, while the stretch between "compressed" and there still contains checking and reservation coordination. An item can sit there for minutes without being in it. Using it as the measure makes the ledger fail exactly when it matters most — and a ledger that fails there is the whole reason this term exists. The subtraction folds that gap in, so the identity does not depend on any call site.

### 4.2 Subdividing `uploading`: which stretch it is stuck in

These tiers are **subsets** of `uploading`, not new segments, so the identity needs no change. The display must subtract them when computing `starting upload`, or one item gets reported twice.

| Tier | Unit | Waiting for | Marked at |
|---|---|---|---|
| `waitingOnPeer` | items | The first uploader of identical content to finish the **whole item** | the waiting side of `LocalDedupResolver` |
| `waitingOnSlot` | **volumes** | A slot on the global upload gate (the gate queues per volume) | `VolumeUploadScope.RunAsync` |
| `checking` | items | Local checking: pushes no bytes, waits on nothing | the four sites below |

What the four stretches share is that they **emit no progress event at all**, while the heartbeat only runs when a transfer is in flight. Without reporting them the screen shows a motionless `1 object starting upload` for minutes — neither starting nor uploading:

| Site | Stretch | Why it is slow |
|---|---|---|
| `ProbeForDedupAsync` | Single-file dedup pre-check, reads the whole file for three hashes | A few GB on a NAS is tens of seconds of reading |
| `TryStat` before packing | `stat` every member | Up to twenty thousand members in one pack |
| Post-compression recheck | `stat` every member, re-hash the ones that moved | As above, and a large changed member means a full read |
| `ClearLeftoverVolumesAsync` | List and delete leftover cloud volumes before a multi-volume upload | Network round trips, noticeable with many volumes |

**Why not fold it into `preparing`**: `preparing` is defined as "holding the global compression lock", and "always 0 or 1" is an invariant the code relies on. Mixing disk-reading work in breaks it, and the screen could no longer tell compressing from reading.

**Why it publishes unthrottled**: `BeginChecking`/`EndChecking` bypass the 200 ms throttle, like `BeginWait`. During checking the caller produces no events at all, so a publish swallowed by the throttle gets no later compensation — the screen would stay frozen on the old snapshot until the stretch ends. Swallow it and the whole change was pointless. The cost is negligible: registration happens **per item**, not per volume like in-flight registration.

**No heartbeat**: there are no new readings during these stretches, so pushing an identical snapshot every second is pointless. One publish on entry and one on exit is enough.

**Must be paired**: all four sites wrap in `try/finally`. `BeginPacking` has already been caught out once in this project — added without a matching call, that column stayed stuck at an inflated number for the rest of the run.

### 4.3 `preparing = 0` with someone waiting means another run holds the lock

`_compressLock` lives on `StagingArea`, which is a DI singleton — production (compression, store-only packing, raw copying) **does not run concurrently across backups either**. But `preparing` is tracked per backup. So with two backups running: the other one holds the lock, all of this backup's threads queue behind it, and this backup's `preparing` is 0.

Before `waitingOnArchive` was split out of `queued`, the screen showed ten thousand `queued` and nothing that could say "this backup is blocked by another run". Once split, the diagnosis is free — no need to expose the lock holder:

| `preparing` | `waitingOnArchive` | Meaning |
|---|---|---|
| 1 | >0 | The lock is your own; the queue is moving |
| **0** | **>0** | **Another run holds it** — you can go stop that one |

The wording `waiting for the archive slot` mirrors `waiting for an upload slot` on the same line (two global gates, one for production and one for upload). It deliberately avoids compress/compressor: store-only packs without compressing and a raw passthrough never runs 7z, yet all three take the same lock.

## 5. The byte ledger: segments never overlap

Every byte is in at most one segment. If they do not add up to the total, this line is worse than nothing.

| Segment | Meaning |
|---|---|
| `workDone / workTotal original` | Completed and total **source** bytes, pre-compression. A fraction only means something when both sides share a unit — the compressed total does not exist until compression has run |
| `transferredBytes uploaded` | Bytes this run actually pushed, post-compression and post-encryption. The ratio to original can exceed 100%: store-only plus encryption makes AES wrapping and archive headers larger than the input, and that is exactly worth telling the user |
| `unfinishedItemBytes` | Already in the cloud, but the item they belong to has not settled |
| `checkingBytes` | Compressed and on disk, but still in checking — not cleared to upload |
| `stagedBytes` | Compressed, **checked**, waiting for its turn |

### 5.1 Why credit per item rather than accumulate per volume

`uploaded` has to be read next to the **source bytes**, which are credited per item. Accumulating per volume makes the numerator climb through the tens of minutes a large item takes while the denominator sits still — it only jumps when the item completes. The percentage then structurally overshoots 100% (measured 112%, falling back to 99% once that item completed). The larger the file the worse it gets, and it has nothing to do with the compression ratio.

Per-item readings are also immune to two other biases: retransmitted bytes are not double-counted (the cloud still holds one copy), and a dedup hit is not counted as sent (an if-missing hit against an existing blob puts zero bytes on the wire).

### 5.2 `unfinished` must have an owner

Crediting per item pushes completed volumes out of `uploaded` — but those bytes **really are in the cloud** and must not vanish from the screen. A large item split into many volumes would otherwise make them disappear for tens of minutes. So they sit in their own segment and are folded in when the item completes.

Originally this was a **monotonically increasing scalar**, reconciled at settle time by subtracting the item-level delta. On the happy path both sides balance exactly (both are the volume file's `FileInfo.Length`). **On the failure path they do not**: a family that uploads a few volumes and then throws has already added them, while `state.AddUploaded` runs *after* `VolumeBlobIO.UploadAsync` and never executed. The retry then subtracts only the attempt that succeeded, and the first attempt's bytes are stranded for the rest of the run — measured at 2 GB on a 3 TB backup at a moment when not a single byte was in flight. `Math.Max(0, …)` guards the opposite direction only.

It is now a ledger keyed by **blobRef**, and each entry has a life:

| Action | When | What it does |
|---|---|---|
| `BeginUpload(owner)` | before `UploadAsync` | Opens the entry. **Opening resets** — a retry uses the same key, so the previous attempt is wiped |
| `EndItem(volume)` | each volume completes | Adds to the family it belongs to |
| `ConfirmUpload(owner)` | after `state.AddUploaded` | Records that the cloud acknowledged the whole family |
| `SetTransferred(total)` | item settles | **Deletes** confirmed entries outright (rather than subtracting a number) |
| `EndUpload(owner)` | `finally` | Discards anything never confirmed |

**The key is blobRef, not the ticket.** A ticket is drawn fresh on every `UploadAsync` call, so a retry gets a new one and could never find the entry it left behind. blobRef survives a retry on both paths: a single file's ref is its content hash, and a pack's id is drawn *outside* the retry unit (which itself exists so a recompressed attempt overwrites the same volume family).

**`ConfirmUpload` and `EndUpload` are separate** rather than clearing unconditionally in `finally`: between the family completing and the item settling there is still index and journal writing, and clearing there would make those bytes vanish between two segments for a moment when they are demonstrably in the cloud. Split apart, success and failure share one `finally` and differ only in whether confirmation happened.

Volumes with a null `owner` (the download side, repair paths that bypass the upload gate) never enter this ledger. If the upload side ever fails to pass one, under-reporting a volume is preferable to creating an entry nobody owns and nobody can delete — that is precisely the shape of the old drift.

### 5.3 `ready to upload` excludes archives still being checked

`_stagedBytes` is booked the moment production moves into staging, and **that is correct**: it is the backpressure gate, and it needs "how much is on the disk right now". Booking it a second later risks blowing the temp disk.

But at that moment the archive still has to pass the post-compression recheck, and a multi-volume one must first clear leftover cloud volumes. If the recheck finds any member changed during compression, **the whole archive is discarded and recompressed** — not one byte goes up. Calling that "ready to upload" over-promises.

One counter serving both the backpressure gate and the display, with different requirements for "when does it start counting". The split reuses the existing `checking` tier by giving it a byte side: `BeginChecking(bytes)` / `EndChecking(bytes)` accumulate `_checkingBytes`, which the snapshot subtracts from `staged` and reports separately. Of the four call sites only two hold an archive (post-compression recheck, clearing leftover volumes) and pass real bytes; the other two pass 0 because nothing is in the pool yet.

The three subtractions never overlap: checking happens entirely before the first volume takes off, so one archive can never be hit by both "in-flight sent" and "being checked".

## 6. Speed: the clock only runs while something is on the wire

### 6.1 The problem

Speed was originally a 10-second rolling window over wall-clock timestamps. The real rhythm of uploading is "compress for tens of seconds, send for a few", so the figure's meaning changed with the length of the pause:

- **Gap < 10 s**: samples on both sides are still in the window, so the byte-free stretch lands in the denominator and dilutes the speed.
- **Gap > 10 s**: nothing triggers a sample during the pause; on resume the old samples age out as a batch, so that tick reports 0 and the next window covers only what came after — pure wire speed, with those tens of seconds of compression entirely uncounted.

What the user saw was "the speed is jumpy": same wire, same transfers, the number bouncing between half and full speed with the occasional 0.

The other half runs the opposite way. `PublishIfDue` only fires on byte movement or count progress. When a stream is open but not moving a single byte (a dead network), there is **no event at all** and the display stays frozen on the last number before the stall — the one case that most needs an alarm is the one you cannot see.

### 6.2 Target semantics and the virtual clock

**Speed = throughput over the time when at least one stream is open on the wire.**

- No transfer in flight (all compressing, queuing, waiting for a slot) → that time does not enter the denominator.
- A transfer in flight but no bytes moving → that time does enter it, pulling the speed down so the stall becomes visible.

The predicate already exists: in-flight registration always happens *after* acquiring a gate slot, so `_active.Count > 0` is exactly "how many streams are on the wire". So the tracker maintains a virtual timeline that only advances during active stretches, and both the sample queue and the 10-second window are measured on it. Mutating `_active` and toggling the clock must happen inside the same critical section.

The **rejected** alternative: keep wall-clock timestamps and subtract idle intervals inside the window. That needs an idle-interval table plus handling of intervals cut in half by the window edge — equivalent to the virtual clock and far more complex.

> **When reading this number**: the virtual clock is **frozen** while nothing is in flight, so the speed does not decay to zero during a silence — it holds the last reading ("the speed during the most recent stretch of uploading"). The `nothing on the wire right now` beside it already states that nothing is moving.

### 6.3 The heartbeat

A stall produces no events, so the virtual clock advancing helps nobody unless something recomputes. A 1-second timer runs **only during active stretches** and its callback does one publish. Two guards were added during implementation, and neither is optional: the callback first checks `_completed` (a finished stage must not get a late extra snapshot — `Dispose` cannot recall an already-queued callback), and the callback wraps in `try/catch` — it runs on a thread-pool timer thread where no caller can catch anything, and an escaping exception takes down the whole process by default. Progress reporting has no business bringing down a running backup.

### 6.4 The switch and where it applies

The constructor parameter defaults to `false`, leaving stages that never call `BeginItem` unchanged — for those the virtual clock would sit at 0 forever and the speed would be permanently 0.

| Stage | Switch | Reason |
|---|---|---|
| Uploading | `true` | Compress for tens of seconds, send for a few |
| Restoring | `true` | Download and extraction alternate |
| Verifying | `true` | Download, then extract and re-hash |
| Scanning / Diffing / LoadingIndex / Metadata / Local / Orphans / Cloud | `false` | Never call `BeginItem` |

All three enabled stages genuinely report as they transfer. On the download side the progress callback is a **factory**, not a single `IProgress<long>`: the SDK reports bytes cumulative *within one call*, and turning cumulative into delta is done against "this instance's own baseline". Sharing one instance across volumes would mis-credit the next volume starting from zero. One factory call per volume mirrors "one `ItemProgress()` per volume" on the upload side.

The download side's in-flight window also narrowed: `EndItem` now fires when the download itself ends, so the subsequent extraction, re-hashing and disk writes no longer count. What the virtual clock measures is therefore genuinely "how fast the wire is", with local CPU time kept out of the denominator.

## 7. ETA

ETA deliberately does **not** use the speed figure. It extrapolates from `elapsed × remaining work ÷ completed work`, which is equivalent to an average over the whole run.

Same rhythm, same reason: during compression the speed window holds no bytes at all, so an ETA derived from it would disappear entirely and then reappear as an implausibly small number the moment compression ends. Those tens of seconds are part of the remaining time, and a whole-run average includes them by construction.

The denominators (total items and total work) keep growing until the diff finishes, so the ETA — like the percentage — is withheld until the total settles. Extrapolating from a growing denominator makes the remaining time shrink and then bounce back.

## 8. The display: two lines, one timeline

The first line is what has **settled**; the second is what has not. The dividing line is a question you can actually answer: can this number still go backwards?

```
Uploading: 6,676 of 11,004 objects · 1.7 TB / 2.7 TB original (62%) · 1.7 TB uploaded (100% of original) · 13.3 MB/s · ~1d 10h left
In flight: +3.4 GB on the cloud · 5 volumes uploading · 2.6 GB ready to upload · 1 object preparing · 4,365 objects queued
```

The second line runs **backwards along the timeline**, with counts and bytes interleaved at the point each belongs to. An item's forward order is:

```
queued → waiting for the archive slot → preparing → [archive lands on disk]
       → checking files → ready to upload → starting upload
       → waiting on peer/slot → uploading → on the cloud → settled (first line)
```

**Why they must be interleaved.** With counts on one line and bytes on another, each was ordered by its own logic and nothing on screen placed `+2.0 GB on the cloud` next to `100 MB ready to upload`. The natural reading — "one item, two thirds uploaded, stuck before the last volume" — is wrong in every part: they are different items at different points, and volumes never stall between one another (that loop does exactly three things: take a slot, `PUT`, delete the local file). This misreading really happened, and took a long conversation to unpick.

The `In flight:` prefix is load-bearing. Without it, `1.7 TB uploaded` on the first line and `+3.4 GB on the cloud` on the second become another puzzle: both are bytes already in the cloud, so why two lines?

A few hard constraints on wording:

- Every entry has the shape `N <unit> <present participle>`, with no preposition (`in checking files` would be the only prepositional phrase on the line).
- The unit must be spelled out. Upload registers per **volume** and everything else per **item**; without units, adding the two kinds together exceeds the total (measured `5,346 + 5 + 1,031 = 6,382 > 6,378`, the extra 4 being "5 volumes − 1 item").
- `nothing on the wire right now` means *this instant*, not "not yet" — mid-run the line above already shows terabytes transferred.
- The percentage after `uploaded` must carry `of original`: a bare `(95%)` reads as upload progress, and the same line already contains a real progress percentage.

The string assembly lives in `frontend/src/lib/stageLines.ts`. The entire difficulty of these two lines is **order** and **wording**, and once a string is inside JSX there is nowhere left to assert it — a wrong order raises no error, it just regrows the "which of these two comes first?" question on screen.

### 8.1 The in-flight list

Each concurrent transfer gets its own line with its own progress (`path — 41.2 MB / 220.0 MB · 18%`). The header says *parallel* explicitly: seeing two or three names at once suggests parallel compression, which never happens. The list is **never truncated** — its length is already bounded by the concurrency setting, and the stuck transfer is usually the one that would have been folded away.

## 9. Why the upload gate arbitrates by item age

This belongs to the upload path, but it directly determines how large the figure in §5.2 gets, so it is recorded here.

The gate's sort key is `(ticket, volume)`: a family of volumes takes one ticket when it starts uploading, lower tickets win, and within a family volumes go in order. It used to be a bare `SemaphoreSlim`, first-come-first-served — and with `UploadConcurrency + 1` consumers and globally serialised compression, the steady state was "one item compressing, N uploading", each getting about one stream: **N items half-finished at once**.

That is not merely untidy. An item is journalled and cleared from the in-flight set only once its whole volume set is confirmed, so "how many items are simultaneously half-finished" is exactly how much work an interruption throws away: `Stop now` deletes every in-flight item's volumes, and a suspend or crash makes each one recompress and reupload from scratch. Arbitrating by item age brings that number down to typically one or two.

Throughput is unaffected because the slots stay saturated: whatever the oldest item cannot use goes to the next one immediately. The sliding window is therefore `MaxParallelPerItem + 1` — the extra volume is a **baton**. Without it, at each volume changeover (`Release` hands off synchronously inside `finally`, while this family's next volume needs a continuation to get queued) a slot leaks to a newer item; one leak per completed volume and the priority is priority in name only.

The baton covers the common timing, **not all of it**: after this family is admitted it still needs a continuation to post the next waiter, and a starved thread pool can let a volume slip through that gap. In production the gap is microseconds against seconds per volume, so at most the occasional volume. Making it absolute would mean "each family holds onto its slots", which turns this layer inside out for no proportionate gain. The unit test therefore bounds it at "fewer than 2 volumes stolen by a newer family before the older one finishes", not at 0.

## 10. Known limitations

- Progress is not persisted and resets to zero on restart. Suspend/resume continues the **run**, but the bar is redrawn from zero.
- Polling means a steady trickle of background requests while the page is open. A local query every 5 seconds is negligible, but it is the only continuously consuming part of this design.
- **Check still loses its report on refresh** (§2.4). The server lacks queryable state; it is not that the client fails to ask.
- `checking` does not cover `RecordPack` plus the per-member `LogFileAsync` after a pack upload — that is only slow with verbose logging on. Running a large backup with verbose enabled shows that stretch as a silence with no name: one serialised append per member.
- Speed freezes rather than decaying during a silence (§6.2); read it together with `nothing on the wire right now` beside it.

## See also

- The user-facing account is *Reading the Details panel* in [README.md](../README.md).
- Suspend, pause and resume: [backup-suspend-resume-design.md](backup-suspend-resume-design.md)
- The backup engine and upload path: [m4-backup-engine-design.md](m4-backup-engine-design.md)
