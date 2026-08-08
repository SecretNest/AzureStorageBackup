# Changing a backup's local root

## The problem

`BackupConfig.LocalRoot` was locked after creation. When the source directory moved to a different mount point on the host and the in-container path moved with it, the configuration could no longer reach its own data: backup, check and restore all stalled on the path boundary or on "everything is missing locally", and the UI offered no way to correct it.

The same lock caused a second gap: when importing an existing backup whose cloud info file has no `SourceRootHint`, `LocalRoot` lands as an empty string — and a locked field meant the user **could not fill it in**. Imported configurations were born half-broken.

What was needed: one dedicated, guarded channel for migrating `LocalRoot` from an old path to a new one.

## The lock's justification does not apply to `LocalRoot`

`AccountId`, `ContainerName`, `LocalRoot`, `IndexTier` and `DataTier` were locked as one group, justified as "local authoritative state is keyed by account plus container, and changing these desynchronises it from the cloud and local indexes". That holds completely for the first two and not at all for `LocalRoot`:

- Local backup state and the cached version indexes are keyed by `(AccountId, Container)`, **independent of the local path**.
- Index entries store paths relative to the root, and scope rules are relative coordinates too.
- Absolute paths appear in exactly two places: the prefix used during scanning, and `SourceRootHint` in the cloud info file — which is documented as advisory only and is rewritten to the new value by the next backup.

**Conclusion**: as long as the new path holds *the same data*, changing the root desynchronises nothing and triggers no full re-upload.

The real danger is not "the content changed" but "an unrelated directory was entered". If that happens, the next backup records the entire backup as fully deleted and fully added, producing an enormous new version in the cloud and possibly pushing older versions out under the retention policy. Every guard in this design points at that one failure mode.

The `IndexTier` / `DataTier` locks are out of scope and stay.

## A revision to an existing premise

The scope-selection design stated:

> `BackupConfig.LocalRoot` is locked after creation, so the relative-path basis of scope rules is permanently stable and needs no extra protection.

This design overturns that premise, and the consequence has to be stated plainly: scope rules are coordinates relative to the root, and after a root change **the rule text is preserved verbatim and never rewritten**. When the new root holds the same data the relative structure is identical and the rules keep matching correctly — which is the normal path for this feature.

If the user forces a migration onto a tree with a different structure, scope rules may point at paths that do not exist. The consequence is that scope matching goes empty or partially fails; it **does not corrupt data and does not delete cloud versions**. The next backup records files that fall outside scope as deleted — exactly the same consequence as narrowing the scope by hand, which the scope-selection design already defines. This risk is spelled out on the forced-migration confirmation screen.

## Design

### 1. Two endpoints: preview and apply are separate

```
POST /api/backup-configs/{id}/local-root/preview   { newRoot }
  → 200 LocalRootPreviewResponse          (validation only; never mutates anything)
POST /api/backup-configs/{id}/local-root           { newRoot, force }
  → 200 BackupConfigResponse | 400 | 409
```

Separate rather than one endpoint with a `confirm` flag: preview is a pure query, idempotent and freely retryable (trying another path leaves no trace), and apply's confirmation semantics are independently identifiable in the log. The repository already has the same shape — restore splits estimate from execution.

Nor "unlock the field on `PUT` and put the validation in the existing update endpoint": that would route everyday edits like renaming and operational actions like migrating a root down one path, and would require the endpoint to ask "did `LocalRoot` change this time?" to decide whether to sample. More importantly it would breach the base-field defence in `UpdateAsync`, and since the reasons for locking `AccountId` and `ContainerName` differ from `LocalRoot`'s, mixing them makes a future mistaken edit easy. **The new channel is another door, not a picked lock**: the locking check in `UpdateAsync` is untouched.

### 2. Validation (short-circuiting, in order)

