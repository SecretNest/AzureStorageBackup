# Catalog format v2: one row per path per change

> **Status: design, approved 2026-09-13; not implemented.** This document is the spec the
> implementation plan is written from. When the code lands, the parts that describe the running
> system move into [storage-format.md](storage-format.md) and this file becomes history.

## Why

The catalog ([storage-format.md](storage-format.md), "The catalog") stores every entry of every
retained version as its own row, indexed nine ways. Two consequences were measured on the user's
largest container (3/database, 1.1 M files, 15 versions) on 2026-09-12/13:

- **Per-run work is proportional to the file count, not to what changed.** A version with 2,675
  changed files inserted 1,111,050 rows. Into the three content-keyed indexes — keyed by hash, ref
  and length, so a new version's rows land at random over the whole history — that was 1.2 GB/min of
  disk reads for over five hours on an 8 GB file, with the run standing still. The interim fix
  (`VersionCatalog.PrefersRebuild`, 2026.9.12.4) drops and re-sorts those indexes for a version that
  is at least a hundredth of the history: 5 minutes of import plus 8–15 minutes of rebuild, and the
  rebuild grows with the history.
- **Size is linear in versions × files.** 10.2 GB for 15 versions, about 1 GB per version, on a
  retention policy that keeps up to 100. Every full read of the file — the operator's check, the
  diff's cursor over the previous version — pays for that.

The per-row weight has its own cause. The `entries` table is `WITHOUT ROWID` with `(version, path)`
as its primary key, and SQLite copies the whole primary key into every secondary index as the row
pointer. Measured on a synthetic 200,000-row catalog with the production schema and 68-character
paths: 1,765 bytes per row, 1,150 of them in the eight secondary indexes, each carrying a path copy.

## Goals

1. A backup's catalog work is proportional to the number of changed entries.
2. The catalog's size is proportional to the number of distinct paths plus the number of changes
   across retained versions, not to versions × files.
3. Every question the catalog answers today is answered the same way, with the same results — the
   query surface (`VersionCatalog`, `VersionCatalog.Queries`) keeps its signatures.
4. Existing catalogs convert in place, once, without downloading anything, and a conversion that
   dies is repeatable.

Out of scope: the diff's own hour (61 minutes over 1.1 M files, 2026-09-13). The diff breakdown
line shipped in 2026.9.13.1 says where it goes; the row slimming here reduces what its cursor reads,
and a later change can stop seeding unchanged entries into the run's draft. Neither is this document.

## The model

An `entries` row is one path's complete entry — content hashes, storage reference, mtime,
permissions, unreadable stamp, unrecoverable flag — together with the half-open version interval
`[version_from, version_to)` over which that entry is what the path looked like. `version_to` is a
sentinel (`2147483647`) for "still current".

| Event at version N | Rows written |
|---|---|
| path unchanged | none |
| path modified (any column of the entry) | close the current row (`version_to = N`), insert `[N, ∞)` |
| path deleted | close the current row |
| path added | insert `[N, ∞)` |

"Modified" means the serialized entry differs in any field, storage and flags included: a repair
that rewrites a version's storage reference is a change like any other (see Repair patches).

The table is an ordinary rowid table. `(path, version_from)` is unique. Secondary indexes carry an
8-byte rowid instead of a path copy. The hash columns stay TEXT: `EntryRowMapper` is shared with the
run's work database, and a catalog-only BLOB encoding would be a second definition of the same row
for a saving of about 100 bytes a row against the 1,150 the key change removes. `seq` is gone: a
version's order is its `path_key` order (see Legacy order).

`dirs` becomes interval rows on the same rule — a directory is current while any entry below it is —
so browsing a version stays one indexed query. `empty_dirs`, `unrecoverable` and `import_issues`
keep their per-version shape; they are a few rows per version.

`versions` is unchanged. `PRAGMA user_version = 2` marks the format; a catalog at `user_version 0`
is format 1 and is converted (see Migration).

### Indexes

| Index | Answers |
|---|---|
| `(path_key)` | the diff cursor, serialization, any "all entries of version N" walk |
| `(parent, path_key)` | browsing a directory at a version |
| `(path_fold)` | case-insensitive search |
| `(full_hash, length)`, `(storage_ref)`, `(length, head_hash)` | dedup, retention, repair — unchanged in meaning |
| `(version_to)` | retention's deletion of rows no retained version can reach |
| `dirs (parent, path)` | subdirectories of a directory at a version |

