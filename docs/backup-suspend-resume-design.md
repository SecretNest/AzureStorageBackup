# Suspendable, pausable, resumable backups

## The problem

A running backup collapsed on one network error and every byte already uploaded was written off:

```
Failed: Retry failed after 6 tries. Retry settings can be adjusted in ClientOptions.Retry …
(The operation was cancelled because it exceeded the configured timeout of 0:01:40.) ×6
```

Another backup running at the same time was fine.

### Root cause: our own backoff never saw this error shape

The blob client options never configured `Retry`, so the Azure Storage SDK defaults applied: `MaxRetries = 5` (six attempts) with a 100-second network timeout. After all six timed out, Azure.Core threw an **`AggregateException`** whose message is the line above and whose six inner exceptions are all `TaskCanceledException`.

Our `IsTransient` recognised only two shapes:

```csharp
private static bool IsTransient(Exception ex) => ex switch
{
    RequestFailedException rfe => rfe.Status == 0 || rfe.Status >= 500 || rfe.Status is 408 or 429,
    IOException => true,
    _ => false,          // ← AggregateException lands here
};
```

`AggregateException` matches neither, falls to `_ => false`, and the retry policy therefore retried **zero times**. The exception went straight to the runner's catch-all and the whole run was marked Failed.

**The configured "exponential backoff from 200 ms, up to 5 attempts" never ran once for this error.**

From first error to giving up took about ten minutes: six 100-second network timeouts plus the SDK's own internal backoff.

### How much a failed run has to re-upload

The finishing steps (writing the index, the info file and local state) come last, so a failed run commits nothing. What was already uploaded meets one of three fates:

| Path taken | On re-run |
|---|---|
| Large file → its own `data/{fullHash}` | **No bytes re-uploaded.** It is recompressed and rehashed, but the upload is if-missing, so anything already in the cloud is skipped |
| Large file + encrypted + multi-volume | **Fully re-uploaded.** Leftover volumes are actively deleted first — AES uses a fresh salt/IV each time, so old and new volumes cannot be assembled into a readable archive |
| Small files → pack | **Always re-uploaded.** Pack ids carry a per-run random prefix and deliberately never repeat across runs, so if-missing cannot help |

For a backup dominated by small files, a re-run is essentially starting over.

### Two pre-existing gaps

- **Staging directory leak**: staging created `staged/{guid}` subdirectories and nothing cleaned them at startup, so a crash left them behind permanently.
- **Stopping meant losing everything**: cancelling was the only way to stop, and it wrote off the entire run with no second option.

## Goals

1. Network errors no longer destroy a run: pause, self-heal, and let the user nudge it.
2. A crash or restart no longer wastes the work: what has been confirmed in the cloud is recorded on disk, and the next start continues from there.
3. The user can pause deliberately, handing resources back, and resume whenever.
4. Blocks left behind by an interruption can be cleaned up safely, and blocks **still being reused must never be deleted by mistake**.

## Non-goals

- No tuning of the network timeout or transfer options. When the link is healthy, timeouts are not the bottleneck; when it is genuinely down, a longer timeout achieves nothing — the correct response is to suspend and wait for a human.
- No reuse of temp files compressed before the crash. Compression is cheap relative to upload, and reusing them would need another class of record plus an integrity check. Since they are not reused, they are garbage and should be deleted.

## The status model

This is the easiest place in the whole design to get wrong, so it is settled first.

`RunStatus` is shared by four runners, `== RunStatus.Running` is checked in fifteen places on the backend, and the frontend polls in a `while (run.status === 'Running')` loop.

**Adding a `Paused` value to it would be wrong.** Missing any one of those sites has ugly consequences: the fifteen backend checks would conclude "not running", so the busy lock and the dispatcher's skip logic both fail and a scheduled task starts a second run on top; the frontend loop would simply `break`, freezing the UI on its last frame.

Therefore:

### Pausing is a sub-state of `Running`, not a new status

`BackupRunState` gains a `Pause?` field (reason, consecutive failure count, next automatic retry time). `Status` stays `Running`.

That is also semantically correct: it really is still running — the self-healing retry is running — it just is not progressing. **None of the fifteen checks change**, so the busy lock, the staging lease and the dispatcher's skip are all correct by construction, with no possibility of a missed site.

### `Suspended` is a new terminal status shared by three reasons