1. **Busy check** — if the account/container is busy, 409 and nothing further.
2. **Path validation** — non-empty, absolute, inside the boundary, exists, is a directory, is listable. Out of bounds → 409 with `code: "path_outside_root"` (the existing repo-wide convention, not a new one for this feature); other invalid input → 400.
3. **Baseline determination** — is there something to compare against? That is the only question, and it is **independent of what the current `LocalRoot` is, or whether it is empty.**

   (An early draft specified "if `LocalRoot` is currently empty, return `NoBaseline` immediately". Review overturned it: a configuration imported without `SourceRootHint` does have an empty root, but all of its version indexes went into the local index cache at import time, so a baseline is right there — and that is precisely the situation where the user is *guessing* at a mount point, which is the last case that should be waved through on the grounds that no old root was recorded.)

   - The backup has no versions yet → verdict `NoBaseline`, sampling skipped, change allowed.
   - Otherwise fetch the latest version number and read that version's index through the same dependencies the file-versions and tree endpoints use, rather than inventing another route to an index. If the index cannot be read (corrupt info file, undecryptable password, unreadable index blob) → verdict `BaselineUnreadable`, with the underlying exception text in `Reason`, **requiring `force`** (§5). There is no room here for "if it cannot be fetched, treat it as absent": the cache read returns a non-null index and a cache miss throws, so a null baseline can only mean no account, no info file, or no versions.
4. **Sampling** — take up to 200 entries from the index, stratified, and check each one's absolute path under the new root.
5. **Graded verdict** — below.

### 3. Matching: existence plus size only

mtime **takes no part in the verdict**; it is counted separately and reported alongside ("43 of them differ in mtime").

The reason: when data moves across filesystems, mtime precision and preservation are frequently inconsistent (rsync without `-t`, differing timestamp granularity), so using it as a criterion produces false mismatches at scale. And a wrong mtime only means the next backup re-uploads those files, whereas a wrong size is what suggests the wrong directory was entered.

Symlink entries are checked only for "exists and is still a symlink", not for size — an index entry's length is always 0 for a symlink.

Entries carrying `UnreadableAt` are **excluded from the sampling pool**: their size and mtime were carried over from a previous version and were never guaranteed to match the disk, so using them can only manufacture false mismatches.

### 4. Stratified sampling

Entries are bucketed by length (0 / <1 MB / 1–100 MB / >100 MB), each bucket gets a quota proportional to its share, and within a bucket samples are taken **at even intervals in index order rather than from the head**. Index order approximates directory order, so taking the head would concentrate every sample in the first subdirectory — and a half-right migration where only one subdirectory got mounted would go undetected.

When fewer than 200 entries are available in total, all of them are used (the match rate is then a full comparison). When a bucket has fewer entries than its quota, the remainder is redistributed to other buckets rather than wasted.

Sampling is a pure function: entries in, samples out, testable on its own.

### 5. Graded verdicts

| Match rate | Verdict | Behaviour |
|---|---|---|
| `[95%, 100%]` | `Ok` | Apply allowed directly |
| `[5%, 95%)` | `NeedsConfirm` | Requires `force: true` |
| `[0, 5%)` (including finding nothing at all) | `Rejected` | Requires `force: true` |
| No baseline (the backup has no versions) | `NoBaseline` | Apply allowed directly |
| Baseline unreadable | `BaselineUnreadable` | Requires `force: true` |

`BaselineUnreadable` was added during review, and it corrects a mistake in the first draft: that draft folded "the index could not be fetched" into `NoBaseline` — which is exactly the verdict that waves things through without confirmation. A backup **with cloud history whose index cannot be read** — the case that most deserves an extra question — would have sailed through as "this backup has never run". Now they are separate: genuinely no history still passes, while history that cannot be read puts the underlying exception into `Reason` and demands `force`.

Intervals are closed on the left and open on the right, so a boundary value falls into the more permissive grade (exactly 95% is `Ok`, exactly 5% is `NeedsConfirm`).

`Rejected` refuses by default but **can still be overridden with `force: true`**. That is deliberate: the user has no command line on the NAS and cannot `ls` around to investigate, so a hard block with a mistaken verdict would leave no way out at all. The frontend makes the override a checkbox that must be ticked deliberately, not a button that is easy to click through.

### 6. The report

