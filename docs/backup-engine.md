# The backup engine

One run of a backup, from walking the source tree to committing the version. The stages that carry
their own weight have their own document; this one is the spine they hang off.

```
Scan → Diff → Plan → Compress → Upload → WriteIndex → Finalize → Cleanup
```

Compression and upload are three concurrent stages rather than steps in this line — see
[pipeline.md](pipeline.md). How each stage decides "changed" and "already exists" is in
[content-identity.md](content-identity.md).

## 1. Scan

`LocalFileScanner` walks the configured local root and produces entries carrying path, kind, length,
mtime and permissions. Three filters apply, in this order:

1. **Scope rules** — the configured subset of the root ([configuration.md](configuration.md)).
2. **Ignore rules** — gitignore syntax, shared engine (§ *The rule engine* below).
3. **Symlink handling** — skipped by default; included as `kind: "symlink"` with a `target` when the
   backup opts in. A symlink's permissions are recorded as a fixed `"0777"`, never stat'd through
   the link: resolving a dangling target throws, and the throw was being swallowed as "unreadable" —
   silently dropping a legitimate entry whose target string has nothing to do with the target
   existing. Diff compares symlinks by target alone, so the constant costs nothing.

Empty directories are collected separately and recorded in the version index, because restore has to
recreate them.

A directory that cannot be listed is recorded as unreadable rather than skipped — see § *Unreadable
input*.

## 2. Diff

`BackupDiffer` compares the scan against the previous version's index and classifies every path:

| Classification | Meaning |
|---|---|
| `Added` | not in the previous version |
| `Modified` | content changed |
| `MetadataOnly` | content identical, mtime or permissions moved — the index is updated, nothing is re-uploaded |
| `Unchanged` | nothing read, every field carried forward |
| `Deleted` | present before, absent now — excluded from the new version |
| `Unreadable` | could not be read this round — the previous entry is carried forward |

The ladder that decides between these — length, then mtime and permissions, then head, tail and full
hashes — is documented in [content-identity.md](content-identity.md).

The diff also produces the **work queue**. `DiffWorkQueue` caps the consumer backlog at 2,000 items /
64 MB and spills the remainder to a file.

> **Rationale.** The write side must never block. If it did, the totals would keep growing while
> consumers ran, and the ETA — which is withheld until the denominator settles — would appear only as
> the run was finishing. Both bounds are needed: an item count alone does not bound memory when paths
> are long, and a byte bound alone does not bound the object count.

## 3. Plan

`GroupingPlanner` decides, for each changed file, whether it becomes its own blob or joins a pack.
The threshold, the don't-group rules and the per-group cap are in [packing.md](packing.md), along
with dead-weight compaction.

Deletions do not enter the plan. Pack members whose content already exists are settled here rather
than being planned at all ([content-identity.md](content-identity.md) § *Pack members*).

## 4–5. Compress and upload

See [pipeline.md](pipeline.md) for the three-stage split, the staging area, backpressure, the raw
in-place route and the volume upload gate.

## 6. Write the index

The version's second-level index is uploaded first and its success confirmed before anything else
records that the version exists. A new version writes its own file and never rewrites an older one,
except where dead-weight compaction forces it.

## 7. Finalize

The info file is updated atomically: the new contents go to a temporary blob, and only on success is
the real name overwritten. A network failure therefore cannot corrupt it. The write carries
`If-Match` against the recorded ETag, so an external change — another machine, a recreated container
— is detected rather than silently overwritten; on conflict the local state is cleared and reported,
and the next run resyncs.

## 8. Retention and cleanup

Retention is evaluated when a backup completes, and by the scheduled cleanup task.

The policy is a maximum version count (100 by default) plus a maximum age (180 days by default), with
a configurable rule for how the two combine: both reached, either reached, count only, or age only.

Deleting a version deletes its second-level index, plus any data blob or pack no longer referenced.
`RetentionCleaner` applies one criterion:

> Delete a block when it is referenced by no retained version **and** by no active journal.

Three details are load-bearing:

- **Retirement commits the info file first, deletes after.** The info file without the retired
  versions is written before their index volumes are touched; the deletes are then best-effort per
  volume. Inverted, a crash between the two leaves an info file naming an index that is gone — an
  unreadable backup — whereas committed-first the worst case is a retired index lingering as an
  unreferenced blob, which the orphan sweep collects later.
- **Volume families are deleted as a unit.** `data/{hash}.NNN` is normalised back to the base name
  when comparing references, and packs are grouped by pack id over the `packs/` prefix, so a
  referenced volume is never deleted by mistake.
- **The journal set is part of the criterion, not a separate pass.** There is no "look up a journal
  and delete what it references" operation; discarding or voiding a journal only removes it from the
  active set, after which an ordinary cleanup runs.

