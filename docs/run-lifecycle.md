# Run lifecycle: pause, suspend, stop and resume

A backup can run for over a day. This document covers everything that interrupts one and everything
that picks it back up: the four ways a run can be held or ended, the journal that makes progress
survive, and the automatic resume after a restart.

## The status model

`RunStatus` is shared by four runners, `== RunStatus.Running` is checked in fifteen places on the
backend, and the frontend polls in a `while (run.status === 'Running')` loop. That shapes the model:

```csharp
public enum RunStatus { Running, Completed, Failed, Canceled, Suspended, Skipped }
public enum SuspendReason { UserRequested, AutoSuspended, ShuttingDown }
```

**Holding is a sub-state of `Running`, not a status.** `BackupRunState` carries a `Pause?` field
(reason, consecutive failures, next retry time) while `Status` stays `Running`.

> **Rationale.** Adding a `Paused` value would mean auditing fifteen backend checks and one frontend
> loop. Missing any one of them has ugly consequences: a check concluding "not running" makes the
> busy lock and the dispatcher's skip both fail, so a scheduled task starts a second run on top; the
> frontend loop would simply `break`, freezing the UI on its last frame. It is also semantically
> correct — the run really is still running, it just is not progressing.

**`Suspended` is one terminal status for three reasons.** Deliberate suspension, automatic
degradation and planned shutdown collapse into it: the resume path, the cleanup criterion and the UI
buttons are identical, and splitting them would turn those fifteen checks from five cases into seven.
The UI distinguishes them by reason.

> **Rationale — why there is no `Crashed` reason.** During shutdown, code is still running and can
> write down its own reason; during a crash nothing is running and nobody can. Fabricating a
> `Suspended` record for a crash — one with no control object and no busy lock — would cost every
> branch that later encounters it an extra "remember, this one is fake". Crash leftovers therefore
> never enter in-memory state; they are read straight from the journal directory by
> `GET /{id}/interrupted`.

| Status | Meaning | Resources | Buttons |
|---|---|---|---|
| `Running` | running normally | lease + busy lock | `Pause` `Suspend` `Cancel` |
| `Running` + user pause | held by the operator | lease + busy lock | `Resume` `Suspend` `Stop` |
| `Running` + error pause | hit a network wall, self-healing | lease + busy lock | `Retry now` `Suspend` `Cancel` |
| `Running` + `Suspending` | winding down, waiting for in-flight uploads | lease + busy lock | (none) |
| `Running` + `Canceling` | winding down | lease + busy lock | (none) |
| `Suspended` | resumable | all released | `Resume` `Discard` |
| `Skipped` | never started: the sentinel was not there | never taken | `Run` |

Both transitional sub-states keep `Status == Running` because the resources have not been handed
back, so everything should still consider the target busy.