```csharp
public enum RunStatus
{
    Running, Completed, Failed, Canceled,
    /// A journal is waiting to be resumed; no active run exists in the process.
    Suspended,
}

public enum SuspendReason { UserRequested, AutoSuspended, ShuttingDown }
```

Deliberate pause, automatic degradation and planned shutdown **collapse into one status**: the resume path, the cleanup criterion and the UI buttons are identical, and splitting them into three would turn those fifteen checks from considering five cases into considering seven. The UI distinguishes them by reason:

- `Suspended — 3,412 items uploaded, paused by you`
- `Suspended — 3,412 items uploaded, network unreachable for 10 min`
- `Suspended — 3,412 items uploaded, interrupted by shutdown`

> **Implementation note**: the third value ended up as `ShuttingDown`. The draft's `Crashed` was **deliberately not implemented**. They are not the same thing: during shutdown, code is still running and can write down its own reason; during a crash nothing is running and nobody can. Fabricating a `Suspended` record for a crash — one with no control object and no busy lock — would cost every branch that later encounters it an extra "remember, this one is fake". Crash leftovers therefore **never enter in-memory state**; they are read straight from the journal directory by `GET /{id}/interrupted`.

`Suspended` is terminal, so the existing `== Running` checks treat it as "not running" — which is also correct.

### Statuses and buttons

| Status | Meaning | Resources | Buttons |
|---|---|---|---|
| `Running` | Running normally | Holds lease and busy lock | `Suspend` `Cancel` |
| `Running` + `Pause` | Hit a network wall, self-healing | Holds lease and busy lock | `Retry now` `Suspend` `Cancel` |
| `Running` + `Suspending` | Winding down, waiting for in-flight uploads | Holds lease and busy lock | (none; shows waiting) |
| `Running` + `Canceling` | Winding down, see "Cancelling" | Holds lease and busy lock | (none; shows waiting) |
| `Suspended` | Resumable | All released | `Resume` `Discard` |

`Suspend` preserves the scene, `Cancel` writes off the run — which also closes the "stopping loses everything" gap. Both transitional sub-states keep `Status == Running`: the resources have not been handed back, so everything should still consider it busy.

## The pause gate

### Error classification

Only network and cloud transient errors pause. Wrong passwords, 7z crashes, a full disk and misconfiguration still terminate — those give the same error however many times a human clicks.

```
IsPausable(ex, ct) =
    RequestFailedException (status 0 / 5xx / 408 / 429)
  | IOException | SocketException | TimeoutException
  | OperationCanceledException where !ct.IsCancellationRequested
  | AggregateException where every inner IsPausable      ← today's case
```

The third line's `ct` predicate is mandatory, and **getting it wrong disables the cancel button**: the SDK's network timeout throws `TaskCanceledException` (a subclass of `OperationCanceledException`), and a user pressing `Cancel` throws the same base class. The only reliable distinction is asking whether this run's cancellation token was triggered — untriggered means a network timeout, triggered means the user wants to stop.

The same predicate replaces `IsTransient`, which is the root-cause fix: once `AggregateException` is recognised, our own backoff actually runs.

**The two layers are in series**: local backoff absorbs the error first, and only once it is exhausted does the gate take over.

### The gate

A per-run singleton on the run state. An upload task that hits the wall waits on the gate; tasks that have not hit it carry on finishing their work — if the network merely blipped they will complete normally, and there is no reason to drag them down.

So pausing is **partial**: one item may be stopped at the gate while five are still transferring. The UI convention is "show Paused whenever anything is waiting at the gate", while still listing the transfers that are running. The `Pause` field carries the number of tasks waiting, so "3 paused, 2 still transferring" can be expressed truthfully.

Three things open the gate:

1. The self-healing retry timer (30 s → 1 m → 5 m, then every 5 m)
2. The user pressing `Retry now`
3. The user pressing `Cancel` (opened into a cancellation)

That ladder belongs to **the gate itself** and is unrelated to the configured retry backoff, and it deliberately does not reuse the retry options — the two layers manage different things. That layer backs off **a single HTTP call**, on the order of seconds; this one waits for **a link to come back**, on the order of minutes, and every extra minute occupies a minute of somebody else's global staging quota. It is hard-coded in the gate (as is the ten-minute patience threshold), with constructor parameters only so tests can inject millisecond values. It is not configurable in production.

A notification fires when pausing, so that an unattended deployment tells somebody.

### Pausing takes other backups hostage, so it must be time-limited