> **Rationale — why the criterion is unified rather than a reverse lookup.** Crashing between "the
> info file is committed" and "the journal is deleted" leaves a journal whose baseline version has
> changed, so it is voided — while the blocks it references are **already referenced by the new
> version's index**. A cleanup that deleted by reverse lookup would destroy data in active use. The
> unified criterion cannot have that problem.

## Post-processing recheck and repeat protection

A source file can change while it is being read, compressed or uploaded. The invariant is the same on
every route — **the index must describe the bytes that were actually stored, and no archive may
travel with a file in it that has already changed** — but the three routes reach it differently, and
only one of them has a repeat counter.

**Single-file blobs — no recheck; the read is the record.** The hash, the length and the mtime all
fall out of the one compression pass (`StreamAndStageAsync`), so the bytes hashed and the bytes
stored are the same set by construction and there is no window to check afterwards. When the content
turns out to differ from what the diff saw, an `EntryOverride` is written and the index entry is
built from **it** rather than from the scan — same file, new length, new hashes. Nothing is
reprocessed, because nothing needs to be: what went to the cloud is correct, it is just not what the
diff expected.

**Packs — recheck, exclude and recompress.** The group's members are `stat`ed before compression and
re-verified after it, all on the compression stage and all **before** the upload. A member counts as
changed only when its metadata **and** its content hash both moved; it is then taken out of the
archive and re-queued into a later group for that directory, or handled as a single file if there is
none — and the archive that contained it is discarded and recompressed without it.

> **Rationale.** The requirement is an invariant, not an implementation: *never upload a pack that
> keeps an already-changed file in it as garbage*. Excluding and recompressing satisfies it, and
> settling it before the upload means the wasted work is one compression, not one transfer.

`ProcessingMaxAttempts` (5 by default) belongs to **this route only**. A member that keeps changing
across that many attempts is demoted to a single-file blob and the run raises a warning; a member
that grows past the single-file threshold is demoted immediately, without waiting for the counter.

**The raw in-place route — a stat bracket around the whole upload.** Length and mtime are compared
before the hashing read and again after the upload has ended, however it ended; any movement means
the blob just written may not hold the content its address names, so it is deleted and the item is
retried through the copying route. Described in full in [pipeline.md](pipeline.md).

## Unreadable input

One file that cannot be read must never invalidate the backup of the other tens of thousands.

| Situation | Handling |
|---|---|
| Read failure at any site | Warn, skip that file, continue |
| Exceptions caught | `IOException` and `UnauthorizedAccessException` only |
| A previously backed-up file becomes unreadable | Carry the previous entry forward, marked `UnreadableAt` |
| A new file is unreadable on first sight | It does not enter this version; a warning is raised |
| An unreadable pack member | Excluded from the archive, which is recompressed without it |
| A file that stays locked | Warns **every** round — it genuinely is not being backed up |

`OperationCanceledException` is deliberately **not** caught: catching it would turn a cancellation
into "skipped one file", and the backup would look successful while never having finished.

> **The rule of thumb.** Whenever "what should happen when X cannot be read" comes up, ask first:
> does this handling make the diff judge it deleted? If so, it is data loss. Unreadable must never be
> treated as deleted — otherwise, after retention rolls over a few rounds, a permanently locked file
> silently disappears from every version.

### `UnreadableAt` on the index entry

When the previous version had the file, the new entry is copied **wholesale** from it — length,
mtime, permissions, all three hashes and storage — with only `UnreadableAt` added. It therefore
uploads nothing, produces no new blob and does not affect dedup.

The field records **the first** round the file could not be read and is not refreshed afterwards: the
question it answers is "since when has this content been unable to update". A round that reads the
file successfully again rebuilds the entry normally and the field returns to null.

It surfaces in four places, so it is not a write-only field: the run summary and success
notification, `GET /backup-configs/{id}/unreadable?version=`, the restore tree and dialog, and check
report entries.

> **Rationale — why there is no per-version substitution for it.** Carried-over content is *valid*
> data and is the best this version can give. Offering a substitution picker would imply a better
> option exists. `UnrecoverablePaths` is different: that data is actually broken, and substitution
> is exactly right there.

### Two boundaries that were real defects

**An upload failure must not be mistaken for an unreadable file.** The uploader classifies
`IOException` as a retryable network error and rethrows once the budget is exhausted — the same
exception shape as "the file could not be read". Accepting it on type alone turns one NAS network
outage into several "unreadable files" while the run reports success. The catch filter therefore
**probes the source again**, opening it and genuinely reading a byte; if it reads fine, the exception
keeps propagating.

**Post-upload work must sit outside the catch.** An early version wrapped the whole processing unit,
so a failure writing a verbose log *after* a successful upload was misread as unreadable — and a blob
already in the cloud was dropped from the index. The catch covers the source read alone.

### 7z drops members it cannot read