`path_key` (the path's UTF-16 big-endian bytes) stays: it is the ordinal order both diff cursors
walk in, and a TEXT collation would not reproduce it for characters outside the BMP. `parent` and
`path_fold` stay for their indexes. The three copies cost about 200 bytes a row; the primary-key
copies they replace cost 1,150.

Estimate for 3/database after conversion: about 1.2 M rows (paths plus 15 versions' changes) at
roughly 1 KB a row with indexes — about 1.3 GB in place of 10.2 GB, growing by a few MB a day.

## Writing a version

The run keeps reading back the `.idx` file it uploaded ("the catalog holds exactly what the
container holds" stays true by construction), but the import is a merge rather than a bulk insert:

```
cursor A: the .idx entries, in path_key order (they are written in that order)
cursor B: rows with version_from <= N-1 < version_to, in path_key order
walk both:
  same path, same entry   → nothing
  same path, different    → close B's row at N, insert A's entry as [N, ∞)
  only in A               → insert [N, ∞)
  only in B               → close B's row at N
```

Reads are sequential over both sides (previous rows + new entries); writes are the changes. The
content-keyed indexes see a few thousand inserts, so the drop-and-rebuild bracket of 2026.9.12.4 is
no longer needed for a run's own import and is removed from that path. `EnsureVersionsAsync` keeps
it for a migration of several missing versions, where the merge would otherwise touch the three
indexes once per version.

Entry equality is field-by-field over the serialized entry. `EntryRowMapper` already defines the
column list; the comparison is written next to it so the two cannot drift.

Re-importing a version that is already present (a repair rewrote its index) is "remove, then
import": `RemoveVersionAsync(N)` first (below), then the merge against N-1.

The Updating catalog stage counts entries consumed from the `.idx` cursor, as it does today.

`ImportVersionAsync`'s two overloads keep their signatures. The `IAsyncEnumerable<IndexEntry>`
overload requires its entries in `path_key` order and throws on a violation, as the differ's
`AscendingPaths` guard does.

## Reading

Every `WHERE version = @v` becomes `WHERE version_from <= @v AND version_to > @v`. The queries:

| Query | v2 shape |
|---|---|
| `EntriesAsync(v)` | scan `(path_key)`, filter the interval — reads paths + changes rows, not paths × versions |
| `GetEntryAsync(v, path)` | `(path, version_from)` unique index: the row with the greatest `version_from <= v`, then check `version_to` |
| `EntriesAtAsync(v, paths)` | the same, per path |
| `ChildrenAsync(v, parent)` | `(parent, path_key)` plus interval; subdirectories from `dirs` the same way |
| `StatsAsync(v)` | interval filter over the scan; also cached on `versions.entry_count` as today |
| `SerializeVersionAsync(v)` | `EntriesAsync(v)` order, or the legacy order table when the version has one |
| `FindBlobByContentAsync`, `HeadSeenAsync`, `FindPackMemberAsync`, `FindRefOwnerAsync`, `IsDamagedRefAsync` | unchanged: no version predicate, a row's existence is the answer |
| `DistinctRefsAsync`, `LivePackMembersAsync`, `EntriesReferencingAsync`, `PackMembersAsync` | unchanged, over interval rows; a ref's rows are reachable by construction after retention runs |
| `RefsOnlyInAsync(versions, kind)` | refs whose every row becomes unreachable when those versions go (see Retention) |
| `CaseCollisionsAsync(v)`, `UnreadableAsync(v)`, `ImportIssuesAsync(v)`, `IsUnrecoverableAsync(v, path)` | interval filter; the last two keep their per-version tables |

A row is **reachable** when some retained version `r` satisfies `version_from <= r < version_to`.
After retention has run, every row is reachable; the dedup and ref queries rely on that and need no
version predicate, as today.

## Retention

Removing version V (`RemoveVersionAsync`):

1. Delete the row from `versions`.
2. Delete every `entries` and `dirs` row that no remaining version reaches:
   `NOT EXISTS (SELECT 1 FROM versions WHERE version >= version_from AND version < version_to)`.
   The `(version_to)` index bounds the candidates: a row with `version_to > max(versions)` is
   reachable and is never examined.
3. Delete V's rows from the per-version tables.

Removing the oldest version is the common case and touches only rows closed at or before it.
Removing a middle version leaves rows that span it untouched — they are still what the neighbours
looked like.

`RefsOnlyInAsync(versions, kind)` — the cloud objects that become garbage when those versions go —
is "refs of rows that step 2 would delete, minus refs that keep a reachable row". Computed as one
query before the deletion, as the cleaner asks it today.

## Repair patches

`ApplyPatchesAsync` writes per `(version, path)`: a storage rewrite, an unreadable stamp, an
unrecoverable mark or clear. On interval rows the row that covers `(V, path)` may span other
versions, and the patch must not leak into them. The write isolates the version first:

```
row [a, b) covers V:
  a == V and b == V+1 → update in place
  otherwise           → split into [a, V), [V, V+1), [V+1, b) (omitting empty pieces),
                        copying the old values into the outer pieces, then update the middle one
```

Splits are never merged back. A repaired path gains up to two rows; the count is bounded by repairs
made, not by versions.

## Legacy order

Since the M4 diff (two path-ordered cursors) a version's entries are emitted in `path_key` order, so
`seq` is derivable. Versions written before that may not be, and `Import_then_serialize_is_byte_
identical` is a promise the catalog keeps. The conversion checks each version: if its `seq` order
differs from its `path_key` order, the version gets rows in `entry_order (version, seq, path)`, and
`SerializeVersionAsync` follows that table when it exists for the version. A version imported later
from the cloud gets the same check on import. Most containers never have this table.

## Migration

A format-1 catalog is converted in place the first time it is opened for writing after the upgrade:

1. In one transaction: create the v2 tables beside the old ones under temporary names.
2. For each version in ascending order, in its own transaction: run the merge of "Writing a
   version" with cursor A over the old `entries WHERE version = v ORDER BY path_key` and cursor B
   over the new table's rows current at v−1; write the legacy order table if the version's `seq`
   order differs; copy the version's per-version rows.
3. In one transaction: drop the old tables, rename the new ones, set `user_version = 2`.
4. `VACUUM`, when the temp volume has room for a copy of the file; otherwise skip it and log —
   the file stays large but correct, and the next VACUUM (a later conversion, or an operator's)
   reclaims it.

Reads and writes are sequential; the cost is the old row count once. For 3/database (7 M rows) that
is minutes to tens of minutes, shown on its own stage line, **Upgrading catalog**, counted in
entries like Loading versions and placed before Checking catalog. Pause is greyed for it as for the
version load; Suspend and Stop interrupt the current version's transaction and the conversion resumes
from that version at the next open — the finished versions are in the new table, the format flag is
not yet set, and the merge is idempotent per version.

A conversion that fails for any other reason falls into the existing corrupt-catalog path: the file
is deleted with a warning and rebuilt from the cloud on demand (`VersionCatalogStore.RecoverAsync`).
That costs downloads, never data.

A read-only open of a format-1 catalog is treated as a miss, the way an unreadable file is today:
the reader falls through to the write path, which converts.

The check operation's `quick_check` runs as before, on the converted file.

## Failure and consistency

- Each import is one transaction: closes and inserts land together or not at all. A version is "in
  the catalog" exactly when its `versions` row exists, as today, so the probe logic is unchanged.
- The journal mode, `unix-excl`, `synchronous=NORMAL` and the write lock per container are unchanged.
- Dedup never sees uncommitted rows (WAL readers see the last commit).
- `HistoryRowsAsync` keeps returning the sum of `versions.entry_count`; `PrefersRebuild` stays for
  `EnsureVersionsAsync`'s bulk case and leaves the run's own import.

## Testing

- **Row mechanics:** insert, close, split; a path modified in three consecutive versions has three
  rows with abutting intervals; a deleted-then-re-added path has a gap.
- **Query parity:** a fixture of five versions with adds, modifications, deletions, renames and a
  case-only rename is imported into a v1 catalog (kept in the tests as the reference) and a v2
  catalog; every public query is asserted equal for every version. This is the test that lets the
  query rewrites be checked one by one.
- **Byte identity:** `SerializeVersionAsync` reproduces the imported `.idx` bytes for every version
  of the fixture, including one whose entries are deliberately out of path order.
- **Retention:** removing the oldest, a middle and the newest version leaves every other version's
  serialization unchanged; `RefsOnlyInAsync` names exactly the refs no surviving row carries.
- **Repair:** a patch on a spanning row changes the target version only; the split rows serialize
  identically for the untouched versions.
- **Migration:** a v1 file converts to a v2 file whose every version serializes identically;
  killing the conversion after version k and reopening completes it; a conversion error leaves the
  rebuild path to do its job.
- **Run integration (Azurite):** a second backup after a small change imports in a transaction that
  writes only the changed rows (asserted through the row count), and the content-keyed indexes stay
  in place throughout.

## Acceptance

From 3/database's first run on the new build: Updating catalog's total time, the converted
`catalog.db` size, and the size growth of the following run. The targets are minutes, about 1.3 GB,
and megabytes.