```csharp
public record LocalRootPreviewResponse(
    string Verdict,            // "Ok" | "NeedsConfirm" | "Rejected" | "NoBaseline" | "BaselineUnreadable"
    int Sampled,
    int Matched,
    int Missing,
    int SizeMismatch,
    int MtimeDiffers,          // informational, not part of the verdict
    double MatchRate,
    string? Reason,            // why there is no baseline, or why path validation failed
    IReadOnlyList<string> Examples);   // up to 10 mismatching relative paths
```

`Examples` is not decoration: with no command line, the UI has to put "which files actually do not match" directly on screen, or a 68% match rate gives the user nothing to judge a forced override by.

### 7. Persisting

Only `LocalRoot` changes; nothing else is touched. Scope rules keep their text (see the revision above). The local index cache and local backup state are neither invalidated nor cleared — they are independent of the path.

One operation log entry records: old root → new root, verdict, match rate, and whether it was forced.

### 8. Races

Apply **does not trust the preview result sent by the frontend** and reruns the full validation itself — which is precisely why the inspection has to be a pure query and safely re-entrant. The new root being unmounted after the preview, or the backup starting between the two calls, is caught by apply's own pass.

## Structure

The domain logic lives in one **static class with no dependency injection**: `Inspect(string newRoot, VersionIndex? baseline)`. It reads the filesystem only — no database, no cloud, no decryption. Everything needed to fetch the index (account, password, cloud info) is prepared by the endpoint, which passes in the `baseline`. That makes it unit-testable without HTTP, EF or Azure, and it splits internally into stratified sampling (a pure function) and filesystem comparison.

The endpoints do orchestration only: load the configuration → busy check → inspect → judge by verdict and `force` → persist and log. The two `MapPost` calls sit next to `reset-password`, as another "dedicated change channel for a field restricted after creation".

On the frontend, the verdict → UI decision is extracted into a pure function (can Apply proceed, is the force checkbox required, which message key) and tested with vitest; the dialog component only draws that function's output. This is because the repository's frontend tests cover pure logic only and there is no component-rendering infrastructure, which this feature does not introduce.

## How the report renders

| Verdict | Presentation |
|---|---|
| `Ok` | Green summary ("196 / 200 sampled entries match"), Apply available |
| `NeedsConfirm` | Match rate, mismatching examples, and an `I understand — change anyway` checkbox that must be ticked before Apply enables |
| `Rejected` | The same, worded more strongly, plus "the next backup will record every file as deleted and re-upload it" |
| `NoBaseline` | "No previous version to compare against — only the path itself was checked." Apply available |

## Pinned behaviour

Stratified sampling covers all four buckets, does not collapse onto the head, and neither repeats nor overruns when a bucket has fewer entries than its quota. `UnreadableAt` entries are excluded from the pool. Symlinks are matched on existence alone and never fail on size.

Full match gives `Ok`, partial gives `NeedsConfirm`, an empty directory gives `Rejected`. No versions gives `NoBaseline`; an unfetchable index gives `BaselineUnreadable` with a non-empty `Reason`. **An empty `LocalRoot` with a usable baseline still samples** — an import missing `SourceRootHint` must not be waved through.

Out-of-bounds paths give 409 with the shared error code; empty, relative, pointing at a file, or non-existent paths give 400. Busy gives 409 **and persists nothing**. `NeedsConfirm` and `Rejected` without `force` persist nothing; with `force` they persist. After persisting, a field-by-field assertion confirms only `LocalRoot` changed and everything else — scope rules included — is byte-identical.

**`UpdateAsync` still refuses to change `LocalRoot`**, so the new channel cannot have loosened the old defence. And the database is unchanged across a preview call, which guards its pure-query nature.

## Deliberately not done

- No bulk migration across configurations. One at a time, each with its own report to read.
- No automatic inference or suggestion of a path prefix.
- No change to the `IndexTier` / `DataTier` / `AccountId` / `ContainerName` locks.
- No automatic check after migrating. The UI saying "the next backup will scan from the new path" is enough — the sampling already provided the confidence, and a full check would be duplicated work.