Measured: for a member it cannot read, 7z emits a **warning** (exit code 1), drops the member, and
still produces a **completely valid** archive — including a 59-byte empty one when no member could be
read at all. The calling layer originally threw only at exit code ≥ 2, so a pack missing a member was
uploaded as a normal result while the index claimed the member was inside.

On exit code 1 the compressor now lists the archive's actual contents (`7z l -slt`) and compares
against the requested entries. Exit code 0 is necessarily complete, so this costs nothing on the
happy path; encrypted archives need the password passed, since `-mhe=on` hides even the entry names.
A confirmed absence throws, and backup folds the missing members into the existing exclusion path.

> **The compaction path was worse.** It overwrites in place, so a pack holding a/b/c could be
> rewritten to hold only c while b was still referenced by a valid version — permanent data loss.
> Compaction now abandons the optimisation rather than ever overwriting an intact pack.

### Unreadable directories

A directory whose contents cannot be listed originally crashed the run during scanning. Wrapping it
in a `try` and skipping would be **worse**: the subtree goes unscanned, the diff judges every entry
beneath it `Deleted`, and one permission failure wipes an entire subtree out of the index — after
which retention deletes the data blobs.

The scan records these paths, and the differ registers the whole subtree into its `seen` set and
marks it unreadable **before** deletion is judged, so it takes the same carry-forward path as the
file-level case. An unreadable directory never enters `EmptyDirs` (restore would recreate an empty
shell), while empty directories beneath it from the previous version are carried over.

### Reporting

Unreadable files go out through the `UnrecoverableError` notification event — the only push channel —
which also raises the log level from Warning to Error.

> **Rationale.** Operation logs are pull-only, and on an unattended deployment nobody goes looking.
> This information is supposed to be glaring.

The system's own reason is preserved verbatim: "held open by which process", "insufficient
permissions" and "device read error" need different responses, and flattening them into "could not be
read" leaves the operator with nowhere to start. An unreadable **directory** pushes one summary
including the affected count — a directory with five thousand files would otherwise become five
thousand webhooks, drowning the operator and stalling the run on pushing.

## The rule engine

One gitignore-syntax implementation (`IgnoreRuleSet`) is shared by four rule sets:

| Rule set | Effect |
|---|---|
| Ignore | matching paths are excluded from the backup entirely |
| Don't-compress | matching paths are stored without compression (still backed up) |
| Don't-group | matching paths are never packed; each becomes its own blob |
| Cross-directory | matching paths are packed by full-path order, ignoring directory boundaries ([packing.md](packing.md)) |

Negation with `!` is supported, and the last matching rule wins.

### Each set has two halves, and they are one set

Every rule set holds **two** blocks of text — a case-sensitive one and a case-insensitive one — and
each half inherits from global settings on its own (`IgnoreRules` / `IgnoreRulesCaseInsensitive`, and
so on for the other three). Overriding "the mp4 rules" for one backup should not silently drag the
global path rules along with it, nor the other way round.

`BackupRequestMapper.Rules` concatenates the pair into **one** `IgnoreRuleSet` — sensitive first,
insensitive after — with case sensitivity carried **per rule** (`IgnoreRuleSet.FromTagged`).

> **Rationale — why one set rather than two consulted in turn.** "The last matching rule decides" has
> to keep holding across the pair. Two sets OR-ed together would break exactly that: a `!keep.mp4` in
> either half could never override a match in the other, and the negation would silently do nothing.
> Sensitive-first is the arbitrary half of the choice; what matters is that the order is fixed and
> written down, so a negation can be authored knowing what it overrides.

> **Rationale — why insensitivity is a flag and not something you write in the pattern.** There is no
> character-class support: everything that is not `*` or `?` goes through `Regex.Escape`, so `[wW]`
> matches those three characters literally. `*.JPG` and `*.jpg` as separate rules is the alternative,
> and it does not scale past a couple of extensions.

Scope rules are deliberately **not** part of this engine — they are exact paths with
longest-prefix-wins semantics, and the reasoning is in [configuration.md](configuration.md).

## Concurrency and the busy lock

`BackupBusyTracker` tracks busy state per `(account, container)` and sets it for backup, restore and
check alike. A scheduled task whose target is busy raises a Warning and skips rather than
interrupting. HTTP backup and restore refuse to run concurrently; a manual check returns 409.

Note that **production is globally serial** — the compression lock is a process-wide singleton — so
running two backups at once is measurably slower than running them in sequence. See
[pipeline.md](pipeline.md).

## See also

- [content-identity.md](content-identity.md) — change detection and dedup
- [pipeline.md](pipeline.md) — compression, staging and upload
- [packing.md](packing.md) — grouping and dead-weight compaction
- [run-lifecycle.md](run-lifecycle.md) — pause, suspend, resume and the journal
- [storage-format.md](storage-format.md) — what gets written to the container
- [check-restore-repair.md](check-restore-repair.md) — verifying and getting data back