The gate does not release the staging lease or the busy lock — the scene and the ledger stay exactly as they are, so resuming does not have to queue for quota again. But that has a consequence that **must** be solved.

The first staging gate is global:

```csharp
private bool HasRoom(StagingLease? lease) =>
    Interlocked.Read(ref _stagedBytes) < stagedLimit()      // ← global, not per lease
    && (lease is null || lease.Bytes < QuotaFor(lease));
```

A paused run's already-compressed, not-yet-uploaded output **still counts towards `_stagedBytes`**. Once that fills the global limit (2 GB by default), another backup's `HasRoom` is permanently false and it blocks waiting for a release signal — and that signal is only raised when a volume finishes uploading or a reservation is disposed. A paused run uploads nothing, so **the signal never comes**.

It is not a permanent deadlock (everything frees up when the network returns), but it amounts to this: a backup with nothing whatsoever to do with the failure — possibly a different account on a different network path — is held hostage until the failure clears. The second gate (fair share between leases) only makes it slow; the first one stops it dead.

**The answer: pausing has a patience threshold and degrades to `Suspended` automatically.**

```
hit the wall → pause, keep the scene, self-heal (30 s → 1 m → 5 m → every 5 m)
             → still unreachable past the threshold (10 minutes by default)
             → take the Suspend wind-down path: clear staged, release the lease and
               busy lock, flush the journal, end the task, reason = AutoSuspended
```

A brief blip costs nothing (the vast majority self-heal within tens of seconds, the scene is intact, and others wait only those seconds); a long failure hands back every resource so other backups are released immediately, while this run loses not one item of progress and can be resumed at any time.

This reuses the already-defined Suspend path and introduces no new mechanism. Automatic degradation notifies as well.

## The journal

### Location and format

`data/journal/{accountId}/{container}/{runId}.jsonl`, append-only text, **not in SQLite**.

> **Implementation note**: the directory is keyed by `(accountId, container)` rather than the draft's `configId` — the cleaner locates containers by exactly those two and never has a `configId` in hand. The `configId` is recorded in the journal header and read from there when needed. The suspend marker is a sibling file with the same name plus one extra suffix, and enumeration only accepts `*.jsonl`, so it can never be mistaken for a journal.

The precedent is this project's own: verbose logs go to text files partitioned by backup and by date rather than SQLite, to keep one DB write per file from becoming the bottleneck of a very large backup. The journal has the same shape — one line per item, hundreds of thousands of items. `data/` is where `app.db` lives and is already a persistent volume.

The first line is a header: run id, config id, start time, baseline version number, local root, encryption identity. Every confirmed item then appends a line: path, ref, kind, length, full hash.

### Write ordering

**The direction cannot be reversed:**

```
compress → upload → upload confirmed → only then append the journal line
                                     ↑ never one step earlier
```

`UploadIfMissingAsync` returning `false` ("it is already in the cloud") **must also be recorded** — that is equally a confirmation that it is there.

### No fsync

Deliberate. The costs of the two failure directions are completely asymmetric:

| At crash time the journal | Consequence |
|---|---|
| Missed a few lines | Those items are recompressed and re-uploaded. Wasteful, but **correct** |
| Recorded one line too many | The index points at a blob that does not exist or is incomplete. **Silent data loss** |

Not calling fsync can only produce the former. The real risk is in the ordering, not in write latency, and hundreds of thousands of fsyncs buy no safety at all. Deliberate suspension is the one exception — on the graceful path it fsyncs once, which costs nothing.

### Preconditions

Before resuming, each is checked and any mismatch voids the whole journal:

- The local root changed
- The baseline version number changed (another backup committed a new version in between)
- The encryption identity or password changed

The baseline check pairs with "blocks protected by an active journal": a protected block implies the baseline should not have changed, so if it did, the protection failed and this is not something to gamble on.

Changed compression settings do **not** void it: single-file blobs are content-addressed on plaintext and unaffected, packs already sealed are unaffected, and the remaining files were going to be re-packed anyway.

## Resuming

### At startup

The journal directory is scanned, and unfinished journals **do not enter in-memory state**: `GET /{id}/interrupted` reads them on demand (peeking only reads the header line and counts the rest — a journal abandoned mid-run may hold hundreds of thousands of records, and this is the startup path). The UI lists them for a human to act on; `DELETE` on the same path discards.

