# Unreadable input during a backup

> A whole backup failed because one file was held open by another process:
>
> ```
> Backup failed: The process cannot access the file
> '/nas/.../VDDatabase/Vistopia.mdf' because it is being used by another process.
> ```
>
> One unreadable file must not invalidate the backup of the other tens of thousands.
>
> Supplements [m4-backup-engine-design.md](m4-backup-engine-design.md) §9.

## 1. What was already correct

Two things already worked as designed and are untouched:

- **The post-processing recheck**: after processing, compare mtime, permissions and length; if they changed, re-hash; if the content really changed, reprocess under the new hash. After `ProcessingMaxAttempts` repeats (5 by default), warn and save the current content.
- **The pack-member recheck**: snapshot `Stat` for every member before compression, compare afterwards, and on any change discard that archive and recompress with the changed member excluded.

For grouping, the requirement was stated as an **invariant**, not an implementation:

> You may exclude the file and recompress the pack, or compress the whole thing again. But do not upload a pack that keeps an already-changed file in it as garbage.

Excluding and recompressing satisfies that invariant, so it stays.

## 2. Decisions

| # | Question | Conclusion |
|---|---|---|
| 1 | Consequence of a read failure | **Warn, skip the file, continue.** Never abort the whole run over one file |
| 2 | What is caught | `IOException` and `UnauthorizedAccessException`. **Not** `OperationCanceledException` — cancellation must keep propagating |
| 3 | A previously backed-up file becomes unreadable | **Carry the previous version's entry forward**, marking on the index entry that it could not be re-read this round |
| 4 | A new file is unreadable on first sight | **It does not enter this version**, only a warning. There is nothing to point at, and fabricating an entry would be a lie |
| 5 | The thing never to do | Unreadable must **never** be treated as deleted. Otherwise, after retention rolls over a few rounds, a permanently locked file silently disappears from every version |
| 6 | An unreadable pack member | Same exclusion path as "the member changed": exclude it and recompress. Satisfies the §1 invariant |
| 7 | Repeated warnings | A permanently locked file warns **every round**. That is deliberate: it genuinely is not being backed up |

## 3. Handling a read failure

**One precondition**: the diff only hashes files it suspects are new or modified — a file whose size and mtime are both unchanged is never re-read. So a file that has been locked for a long time but whose content matches the last round never triggers a read at all. What actually triggers this is "locked **and** the size or mtime changed" — exactly the `.mdf` case.

| Site | Handling |
|---|---|
| Scan / diff | Marked unreadable for this round, excluded from planning |
| Single-file processing | As above, and no blob is produced |
| Post-processing recheck | Treated as unreadable, this round's result discarded |
| Pack-member recheck | Exclude the member and recompress (decision 6) |

### 3.1 Reporting goes through the push channel, not just the log

Operation logs are pull-only, and on an unattended deployment nobody goes looking. Unreadable files therefore reuse the existing `UnrecoverableError` notification event — the only push channel — which also raises the log level from Warning to Error. That is intentional: this information is supposed to be glaring.

The system's own reason must be preserved verbatim. "Held open by which process", "insufficient permissions" and "device read error" need different responses, and flattening them into "could not be read" leaves the operator with nowhere to start.

An unreadable **directory** pushes one summary (including the number of affected entries); the file-level entries derived from it are not pushed individually. A directory with five thousand files would otherwise become five thousand webhooks, which both drowns the operator and stalls the backup on pushing.

### 3.2 Failures after the diff count too

The diff is not the only place the source is opened — it is opened again for packing or raw passthrough, and on a large backup that window can be hours long. Failures there are handled identically to failures during the diff: no blob, the index carries the old entry forward, and it counts towards the total.

Two boundaries here were both real defects:

- **An upload failure must not be mistaken for an unreadable file.** The uploader classifies `IOException` as a retryable network error, and once the retry budget is exhausted it rethrows — with exactly the same shape as "the file could not be read". Accepting it on exception type alone means one NAS network outage gets recorded as several "unreadable files" while the run reports success, and the operator reads "Backup succeeded, 0 changed files". The catch filter therefore **probes the source again** (open it and genuinely read one byte); if it reads fine, the exception keeps propagating.
- **Post-upload work must be outside the catch.** An early version wrapped the whole processing unit in the `try`, so a failure writing a verbose log *after* a successful upload was misread as an unreadable file — and a blob already in the cloud was dropped from the index. The catch now covers only the actual source read.

## 4. How it appears in the index

`IndexEntry` carries a nullable field:

```csharp
/// This round could not re-read the file (locked / no permission / read error); the entry's
/// content is carried over from the previous version. null = read normally this version.
public DateTimeOffset? UnreadableAt { get; init; }
```

