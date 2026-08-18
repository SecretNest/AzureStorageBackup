# Deleted bytes in the run summary

## The problem

A finished backup reports `12 deleted` and stops there. Twelve deleted files can be twelve empty log
stubs or twelve 400 GB disk images, and the summary gives the operator no way to tell which. The
count alone is the one item on that line with no sense of scale attached to it — `new` and `modified`
at least have `changed at source` standing behind them.

## Starting point

Everything needed is already in hand at the point the summary is built:

- `ChangeKind.Deleted` changes are synthesized in `BackupDiffer.DiffAsync` (`BackupDiffer.cs:161`)
  and always carry the previous version's `IndexEntry` in `FileChange.Previous`.
- `IndexEntry.Length` is the source-side raw byte size — the same figure summed into
  `VersionStats.Bytes`, and the same unit as `BackupRunResult.ChangedBytes`.
- `BackupOrchestrator` already walks `diff.Changes` once at the end to count the categories
  (`BackupOrchestrator.cs`, the single-pass category loop at the tail of `RunCoreAsync`, just before it
  builds the `BackupRunResult`).

So the number costs one addition inside a loop that already runs. No extra pass, no extra IO, no
index format change.

## Design

### 1. `BackupRunResult.DeletedBytes`

The counting loop gains one accumulator:

```csharp
case ChangeKind.Deleted: deletedFiles++; deletedBytes += c.Previous?.Length ?? 0; break;
```

Symlinks carry `Length` 0 (their content is the `Target` string), so deleting one contributes
nothing — correct, it never occupied that much at the source either.

**What this number is not.** It is the size the deleted files *had at the source*, not space freed in
the cloud. Older versions still reference that content, and it stays in the container until retention
retires those versions; what the cloud actually gave back is the `freed` figure on the retention line.
The two are different quantities and must never be added together.

### 2. The same wording in both places

`BackupSummary` (operation log + webhook notification) and `runSummary.ts` (`runTotals`, the Backups
page) are deliberate mirrors of each other, so both gain the figure in the same shape — parenthesised
right after the count, where it reads as "the size of *those* files":

```
Files: 3 new, 1 modified, 12 deleted (4.7 GB)
Data: 1.2 GB changed at source → 310 MB uploaded
```

It is kept out of the `Data` line on purpose. That line tracks one thing — what changed at source
versus what went over the wire — and deleted bytes are neither. Putting them there invites reading
them as part of the upload arithmetic.

The existing "a zero makes its item disappear" rule extends to the parenthesis: zero bytes renders as
plain `12 deleted`. A round that deleted twelve empty files should not be made to carry `(0 B)`.

### 3. Transport

`long? DeletedBytes` follows the path the other figures already took, nullable throughout:
`BackupRunResult` → `BackupRunState` → `BackupRunResponse` → `BackupRun.deletedBytes` → `runTotals`.

An older backend sends no such field. The frontend then shows the count alone and **must not**
substitute 0 — "deleted nothing worth mentioning" is a claim, and nothing is known here to support it.
The existing older-backend probe in `runTotals` (all figures null → report nothing) stays as it is:
`deletedBytes` adds no information to it, since a backend that sends this field sends the others too.

## Tests

- `BackupSummaryTests`: the figure appears after the count; zero bytes leaves `12 deleted` bare.
- `BackupStatsTests`: an end-to-end run that deletes files reports their source size in `DeletedBytes`.
- `BackupRunStateTests`: the field survives the run-state → response hop.
- `runSummary.test.ts`: the same two wording rules, plus the older-backend case (count, no size).