> **Implementation note**: the draft said "register a `Suspended (reason = Crashed)` state". The reasoning for dropping that is in the `SuspendReason` note above — it would be a fake record with no control object and no busy lock. **The constraint that no task starts and no busy lock is taken has not changed; what changed is not having to fabricate a status for it.**

Pressing Run manually while a valid journal is on disk is equivalent to resuming (it does not start over). That is also why **there is no separate resume endpoint**: resuming is not a mode — every run recognises any still-valid journal when it opens one, and the button says `Resume` only because that is what it means to the user.

### The resume itself

It is a normal run with one extra parameter. Scanning and diffing run as usual (they are local and fast); the only difference is one lookup before packing:

```
diff produces (path, length, fullHash)
  → journal hit (path AND fullHash both match)?
     hit   → fill in the storage reference directly; no compression, no upload
     miss  → normal handling; whatever is left gets packed into new packs
```

The criterion must be **path and fullHash together**, never path alone — the file may have been modified after the crash. The full hash is computed during the diff anyway, so this costs nothing.

The pack ids' per-run random prefix is an advantage here: newly computed packs can never collide with already-uploaded ones.

Resuming **keeps appending to the same journal** rather than starting a new one, so a second crash can be resumed too.

### Lifecycle

The info file commits successfully → the journal is deleted → cleanup runs.

## Cleanup

### One criterion

> Delete when a block is referenced by no retained version **and** by no active journal.

The retention cleaner's referenced-blob and referenced-pack sets each take in one "active journal references" set. That is the entire change.

**There is no "look up a journal and delete what it references" operation.** Discarding or voiding a journal only removes it from the active set, after which a **normal** cleanup runs.

This formulation dodges one boundary: crashing between "the info file is committed" and "the journal is deleted" means startup finds a changed baseline version and voids the journal — while the blocks it references are **already referenced by the new version's index**. Cleaning by reverse lookup would delete data in active use. The unified criterion cannot have that problem.

### When it runs

- On backup completion: the existing cleanup step covers it
- On discard: run one explicitly
- On voiding (a failed precondition check): run one explicitly
- **Not on `Cancel`** — see the cancelling section
- On deleting a backup configuration: see the end of that section

### Staging directories

`{tempPath}/compress` and `{tempPath}/staged` are emptied at startup.

The criterion is unambiguous by construction: the process has just started, so by definition no run is alive, everything there is left over from the previous process, and leftovers are not reused. This also fixes the pre-existing directory leak. Staged files during a run are still protected by in-memory objects and are unrelated to this.

## Deliberate suspension

**Deliberate suspension is a graceful crash recovery.** The resume path, the cleanup criterion and the UI buttons are identical to the crash case; only the way it is reached is clean:

```
press Suspend
  → stop taking new work from the diff queue
  → let in-flight uploads finish on their own (no forced kill)
  → fsync the journal
  → end the task: release the staging lease and busy lock, clear staged temp files
  → settle as Suspended (reason = UserRequested)
```

**In-flight uploads must be allowed to return and must not be killed.** Aborting midway lands in "it may or may not have got there", and the journal only records confirmed returns. Letting it return on its own — recording on success, not recording on failure — is the only clean boundary.

The cost is that suspension is not instantaneous: with a large file in flight it can take minutes. That is the `Suspending` sub-state, where `Status` **is still `Running`** because the resources have not been handed back. The UI shows `Suspending… (waiting for 2 uploads to finish)`.

`Suspend` also works while automatically paused, taking the same wind-down path.

## Cancelling

`Cancel` differs from `Suspend` in **whether this run still counts**: `Suspend` settles as `Suspended` (with `Resume` / `Discard`), `Cancel` settles as `Canceled` (back to an ordinary `Run`). Both leave already-uploaded blocks in the cloud (below).

> **Implementation note**: the draft said `Cancel` deletes the journal; the implementation **does not**. Both stop kinds flush it with fsync. The reasoning is the conclusion of "keep what is complete" below: having decided to leave completed blocks in the cloud for the next run to reuse, there is no reason to burn the ledger recording *which* blocks are complete. Delete it and the next run has to rediscover them one `If-None-Match` at a time — and packs are unrecoverable entirely, given the random prefix. So the next Run after a `Cancel` also continues where it left off; the UI just does not call it `Resume`.
>
> Only three things delete a journal: this run committing its index successfully, the user pressing `Discard`, and deleting the backup configuration.