- **The previous version had the file**: the new entry is copied **wholesale** from it (length, mtime, permissions, all three hashes and storage), so it points at the same already-uploaded content, with only `UnreadableAt` added. Restore uses this to say "this is the last content that was read successfully".
- **The previous version did not**: it does not enter this version's index.

An entry with `UnreadableAt` set **uploads nothing** — it reuses the old entry's storage, so it produces no new blob and does not affect dedup.

### 4.1 Semantics and where it surfaces

**Semantics**: it records **the first** time the file could not be read, and is not refreshed on subsequent rounds. The question it answers is "since when has this content been unable to update"; overwriting it with the current time each round would erase the answer. When a round reads the file successfully again the entry is rebuilt normally and the field returns to null.

It would otherwise be a write-only field — written into the index and read by nobody — so it surfaces in four places:

- The run state and the success notification carry this round's unreadable count.
- `GET /backup-configs/{id}/unreadable?version=` lists the carried-over entries in that version. Symmetric with `/unrecoverable`, but different in meaning: there the data is damaged and there is nothing to give, here the content is valid, just old.
- Restore tree nodes and the restore dialog summary — the moment of choosing what to restore is exactly when it matters most to know that what you get is not the content as of that version's timestamp.
- Check report entries — without it, `Local=Changed` reads as "the local file was modified", when the real cause is that the backup never managed to update the cloud copy.

**No per-version substitution is offered.** Carried-over content is *valid* data and is the best this version can give. Flagging it is enough; adding a substitution picker would imply a better option exists. `UnrecoverablePaths` needs substitution because that data is actually broken.

## 5. 7z silently drops members it cannot read

The original assumption was that a failed compression fails. Measurement says otherwise: for a member it cannot read, 7z emits a **warning** (exit code 1), drops the member, and still produces a **completely valid** archive — including a 59-byte empty archive when no member could be read at all. The calling layer only threw at exit code ≥ 2.

The consequence is a pack missing a member being uploaded as a normal result, while the index claims the member is inside, surfacing only at restore or deep check. **The compaction path is worse**: it overwrites in place, so a pack holding a/b/c is rewritten to hold only c while b is still referenced by a valid version — permanent data loss.

On exit code 1 the compressor now lists the archive's actual contents (`7z l -slt`) and compares against the requested entries. Exit code 0 is necessarily complete, so this costs nothing on the happy path; encrypted archives need the password passed, since `-mhe=on` hides even the entry names otherwise. A confirmed absence throws, backup folds the missing members into the existing exclusion path, and compaction abandons the optimisation rather than ever overwriting an intact pack.

## 6. Unreadable directories

The original design stopped at file level. A directory whose contents cannot be listed would crash the whole run during scanning — and **simply wrapping it in a `try` and skipping would be worse**: the subtree goes unscanned, the diff judges every entry beneath it Deleted, and one permission failure wipes an entire subtree out of the index, after which retention deletes the data blobs.

The scan result now records these paths (directories and files marked separately), and the differ registers the whole subtree into `seen` and marks it unreadable **before** deletion is judged, so it takes exactly the same carry-forward path as the file-level case. An unreadable directory never enters `EmptyDirs` (that would make restore recreate an empty shell), while empty directories beneath it from the previous version are carried over so the restored structure is not missing a piece.

> **The rule of thumb**: whenever "what should happen when X cannot be read" comes up again, ask first — does this handling make the diff judge it deleted? If so, it is data loss.

## 7. Check has to survive it too

Local verification originally had no protection, so one unreadable local file crashed the entire check — and "there are unreadable files" is precisely when a check is most needed. It is now handled as `LocalState.Missing` (no usable local copy, and unusable as a repair source), consistent with the existing "path outside the root" case.

## 8. Known consequences

- An exclusively locked file like that `.mdf` warns every round and is never backed up. The real fix is to add it to the ignore rules and use the database's own export mechanism — that is an operations decision, not one this tool should make on the operator's behalf.
- Carrying an entry forward means restoring that version yields **old content**. The index marking makes it visible, but the operator still has to understand what it implies.

## 9. Pinned behaviour

Each of the read sites continues the backup when reading throws, with the remaining files backed up normally. A previously backed-up file that becomes unreadable produces an entry pointing at the old content with the marker set, **and does not appear in the deletion set** — that assertion is the guard for decision 5. A new unreadable file is absent from the index and produces a warning. An unreadable pack member is absent from the uploaded pack while the other members pack normally — the guard for the §1 invariant. Cancellation still aborts the backup rather than being swallowed as unreadable. Two consecutive unreadable rounds produce two warnings.