**`Skipped` is its own status because it is neither of the two it resembles.** A backup whose
sentinel path is absent never starts — see
[configuration.md](configuration.md#sentinel-path-refusing-to-run-on-an-unmounted-source). `Failed`
would write an `Error` the operator has to clear by hand, and a NAS that is simply not mounted
overnight would wear a red badge every morning until the alarm stopped being read. `Canceled` says a
person changed their mind, and nobody did. What happened is that a precondition was not met, so
nothing was attempted and **nothing new is known about this backup** — which is why the persisted
status is left exactly as it was, in both directions. Writing `Normal` would be the worse of the two:
it would wipe a genuine earlier failure off a backup that has not run since.

> **The check every caller has to make.** `TaskDispatcher` writes `Normal` after any dispatch that
> did not throw, so a status it has not been taught about is silently treated as a success. Adding
> `Skipped` therefore meant adding a fourth early return beside `Canceled` and `Suspended`. Anything
> else that concludes "not `Failed`, so it worked" needs the same treatment.

## The pause gate

One gate, two independent reasons to be closed. A worker never has to ask *why* it is parked.

```csharp
public enum PauseSource { TransientError = 0, User = 1 }

public void PauseByUser();                              // no timer, no patience, never downgrades
public void ResumeByUser();                             // clears only the user's hold
public Task WaitIfPausedAsync(CancellationToken ct);    // pass through when open, park when closed
```

The gate is closed while **either** reason holds. A resume that finds the error reason still standing
leaves it closed and keeps reporting the transient error, which is correct — the run is not ready to
proceed.

### Error classification

Only network and cloud transient errors pause. Wrong passwords, 7z crashes, a full disk and
misconfiguration still terminate — those give the same error however many times a human clicks.

```
IsPausable(ex, ct) =
    RequestFailedException (status 0 / 5xx / 408 / 429)
  | IOException | SocketException | TimeoutException
  | OperationCanceledException where !ct.IsCancellationRequested
  | AggregateException where every inner IsPausable
```

> **Rationale — the `ct` predicate is mandatory, and getting it wrong disables the cancel button.**
> The SDK's network timeout throws `TaskCanceledException`, a subclass of `OperationCanceledException`
> — and a user pressing Cancel throws the same base class. The only reliable distinction is asking
> whether this run's own cancellation token was triggered: untriggered means a network timeout,
> triggered means the user wants to stop.

> **Rationale — why `AggregateException` is in the list.** It is the shape this whole mechanism was
> built for. The blob client options never configured `Retry`, so the SDK defaults applied
> (`MaxRetries = 5`, 100-second network timeout); after six timeouts Azure.Core threw an
> `AggregateException` whose inner exceptions are all `TaskCanceledException`. The original
> `IsTransient` recognised only `RequestFailedException` and `IOException`, so this shape fell through
> to `false` and **the configured exponential backoff ran zero times**. One network blip destroyed a
> run that had been uploading for hours.

The two layers are in **series**: the configured local backoff absorbs the error first, and only once
it is exhausted does the gate take over.

### The self-healing ladder

Three things open the gate on the transient-error reason: its own retry timer (30 s → 1 m → 5 m, then
every 5 m), the operator pressing `Retry now`, and a cancellation.

> **Rationale — why this ladder is not the configured retry backoff.** The two layers manage
> different things. That one backs off **a single HTTP call**, on the order of seconds; this one
> waits for **a link to come back**, on the order of minutes — and every extra minute occupies a
> minute of somebody else's global staging quota. It is hard-coded, with constructor parameters only
> so tests can inject millisecond values.

A notification fires when pausing, so an unattended deployment tells somebody.

### Patience, and why a transient pause must be time-limited

The gate does not release the staging lease or the busy lock, so the scene stays exactly as it is and
resuming does not queue for quota again. But a paused run's already-compressed, not-yet-uploaded
output still counts towards the **process-wide** `_stagedBytes`.

> **Rationale.** Once that fills the global limit, another backup's `HasRoom` is permanently false
> and it blocks waiting for a release signal — which is only raised when a volume finishes uploading
> or a reservation is disposed. A paused run uploads nothing, so **the signal never comes**. It is
> not a permanent deadlock, but a backup with nothing whatsoever to do with the failure — possibly a
> different account on a different network path — is held hostage until the failure clears.

So the transient-error reason has a patience threshold (10 minutes by default) and **degrades to
`Suspended` automatically**, taking the ordinary suspend wind-down: clear staged, release the lease
and busy lock, flush the journal, reason `AutoSuspended`. A brief blip costs nothing; a long failure
hands back every resource while this run loses not one item of progress.

**The user's hold has no patience and never downgrades.** The clock belongs to the transient-error
reason alone, and a user hold stops it.

> **Rationale.** Patience means "the run kept retrying and nothing ever recovered". While the hold
> stands no worker is permitted to retry anything, so the clock would be reading an operator's coffee
> break as a network that never came back. Two consequences follow: a failure landing during a pause
> parks its worker and starts its backoff but can never downgrade the run; and `ResumeByUser` clears
> the clock and the failure count, exactly as `Retry now` does, so the retry the operator has just
> authorised gets a full patience window rather than finding the budget already spent.

There is deliberately **no timeout on a user pause**. An automatic downgrade would turn a pause into
a suspend exactly when the operator is not watching. The cost is stated on screen instead —
`Paused 23 minutes ago · holding 4.2 GB of staging` — and the operator decides.

### Four gates, at the top of each loop

`WithPauseAsync` wraps an item's *failure* path. Pausing needs the *entry* path, so each producing
loop takes the gate at the top: the diff's change handler, the prober, the compressor and the
uploader.

The diff is included because "pause the backup" means the disk stops being read at all, not merely
that the pipeline stops draining.

Granularity is **finish the item in hand, then hold** — worst case, the time to compress one large
file. Aborting mid-item and re-queueing would need 7z killed, partial output deleted and the item
recompressed from scratch next time, trading a real recompression for a few seconds of
responsiveness.

> **One gate means these four also park on a transient-error pause**, which changes how the pipeline
> behaves during a network blip and not only during a deliberate pause. Before, an upload that hit
> trouble parked only itself while the compressor went on filling the staging pool and the diff went
> on reading the disk. It is mostly an improvement — compressing more output that cannot be uploaded
> only fills a process-wide pool — but it is stated here because it is invisible in the code, and
> because it is real: measured, a test that assembles its scene by letting the compressor fill the
> pool while both uploaders sit out a backoff no longer does so.

**A run that fails while paused has to be able to end.** Parking four loops on a gate only the
operator can open means a failure raised *outside* those loops — the run's own tail — would otherwise
wait forever for workers nobody is going to release, holding the busy lock and the process-wide quota
until the container restarts. So the orchestrator's teardown downgrades the gate before it rethrows.
Two consequences follow from that being unconditional: the gate reads as downgraded after any failed
run, and a worker parked mid-backoff at that moment is released rather than retrying until patience
expires — a little journalled progress traded for a teardown that terminates.

## Stopping: which stages' work is worth finishing

"Stop" is one word for work whose value differs by stage.

| Stage | In-flight work at stop time | Worth finishing? |
|---|---|---|
| prober | a content identity, persisted nowhere | **No** — the next resume computes it again |
| compressor | an archive that the drain will release anyway | **No** |
| uploader | an upload that, once complete, is journalled | **Yes** — the next run skips it |

So the two feeding stages are linked to `StopToken`, which fires for **every** stop kind, while the
uploaders run on the working token. Suspend and "finish current files" therefore cancel the prober
and the compressor immediately and let the uploaders finish the volume in hand. `StopNow` fires the
abort token instead, cancelling everything including uploads.

> **Two cleanup corrections this exposed.** A cancelled 7z leaves `name.7z.001.tmp` — it renames each
> volume only once complete — while the existing cleanup matched only *finished* volumes, so the file
> a cancellation actually produces was precisely the one it could not see. And the pack path had no
> cleanup for cancellation at all: it cleaned up only on the "7z dropped a member" exit code, so a
> cancelled pack compression left its volumes behind. Both were unreachable in practice while
> interrupting a compression required `StopNow`; the staged stop makes them ordinary.

### Suspend

```
press Suspend
  → stop taking new work; cancel the feeding stages
  → let in-flight uploads finish on their own (never killed)
  → fsync the journal
  → release the staging lease and busy lock, clear staged temp files
  → settle as Suspended (reason = UserRequested)
```

**In-flight uploads must be allowed to return.** Aborting midway lands in "it may or may not have got
there", and the journal only records confirmed returns. Letting it return — recording on success, not
recording on failure — is the only clean boundary.

The cost is that suspension is not instantaneous: with a large file in flight it can take minutes.
That is the `Suspending` sub-state.

### Cancel

`Cancel` differs from `Suspend` in **whether this run still counts**: `Suspend` settles as
`Suspended` with Resume/Discard, `Cancel` settles as `Canceled` and the backup goes back to an
ordinary Run button. Both leave already-uploaded blocks in the cloud.

Two kinds, asked at the moment of cancelling:

| Option | Behaviour |
|---|---|
| `Stop now` | the abort token propagates, killing in-flight uploads. Fastest |
| `Finish current files` | in-flight files finish, **including all their volumes**, then stop |

> **Rationale.** The second matters most for large multi-volume files whose volumes cannot be
> reused: an encrypted archive never label-matches across runs (fresh random salt/IV every
> compression — see [volume-identity.md](volume-identity.md)), so a 50 GB encrypted file killed at
> volume 19 wastes all 19 — the next attempt overwrites them. An unencrypted family loses less:
> its landed volumes carry identity labels, and the next attempt verifies them in place and skips
> them — though the recompression is paid in full either way. `Suspend` has no such choice — by
> definition it *is* "finish current files" — so `Stop now` is the only path that ever kills an
> in-flight upload.

### The kinds form a ladder, and a wind-down can be escalated

```
None (0)  <  Suspend (1)  <  FinishCurrentFiles (2)  <  StopNow (3)
```

`BackupRunControl.RequestStop` is a CAS loop that only ever moves the value **up** — a request weaker
than or equal to the standing one returns without doing anything, a stronger one takes effect. So a
second stop is not a race with an unpredictable winner; the strongest one asked for is the one that
happens, whenever it arrives.

This is not an incidental property, it is the escape hatch. `Suspend` and `Finish current files` both
wait for the file in hand and every volume of it, with the run still reporting itself as `Running`
throughout — minutes on a slow uplink, and the operator often only learns *what* it is waiting on
after pressing. Escalating to `Stop now` is the one correct move at that point, and per the table
above it is also the only one that interrupts the transfer already on the wire.

The UI therefore keeps **Stop** live while anything weaker winds down, and quiet only once `Stop now`
itself is the standing request. The other controls do go disabled, for a reason that does not apply to
Stop: `Retry now`, `Resume` and `Pause` all act on the pause gate, and `RequestStop` **downgrades**
that gate, after which it can never hold anyone again — so those buttons could not do what their
labels say however long the wind-down lasted. `Suspend` is disabled as a step back down the ladder,
which would be ignored. See `frontend/src/lib/windDownControls.ts`.

**Cancel does not delete the journal.** Having decided to leave completed blocks in the cloud for the
next run to reuse, there is no reason to burn the ledger recording *which* blocks are complete —
delete it and the next run has to rediscover them one request at a time, and packs are unrecoverable
entirely given their random prefix. So the next Run after a Cancel also continues where it left off;
the UI just does not call it Resume.

Only three things delete a journal: the run committing its index successfully, the user pressing
`Discard`, and deleting the backup configuration.

### Keep what is complete, delete what is not

The criterion is **the journal**: it records only confirmed returns, so what is in it is complete and
what is in flight is not.

| | Staged, not uploaded | Fully uploaded | The in-flight file |
|---|---|---|---|
| `Stop now` | deleted | kept | **all its leftover volumes deleted** |
| `Finish current files` | deleted | kept | allowed to finish, so kept |

**Completed uploads stay.** A single-file blob is content-addressed, so the next backup reaching the
same file hits if-missing directly and not one byte of this run is wasted. Deleting them on Cancel
would make "finish current files" pointless — finishing would only mean deleting.

**Incomplete uploads are deleted immediately.** A file killed mid-family may have landed some of its
volumes, and 19 out of 20 is unopenable. The wind-down enumerates and deletes that reference's own
volumes, using the volume-membership predicate that recognises only this archive's own volumes and
will **not** touch a collision-avoidance sibling `data/{hash}~1` — different content, referenced by a
different index entry, where a mistaken delete is real data loss.

> Strictly, per-volume if-missing would fill the gaps by itself (recompression produces byte-identical
> volumes in the unencrypted case, which has been measured). The value of deleting immediately is not
> having to wait until the next backup to reclaim the space.

Packs get no "keep it for reuse" treatment: the random prefix guarantees the next run makes new ones,
so old ones are permanently orphans and the next backup's cleanup collects them.

**A backstop when deleting a configuration.** Blocks left by a Cancel are reclaimed by "the next
backup", and deleting the configuration means there will not be one. So the delete path runs an
orphan cleanup first when the container is being kept, and deletes all of that configuration's
journals.

## The journal

`data/journal/{accountId}/{container}/{runId}.jsonl`, append-only text, **not in SQLite**.

> **Rationale.** The precedent is this project's own: verbose logs go to text files partitioned by
> backup and date rather than SQLite, to keep one DB write per file from becoming the bottleneck of a
> very large backup. The journal has the same shape — one line per item, hundreds of thousands of
> items.

The first line is a header (run id, config id, start time, baseline version, local root, encryption
identity). Every confirmed item then appends a line: path, ref, kind, length, full hash, head and
tail hashes, volume count and sizes, and the source mtime.

### Write ordering

```
compress → upload → upload confirmed → only then append the journal line
                                     ↑ never one step earlier
```

An if-missing upload returning "it is already there" **must also be recorded** — that is equally a
confirmation.

### No fsync

Deliberate, because the two failure directions are completely asymmetric:

| At crash time the journal | Consequence |
|---|---|
| missed a few lines | those items are recompressed and re-uploaded. Wasteful, but **correct** |
| recorded one line too many | the index points at a blob that does not exist. **Silent data loss** |

Not calling fsync can only produce the former. The risk is in the ordering, not in write latency, and
hundreds of thousands of fsyncs buy no safety. A deliberate suspension is the one exception: on the
graceful path it fsyncs once, which costs nothing.

### Preconditions

Each is checked before resuming, and any mismatch voids the whole journal:

- the local root changed
- the baseline version number changed (another backup committed a version in between)
- the encryption identity or password changed

Changed compression settings do **not** void it: single-file blobs are content-addressed on plaintext
and unaffected, packs already sealed are unaffected, and the remaining files were going to be
re-packed anyway.

## Resuming

Unfinished journals **do not enter in-memory state** at startup. `GET /{id}/interrupted` reads them
on demand, peeking only at the header line and counting the rest — a journal abandoned mid-run may
hold hundreds of thousands of records, and this is the startup path.

**There is no separate resume endpoint.** Resuming is not a mode: every run recognises any still-valid
journal when it opens one, so pressing Run manually is equivalent. The button says `Resume` only
because that is what it means to the user.

A resume is an ordinary run. Scanning and diffing happen as usual — they are local and fast — and the
journal is consulted at three points, cheapest first:

1. **`FindUntouchedBlob(path, mtime, length)`** — no read at all. The rationale is in
   [content-identity.md](content-identity.md) § *Tier 0*.
2. **`FindBlob(path, fullHash, length, head, tail)`** — path **and** content, never path alone: the
   file may have been modified after the interruption.
3. **`FindPack(members)`** — matched on the member set.

All three tiers — and cross-version dedup behind them — refuse a hit whose ref is damage-marked
(`IsDamagedRef`): a resume must not adopt a reference to a blob a check condemned, so the item falls
through to the ordinary compress-and-upload, which heals the family in passing
([volume-identity.md](volume-identity.md)).

> **Why the cheap question was worth adding.** Measured on a real resume: 194.1 GB of source
> processed, 704.4 MB actually uploaded. Better than 99% of that read was spent proving nothing
> needed to be sent — and a resume happens after something already went wrong, with the operator
> watching a progress bar crawl through the whole dataset again before reaching the part that failed.

The journal's confirmed blocks are also fed into `LocalDedupResolver.Build`, not merely to save an
upload.

> **Rationale.** Resume accounts by **path**. Suppose the previous run finished uploading A and
> suspended before reaching B, which has the same content. This run reuses A directly, but B does not
> recognise it, so it recompresses; `ResolveAsync` then hands B the **same** address (content
> addressing), and the upload writes over A's own volumes. Deterministic output makes that mostly
> waste — the volumes label-match and are skipped — but an encrypted backup's output never matches
> (fresh salt/IV), so every volume of A's family is overwritten, and an interruption mid-family
> leaves the address a splice of two runs' volumes — unopenable, for an encrypted multi-volume
> archive — while the next run adopting the journal reuses A as usual and commits an index pointing
> at it. The error surfaces only at restore or check time. Fed in, B takes the ordinary
> cross-version dedup path and those volumes are never touched.

Resuming **keeps appending to the same journal**, so a second interruption is resumable too. Pack ids
carry a per-run random prefix and never repeat across runs, so newly computed packs cannot collide
with already-uploaded ones.

The journal is deleted once the info file commits successfully, after which cleanup runs.

## Graceful shutdown and automatic resume

### Shutdown suspends on the way out

On a stop signal, every `Running` run takes the suspend wind-down with `reason = ShuttingDown`,
fsyncs its journal, and writes a **marker file beside the journal**.

> **Rationale — a sibling file rather than a record inside the journal.** The cleanup criterion sorts
> records into a blob or pack bucket by kind, and an unexpected third kind would silently land in the
> blob bucket.

Three timeouts form **one chain**, and changing any one means revisiting the other two:

```
docker stop_grace_period 45s  >  HostOptions.ShutdownTimeout 30s  >  waiting for runs to flush 20s
```

`45 > 30` because docker's grace period ending means `SIGKILL`, and .NET's own timeout has to fire
first for there to be any chance of writing the logs out. `30 > 20` because once the host's
`StopAsync` timeout is exceeded it stops waiting and tears services down, at which point nobody even
records *which* run failed to stop.

**What this wait cannot do is stated honestly.** A suspend deliberately does not touch the abort
token, and the uploader loop only exits before starting the next item, so whatever is in hand —
possibly a multi-gigabyte upload — is left to finish. A run that has not flushed by the deadline is
abandoned mid-way and reappears at the next start as an interruption **with no marker**, requiring a
manual Resume. That is the deliberate trade: abandoning one in-flight file costs re-uploading it,
while waiting past the grace period costs `SIGKILL`, which loses the whole run.

Issuing the stop and waiting for flushes are **two passes**: send the intent to every run first, then
wait for all of them. Combining them is fatal with concurrent backups — if the first has a
multi-gigabyte upload in hand it consumes the entire shutdown budget by itself, and the runs behind
it never even receive the request.

### Startup: only the shutdown marker counts

Automatic resume starts about 15 seconds after launch, is controlled by a global setting (on by
default), and starts runs **one at a time, waiting for each to finish** — the production lock is
global, so rushing them all in only makes them queue against each other.

One criterion: the configuration has **at least one** journal left, and **every** journal beside it
carries a `ShuttingDown` marker.

| On disk | Resume automatically? | Why |
|---|---|---|
| `ShuttingDown` | **Yes** | the only case that is "an interruption this process caused, scene intact" |
| `UserRequested` | No | restarting it for them erases the intent behind that press |
| `AutoSuspended` | No | the transient error is probably still there |
| No marker | No | cannot be explained |

> **Rationale — the last row is the centre of gravity.** An absent marker does not mean "the process
> was killed". A crash, a power cut, a run abandoned when the shutdown flush timed out, an operator
> pressing Cancel (both kinds flush and deliberately write no marker), or the marker write itself
> failing — all look identical on disk. At least one of those is a user explicitly saying "stop", so
> the entire category is left alone.

Requiring **every** journal to qualify rather than "the latest one wins": markers are per journal, and
one configuration can genuinely end up with journals that disagree (suspend by hand → press Run →
the new run adopts the old journal → a shutdown stops it as `ShuttingDown`), while a resuming run
adopts every still-valid journal at once. Unanimity removes the need to invent an arbitration rule
for "which journal is newer".

Every **rejected** configuration gets a log line naming which journal and which marker.

> **Rationale.** This is a NAS appliance where the operator has neither a shell nor any tool for
> inspecting marker files. Without it, "why didn't my backup continue after the restart?" leaves them
> with nothing at all to go on — the toggle in the UI is on, the log says nothing, and the actual
> reason exists only on disk.

### A pause does not survive a restart

Pause is memory state, which is exactly why it is cheaper than Suspend. After a restart the run is
gone and its journal is on disk, so the configuration shows an interrupted run with a Resume button —
the Suspend outcome.

What the hold *does* survive as is the **reason** written next to the journal. `RequestStop` records
a suspension that catches a standing user hold as `UserRequested`, whatever the caller asked for, and
auto-resume leaves those alone.

> **Rationale.** On a NAS the container restart *is* the routine upgrade path, so "pause → pull the
> new image → restart" is the first thing that happens to an operator who pauses and then updates.
> Without this, the backup would be running again with the hold gone and no record it ever existed.
> The reason has to be decided at that point because it is the only place where the stop request and
> the run's gate are both in hand, and it must be read before the control's own `RequestStop`, which
> downgrades the gate and thereby ends the hold.

## Endpoints

```
POST /backup-configs/{id}/pause      → hold the gate
POST /backup-configs/{id}/resume     → release the user's hold
POST /backup-configs/{id}/retry-now  → release the transient-error hold immediately
GET  /backup-configs/{id}/interrupted → read journals left on disk
DELETE (same path)                   → discard them
```

Cancel and Suspend return only once the wind-down is genuinely complete — the journal flushed, temp
files cleared, lease and busy lock released.

> **Rationale.** If the endpoint reported success while the busy lock was still held, the user's next
> action — editing the configuration, deleting it, running again — would collide with a run that is
> not dead yet.

Pause on a run that is already winding down is a conflict, not a no-op: the gate is downgraded and
can never hold anyone again, so answering 204 would be a lie.

## Not covered

- **Pause is not offered for restore or check.** Those runs are short; the machinery is not worth
  duplicating until one of them is measured to need it. Repair earned its own — a field repair of
  hundreds of GB is not short — as `POST /backup-configs/{id}/repair/pause` and `/repair/unpause`:
  an in-memory hold like the backup's (a restart lifts it), awaited before each object **and each
  volume**, so it answers in seconds even midway through a hundred-gigabyte family, and a paused
  repair can still be stopped or suspended.
- **Temp files compressed before an interruption are not reused.** Compression is cheap relative to
  upload, and reusing them would need another class of record plus an integrity check. They are
  garbage and are deleted.
- **Network timeouts and transfer options are not tuned.** When the link is healthy, timeouts are not
  the bottleneck; when it is genuinely down, a longer timeout achieves nothing — the correct response
  is to suspend and wait for a human.

## See also

- [pipeline.md](pipeline.md) — the stages the gates sit on
- [content-identity.md](content-identity.md) — how the journal is consulted during a resume
- [backup-engine.md](backup-engine.md) — the cleanup criterion the journal takes part in