### Returning only once the wind-down is genuinely complete

Cancelling used to call `Cancellation.Cancel()` and return a `bool` immediately, leaving the caller with no idea whether the run had actually stopped. It is now asynchronous: the endpoint returns only after the journal is flushed, temp files are cleared, and the lease and busy lock are released. In between it is the `Canceling` sub-state and the UI shows `Canceling…`.

Same reasoning as `Suspend`: if the endpoint reports success while the busy lock is still held, the user's next action — editing the configuration, deleting it, running again — collides with a run that is not dead yet.

### Two ways to stop, asked at the moment of cancelling

| Option | Behaviour |
|---|---|
| `Stop now` | The cancellation token propagates all the way, killing in-flight uploads. Stops fastest |
| `Finish current files` | Lets in-flight files finish, **including all their volumes**, then stops |

The second matters most for large multi-volume files: a 50 GB file killed at volume 19 wastes all 19, and an encrypted multi-volume archive has its leftovers deleted and re-uploaded next time anyway.

`Suspend` has no such choice — by definition it *is* `Finish current files`. So `Stop now` is the only path that ever kills an in-flight upload.

### Keep what is complete, delete what is not

The criterion is **the journal**: it records only confirmed returns, so what is in the journal is complete and what is in flight is not.

**Completed uploads stay.** A single-file blob is content-addressed at `data/{fullHash}`, so the next backup reaching the same file hits `If-None-Match` directly and **not one byte of this run is wasted**; once reused it is referenced by the new version's index and is no longer an orphan. Deleting them on `Cancel` would make `Finish current files` pointless — finishing would only mean deleting. The two decisions have to agree, and this picks the side where the bytes have value.

**Incomplete uploads are deleted immediately.** When `Stop now` kills an in-flight upload, that file may have landed some of its volumes: 19 out of 20 is **unopenable**. The wind-down enumerates and deletes that blob reference's own volumes, reusing the volume-membership predicate from leftover clearing — which only recognises this archive's own volumes and will not touch a collision-avoidance sibling `data/{hash}~1`, that being different content referenced by a different index entry, where a mistaken delete is real data loss.

So the two stop kinds wind down differently:

| Option | Staged, not uploaded | Fully uploaded | The in-flight file |
|---|---|---|---|
| `Stop now` | Deleted | Kept | **All its leftover volumes deleted** |
| `Finish current files` | Deleted | Kept | Allowed to finish, so also complete, kept |

Strictly speaking, per-volume if-missing would fill in the gaps by itself (recompression produces byte-identical volumes in the unencrypted case, which has been measured), so leftover volumes would not cause an error. The value of deleting them immediately is elsewhere: **not having to wait until the next backup to reclaim them** — and `Stop now` means stopping cleanly.

Packs do not get the "keep it for reuse" treatment: the random prefix guarantees the next run makes new packs, so old ones are permanently orphans and get cleaned up by the next backup's cleanup step.

**The cost**: if this backup is never run again, those blocks keep costing cloud storage. Hence —

### A backstop when deleting a backup configuration

Blocks left by `Cancel` are reclaimed by "the next backup", and deleting the configuration means there will not be one. The delete path adds a backstop:

- `deleteContainer = true`: the container goes, orphans with it, nothing more needed
- `deleteContainer = false`: the cloud data is kept for a later import, but those orphans are pure garbage — run an orphan cleanup before deleting (the journal has already been removed from the active set, so the unified criterion still applies) and delete all of that configuration's journals

Deleting a configuration in the `Suspended` state takes the same path: nobody is going to resume it, so the journal and the blocks it protects go together.

## Graceful shutdown and automatic resume

> Added after the design was signed off. Everything above solves "progress survives an interruption", but a planned restart — `docker stop`, upgrading the image — still required a human to come back and press a button. This removes that one press, and **only** that one.

### Shutdown: `SIGTERM` suspends on the way out

When the host receives a stop signal, every `Running` run takes the existing Suspend wind-down path with `reason = ShuttingDown`, fsyncs its journal, and writes a **marker file beside the journal** recording that reason.

The marker is a sibling file rather than a record inside the journal because the cleanup criterion sorts records into a "blob" or "pack" bucket by kind, and an unexpected third kind would silently land in the blob bucket.

Three timeouts form **one chain**, and changing any one means revisiting the other two:

```
docker stop_grace_period 45s  >  HostOptions.ShutdownTimeout 30s  >  waiting for runs to flush 20s
```

- `45 > 30`: docker's grace period ending means `SIGKILL`. .NET's own timeout has to fire first for there to be any chance of writing the logs out.
- `30 > 20`: the host's own `StopAsync` timeout, once exceeded, stops waiting and tears services down — at which point nobody even records *which* run failed to stop.

**This wait must be bounded, and what it cannot do must be stated honestly.** A suspend deliberately does not touch the abort token, and the consumer loop only exits before starting the **next** item, so whatever is in hand — possibly a multi-gigabyte upload — is left to finish. A run that has not flushed by the deadline is abandoned mid-way, and at the next start it is an interruption **with no marker**, requiring a manual Resume. That is the deliberate trade: abandoning one in-flight file costs re-uploading it, while waiting past the host's grace period costs `SIGKILL`, which really does lose the whole run.

Issuing the stop and waiting for flushes **must be two passes**: send the intent to every run first, then wait for all of them. Combining them (send one, wait for one) is fatal with concurrent backups — if the first has a multi-gigabyte upload in hand it consumes the entire shutdown budget by itself, and the runs behind it never even receive the stop request.

### Startup: only the shutdown marker counts

Automatic resume starts about 15 seconds after launch (letting the web port come up and the scheduler take its first tick), is controlled by a global setting (on by default), and starts runs **one at a time, waiting for each to finish**: the production lock is global, so rushing them all in only makes them queue against each other — and concurrent backups are measurably slower in this repository.

There is one criterion: the configuration has **at least one** journal left, and **every** journal beside it carries a `ShuttingDown` marker.

| What is on disk | Resume automatically? | Why |
|---|---|---|
| `ShuttingDown` | **Yes** | The only case that is "an interruption this process caused, with the scene intact" |
| `UserRequested` | No | Restarting it for them erases the intent behind that press |
| `AutoSuspended` | No | The transient error is probably still there, and resuming immediately just hits the same wall |
| No marker | No | Cannot be explained |

That last row is the centre of gravity of this criterion: **an absent marker does not mean "the process was killed"**. A crash, a power cut, a run abandoned when the shutdown flush timed out, an operator pressing Cancel (both cancel kinds flush and deliberately write no marker), or even the marker write itself failing — all look identical on disk. At least one of those (Cancel) is a user explicitly saying "stop", so the entire category is left alone.

Requiring **every** journal to qualify, rather than "the latest one wins": markers are per journal, and one configuration can genuinely end up with journals that disagree (suspend by hand → press Run again → the new run adopts the old journal → a shutdown stops the new run as `ShuttingDown`), while a resuming run adopts every still-valid journal at once. Requiring unanimity removes the need to invent an arbitration rule for "which journal is newer".

Every rejected configuration **gets a log line** naming which journal and which marker. That is not ceremony: this is a NAS appliance where the operator has neither a shell nor any tool for inspecting marker files. Without it, "why didn't my backup continue after the restart?" leaves them with nothing at all to go on — the toggle in the UI is on, the log says nothing, and the actual reason exists only on disk.

## Pinned behaviour

Safety first; each of these has the shape of "get it wrong and lose data":

- Journal ordering: an upload that throws leaves **no** record line; `UploadIfMissingAsync` returning `false` **does** leave one.
- The resume criterion: same path, different full hash → no hit.
- The cleanup criterion: blocks referenced by an active journal are not deleted; after voiding, the normal criterion applies; and **the "info committed, journal not yet deleted" boundary — where blocks are already referenced by the new version and a voiding cleanup must not touch them**.
- The status model: the scheduler does not start a second run during `Suspending`, `Canceling` or `Suspended`.
- **Degradation releases the hostage**: A pauses holding the full staging limit → B blocks waiting for room → A times out and degrades → B unfreezes and completes. A concurrency timing test, and the reason that whole section exists.
- `Stop now` deletes the in-flight file's leftover volumes and **does not touch** the collision-avoidance sibling `data/{hash}~1`.
- Token discrimination: a user pressing `Cancel` is not misread as a network error and paused.
- **The automatic-resume criterion**: each of the four on-disk shapes resumes or does not as documented; disagreeing markers under one configuration do **not** resume; and turning the setting off genuinely stops it — the failure mode of that last one is **silent** (the UI still saves happily, right up until a backup the user did not want starts itself after some future restart).

Azurite must be running, or the related integration tests skip silently.
