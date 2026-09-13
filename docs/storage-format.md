# Storage format

What a backup looks like inside its Azure Blob container, what the local cache holds, and how the two
stay in step.

## Container layout

```
azurestoragebackup.index.json          # info file (unencrypted variant)
azurestoragebackup.index.json.enc      # encrypted variant — one or the other, never both in use
indexes/v{N}.json[.enc]                # second level: one file manifest per version
data/{address}[.001,.002,...]          # single-file data blobs, content-addressed
packs/{packId}.7z[.001,.002,...]       # grouped archives
restore-tmp/{name}                     # transient Hot copies of archived volumes during a restore
journal/                               # local only — see run-lifecycle.md; not in the container
index-cache/                           # local only — the SQLite catalog built from indexes/v{N}; not in the container
```

`restore-tmp/` exists only while a restore of Archive-tier data runs: the archived originals are
never rehydrated in place — each volume is Copy-Blobbed here at an online tier, downloaded, and the
copy deleted per group. Anything still under the prefix at process start is the leftover of a crash
and is swept (`RestoreTempSweeper`), since it bills online-tier storage for nothing. See
[check-restore-repair.md](check-restore-repair.md).

**A container holds at most one backup.** The info file's presence is what marks a container as
belonging to this tool.

The `.json` suffixes are historical: the contents are **compact binary**, not JSON. The schemas below
describe the logical structure.

## Two index levels

| Level | Where | Contents | Written |
|---|---|---|---|
| One | inside the info file | version number, timestamps, index blob reference, statistics | once per run |
| Two | `indexes/v{N}` | the full file manifest for that version | once, never rewritten |

> **Rationale.** A single index for a backup of hundreds of thousands of files would be enormous and
> would be rewritten on every run. Splitting by version means a new version writes only its own file.
> The exception is dead-weight compaction, which is deliberately designed so that it does **not** need
> to rewrite old indexes ([packing.md](packing.md)).

Both levels are compressed and, for an encrypted backup, encrypted.

## The info file

```jsonc
{
  "schemaVersion": 1,
  "backup": {
    "name": "...", "description": "...",
    "sourceRootHint": "/data/photos",     // advisory only; the user re-specifies on recovery
    "encrypted": true,
    "createdAt": "...",
    "settings": { /* a snapshot of the resolved settings in force for this backup */ }
  },
  "versions": [
    { "version": 1, "startedAt": "...", "createdAt": "...",
      "indexBlob": "indexes/v1.json.enc",
      "stats": { "files": 1200, "bytes": 3.4e9, "changedFiles": 12, "changedBytes": 5e7 } }
  ],
  "packs": {
    "p0001": { "blob": "packs/p0001.7z", "members": ["..."],
               "originalBytes": 900000, "deadBytes": 0, "volumes": 1, "volumeSizes": [...] }
  }
}
```

It is the authoritative metadata for recovery on another machine: configure the account, pick the
container, and everything except device-local settings is restored from it.

**Version timestamps.** `CreatedAt` means *committed*, i.e. when the backup ended; `StartedAt` is
taken at the entry to the run, before scanning. Versions written before `StartedAt` existed have
null.

> **Rationale — why the completion notice does not use the runner's own clock.** Post-run cleanup
> (retention, compaction) keeps going for a while after the version is committed, so the run's end
> time is several minutes later than the version record. Reading from the runner would make the
> notice say 14:47 while the restore dialog says 14:44 for the same backup. Both read the two times
> in the version record instead.

## The version index

```jsonc
{
  "version": 1,
  "entries": [
    { "path": "sub/a.txt", "kind": "file",
      "length": 123, "mtime": "...", "permissions": "0644",
      "headHash": "xxh128:...", "fullHash": "xxh128:...", "tailHash": "xxh128:...",
      "unreadableAt": null,
      "storage": { "kind": "blob", "ref": "data/...", "raw": false,
                   "volumes": 1, "volumeSizes": [123] } },
    { "path": "sub/small.txt", "kind": "file", "length": 40,
      "headHash": "...", "fullHash": "...", "tailHash": "...",
      "storage": { "kind": "pack", "ref": "p0001", "entryName": "sub/small.txt" } }
  ],
  "emptyDirs": ["sub/empty1", "sub/empty2"],
  "unrecoverablePaths": []
}
```

Every entry carries permissions, mtime, length and all three hashes — the diff compares against them
and restore reapplies them. A symlink entry carries `kind: "symlink"` and a `target` instead of
content.

| Field | Purpose |
|---|---|
| `headHash` / `tailHash` / `fullHash` | change detection and dedup — [content-identity.md](content-identity.md) |
| `unreadableAt` | this round could not re-read the file; content carried over — [backup-engine.md](backup-engine.md) |
| `storage.raw` | the blob holds source bytes with no 7z wrapper |
| `storage.volumes` / `volumeSizes` | how many blobs this reference spans, and how large each is |
| `emptyDirs` | recreated by restore; nothing else records them |
| `unrecoverablePaths` | repair could not fix them — [check-restore-repair.md](check-restore-repair.md) |

**Volume counts live in the version index for blobs and in the info file for packs.** Compaction can
change a pack's volume count, which updates the info file rather than any version index.

> **Rationale — why per-volume sizes are recorded.** They let a check verify existence *and* size
> with a `HEAD` per blob, catching truncation and wrong blobs without downloading anything.

## Addressing

A data blob's name is derived from its content, so identical content produces identical addresses and
dedup is free.

| Backup | Address |
|---|---|
| Unencrypted | `data/{fullHash}` |
| Encrypted | `data/{HMAC(key, fullHash)[:16]}`, `key = HKDF(password, KdfSalt)` |

For an encrypted backup the collision metadata is likewise an opaque `HMAC(key, fullHash|length|head|tail)`,
leaking neither length nor header.

> **Rationale.** Someone who can list the container must not be able to take a publicly known file's
> hash and decide whether it was backed up. Dedup is unaffected — same content, same address. The
> residual leak is blob count and blob sizes.

Only the orchestrator uses the key when creating blobs; restore, check and cleanup all use the
address recorded in the index.

**Collision avoidance.** When different content resolves to an address already taken, the newcomer
steps aside to `data/{address}~1`, `~2`, and so on, and the actual name goes into `storage.ref`. This
raises an unrecoverable-error notification, because at 128 bits it should not happen.

**Volumes.** A blob or pack too large for one object is split: `.001`, `.002`, … A single-volume
family uses the base name with no suffix. `VolumeBlobIO` treats a family as a unit for read, write
and cleanup alike.

Every volume carries its own xxh128 in blob metadata — `x-ms-meta-xxh128`, value
`xxh128:<32 hex>` — written **with** the upload request so the label commits atomically with the
bytes it describes. A volume that fits its upload stream's share of the global upload memory limit
is hashed and sent from one in-memory read; a bigger one is hashed from disk and re-read for the
send (two-pass); the raw route supplies the hash it already holds. The label's only consumer is the upload
path's skip decision — resume and repair verify a cloud volume in place instead of re-sending it;
check never reads it. Legacy volumes carry none and therefore always read as "different". So do the
volumes of an **encrypted** backup, which carry none on purpose: 7z's random IV makes every encrypted
archive different bytes, a label could never match, and computing one would only cost the memory or
the second read — they stream from disk unhashed. The full argument is
[volume-identity.md](volume-identity.md).

> **Rationale — why `.001` is not written last as a completeness marker.** It used to be, as an "the
> family is complete" signal, and it was dropped together with cloud-side existence dedup: it doubled
> upload time for 2–5 volume files, and dedup no longer asks the cloud anything.

## Serialisation

`IndexSerializer` writes a compact custom binary format over `BinaryWriter`, then compresses it.

> **Rationale — why not JSON.** Size. Hashes are stored as 16 raw bytes rather than `xxh128:` plus
> hex text, with fixed-width encoding for enums, times and lengths. The public API is a byte-array
> round trip, so backup, restore and blob storage were unaffected by the change.

Both the info file and the index carry a format number, and new fields are added by bumping it and
reading conditionally. Old files still read.

> **The upgrade is one-way.** Reading a format newer than the running build throws, so once a newer
> image has written the info file, an older image can no longer read it. That is fine for a rolling
> upgrade on a single instance, but there is no rolling back afterwards.

## Atomicity

- **Data and pack blobs are content-addressed**, so uploading is idempotent: upload data first, then
  update the index, and re-uploading is equivalent.
- **Second-level indexes are never overwritten.** A new version writes a new file.
- **The info file is written to a temporary blob and then overwritten on success**, with `If-Match`
  against the recorded ETag. A network failure leaves the old file intact; an external change is
  detected rather than silently lost.

## The local cache

The container is expensive to read — data may sit in Cold or Archive, where reads cost money — so
local state is authoritative during normal operation.

| Where | Holds |
|---|---|
| `LocalBackupState` (SQLite) | a serialised copy of the info file plus its cloud ETag |
| `index-cache/{accountId}/{container}/catalog.db` (beside `app.db`) | every retained version's index entries, as queryable rows |

### The catalog

Version indexes are queried, never resident. The catalog exists because they used to be deserialised
whole into a process-wide cache: two indexes resident at a time, the dictionaries a run built out of
them, the scan's list and the new version's entries — every one of them a structure whose size is the
file count. At several million files that came to gigabytes, and an idle process never collects, so a
container that had finished or suspended a backup hours earlier was still holding all of it. Every
question those structures answered is an indexed lookup here, and what a run holds in memory is
bounded by the pipeline's width rather than by the size of the backup — see
[operations.md](operations.md) § *Memory* for what that costs and what it now measures.

One SQLite file per (account, container), in WAL mode, opened directly through
`Microsoft.Data.Sqlite` with pooling off and entirely separate from `app.db` — no EF context, no
migrations. On Unix it is opened through SQLite's `unix-excl` VFS (the data source is
`file:…/catalog.db?vfs=unix-excl`), which keeps the WAL index in the process's heap instead of a
memory-mapped `catalog.db-shm` file and takes no byte-range locks on such a file; connections inside
the process still share the index and run concurrently, but the file is this process's alone while
it is open — which it always was. The reason is in [history.md](history.md) (2026.9.8.2): a NAS
kernel refused the `-shm` locks for no visible cause, and there is nothing the catalog needs from a
file that exists to share an index between processes. One consequence: the connections that only
read are opened `ReadWrite` at the SQLite level, because a `ReadOnly` handle is excluded from
`unix-excl` and would bring the `-shm` file back for every connection on the file.

Beside it, `catalog.db.open` is the clean-shutdown marker: a write open creates it, and disposing the
store at host shutdown removes it. Found at the next start, it says the last process that wrote this
catalog did not exit cleanly, and the first write open logs one line to that effect. It used to make
that open pay the full-file `PRAGMA quick_check` as well; since 2026-09-13 it does not. SQLite's
write-ahead log covers a kill or a crash on its own — an interrupted import's 2.5 GB of uncommitted
frames was discarded cleanly on the next open — while the read cost 37 minutes on an 8 GB catalog
with the run standing still, after every `docker stop` that landed mid-import. The full check is owed
only once a reader has hit `SQLITE_CORRUPT` on the file (the backup's "Checking catalog" stage, which
then runs ahead of the version load), and the check operation runs it on purpose every time.

It holds:

- `versions` — one row per version the catalog knows: the version number, its identity stamp, its
  entry count and when it was imported.
- `entries` — **one row per path per change**, not one per path per version. A row carries the
  entry's own columns (kind, length, mtime, permissions, the three hashes, symlink target, unreadable
  mark, storage reference, the unrecoverable flag) plus three derived ones — `parent`, the directory
  part of the path; `path_fold`, the upper-cased path; and `path_key`, the path's UTF-16 big-endian
  bytes, whose byte order is ordinal string order — together with the half-open version interval
  `[version_from, version_to)` over which that entry is what the path looked like. `version_to` is
  `2147483647` while the entry is still current. A path untouched across fifty versions is one row; a
  path rewritten at every version is one row per version. `(path, version_from)` is unique: a version
  starts at most one row per path.
- `dirs` — every directory implied by an entry path, plus every empty directory, on the same interval
  rule (a directory is current while anything under it is), so listing one level of the tree is a
  point lookup on `(parent, path_key)` rather than a scan of the subtree.
- `empty_dirs` — the directories the version records as empty, in the order the index lists them.
- `unrecoverable` — the paths a check or a repair declared beyond recovery, likewise in index order.
  The same fact is mirrored in an `entries.unrecoverable` flag, written in the same transaction, so
  the flag and the list cannot disagree about a path. The flag is part of the row, so setting or
  clearing it is a change like any other and opens a new interval.
- `import_issues` — what an import had to drop rather than store. Today that is one case: a version
  whose index lists the same path twice, which the merge cannot keep, since a version starts at most
  one row per path. The first entry wins and the loss is recorded, because it means that version can
  no longer be serialised byte-identically.
- `entry_order` — the serialisation order of a version whose entries did **not** arrive in path
  order. Empty for almost every container; see *Legacy order* below.

`PRAGMA user_version` carries the file format. 2 is the layout above; a file at 0 is the previous one
— one row per version per path, with a `seq` column — and is converted in place on its first write
open (*Converting a format-1 catalog*, below).

> **Why a row spans versions.** The old layout stored every entry of every retained version as its
> own row, indexed nine ways, and two consequences were measured on the largest container in the
> field (1.1 M files, 15 versions, 2026-09-12/13). *Per-run work was proportional to the file count,
> not to what changed*: a version with 2,675 changed files inserted 1,111,050 rows, and into the
> three content-keyed indexes — keyed by hash, ref and length, so a new version's rows land at random
> over the whole history — that was 1.2 GB/min of disk reads for over five hours on an 8 GB file,
> with the run standing still. *Size was linear in versions × files*: 10.2 GB for 15 versions, about
> 1 GB per version, on a retention policy that keeps up to 100 — and every full read of the file, the
> operator's check and the diff's cursor included, pays for that. The per-row weight had its own
> cause: the table was `WITHOUT ROWID` with `(version, path)` as its primary key, and SQLite copies
> the whole primary key into every secondary index as the row pointer. Measured on a synthetic
> 200,000-row catalog with the production schema and 68-character paths: 1,765 bytes per row, 1,150 of
> them in the eight secondary indexes, each carrying a path copy. An interval row on an ordinary rowid
> table answers both — the indexes carry an 8-byte pointer, and a version costs its changes. The hash
> columns stayed TEXT: `EntryRowMapper` is shared with the run's work database, and a catalog-only
> BLOB encoding would be a second definition of the same row for about 100 bytes a row against the
> 1,150 the key change already removes.

The secondary indexes are the point of the file — each one exists because something asks it a
question that would otherwise be a scan of every entry of every version:

| Index | Who asks |
|---|---|
| `(path, version_from)`, unique | "what did this path look like at version N?", one path at a time |
| `(path_key)` | the diff cursor, which streams a whole version in ordinal path order, and the serialiser |
| `(parent, path_key)` on `entries` | the files in one directory, when browsing one level of the tree |
| `(parent, path_key)` on `dirs` | the subdirectories of that same directory, the other half of that listing |
| `(path_fold)` | restore's case-collision check |
| `(full_hash, length)` | dedup: "is this content already in the cloud?" |
| `(storage_ref, storage_kind, path_key)` | collision avoidance ("is this address taken?"), retention ("does any retained version still reference this blob?"), and grouping a restore's or a check's downloads by the object they live in |
| `(length, head_hash)` | the prescreen, before a full hash is paid for |
| `(version_to)` on `entries` and on `dirs` | retention's deletion of the rows no retained version can reach |

The three derived path columns cost about 200 bytes a row between them, against the 1,150 the primary
key used to. `path_key` is not a spelling of `path`: it is the ordinal order both diff cursors walk
in, and SQLite's `BINARY` collation on a TEXT column compares UTF-8 bytes, which disagrees with
ordinal order the moment a surrogate pair appears.

**Writing a version is a merge, not an insert.** The run reads back the `.idx` file it just uploaded
— that is what makes "the catalog holds exactly what the container holds" true by construction — and
walks it against the rows the catalog already holds, both sides in `path_key` order:

| At the two cursors | What is written |
|---|---|
| the same path, the same entry | nothing |
| the same path, a different entry (any column, storage and the unrecoverable flag included) | the covering row is closed at this version; the new entry goes in as a row starting here |
| a path only the index has | a row starting here |
| a path only the catalog has | the covering row is closed at this version |

Both sides are read sequentially, and what is written is the version's changes. "A different entry"
is field-by-field over every field the wire format carries, defined next to the column list it
belongs to (`EntryRowMapper.SameEntry`), because a field the comparison missed would be a change the
catalog silently dropped.

A row starting here ends at the **next version the catalog already holds** — at the sentinel, when
this is the newest, which is the ordinary case. A version imported into a gap therefore describes
itself and whatever versions after it nobody has imported yet, and nothing beyond. For the same reason a replacement never reaches further than the row it replaces
— a row that ended at a since-removed version says the path stopped there, and widening it would make
re-importing that version find the path present and unchanged rather than absent. And a row being
closed that reached past that next version keeps its tail, re-inserted as its own row: those later
versions still see the old entry, and only the stretch this version speaks for may change.

Re-importing a version already in the catalog (a repair rewrote its index) is "remove, then import",
inside the one transaction; the rows other versions still reach survive that removal untouched.

The whole import is one transaction, so its closes and its inserts land together or not at all, and a
version is "in the catalog" exactly when its `versions` row is there — the probe logic is unchanged.
Entries that do not arrive in path order — an in-memory import, an index written before the M4 diff
— are staged in a temp table, sorted by `path_key` and merged from there, so no caller has to promise
an order it does not control; the order it did give is written to `entry_order` only when it really
differs from path order.

**Reading is the interval predicate plus a guard.** Every query that used to say `WHERE version = @v`
now says `version_from <= @v AND version_to > @v AND EXISTS (SELECT 1 FROM versions WHERE version=@v)`.
The guard is not decoration: without it an open-ended row would report the newest version's content
under any later number, and a gap retention left behind would report its predecessor's — a version the
catalog does not hold has to answer nothing, exactly as it did when every row named one version. The
dedup, collision-avoidance and prescreen lookups carry no version predicate at all, as before: a row's
existence is the answer, and after retention has run every row is reachable from some retained
version. The two questions repair asks by ref — which versions point at this object, which versions
hold this pack member — join `entries` back to `versions` on the interval, which expands one row into
the one-row-per-version shape repair reads.

**Retention deletes what nothing reaches.** Removing a version drops its `versions` row first, so
reachability is judged against what remains, then deletes every `entries` and `dirs` row no remaining
version lies inside. `version_to <= max(versions)` bounds the candidates off the `(version_to)` index,
so a row spanning the newest retained version is never examined; then the version's rows go from the
per-version tables. Retiring the oldest version, the ordinary case, touches only rows closed at or
before it; retiring one in the middle leaves the rows that span it alone, because they are still what
the neighbours looked like. Only a removal that lowers the highest retained version can leave rows
*starting* above it, and only then is the extra scan for them run — there is no index on
`version_from`. `RefsOnlyInAsync`, which tells the cleaner which cloud objects become garbage when a
set of versions goes, is the same reachability question asked per ref, in one query before the
deletion.

**A repair patch isolates its version first.** A patch names one `(version, path)` — a storage
rewrite, an unreadable stamp, an unrecoverable mark or its removal — and the row that covers it may
span other versions the patch must not leak into. So the row is split into up to three: the piece
before the version, the piece for the version, the piece after; the outer two copy the old values
under new ids and the middle one takes the patch. The middle piece ends at the next retained version,
or where the row already ended if that is sooner — never at `version + 1`. A piece covering a version
that does not exist would be reachable by nothing, would survive retention (which only deletes what
no version reaches) and would still answer the content lookups, which carry no version predicate.
Splits are never merged back; a repaired path gains at most two rows per patch, so the count is
bounded by repairs made rather than by versions.

**Legacy order.** Since the M4 diff (two path-ordered cursors) a version's entries are emitted in
`path_key` order, so the position an entry had in the index stream is derivable and is no longer
stored per row. Versions written before that may not be, and "import then serialise is
byte-identical" is a promise the catalog keeps — a repair rewrites a version by serialising the
catalog rather than the file it downloaded, and a resorted copy is the same information in different
bytes. So an import that sees an order other than path order writes that version's order into
`entry_order`, and the serialiser follows that table for the versions that have one. Most containers
never get a row in it.

> **Why this is not rows in `app.db`.** Version indexes used to be one row each in
> `CachedVersionIndex`, and one row can be 100 MB for a backup of half a million files. SQLite
> permits a single writer at a time (WAL only stops readers and the writer from blocking each
> other), so committing an index held the application database's write lock for the whole write. On
> a loaded disk that was tens of seconds during which nothing else could write: the log-retention
> sweep failed with `database is locked`, and editing a backup in the UI appeared to do nothing. A
> separate file has its own write lock, and nothing on the Settings page waits behind it.

**Lazy migration.** A version is pulled into the catalog the first time something needs it, never
eagerly, from whichever of the older homes still has it:

1. the catalog's own row, if its identity matches — the case every run after the first takes;
2. the `{version}.idx` file an older build left in the same directory;
3. the legacy `CachedVersionIndexes` row in `app.db`;
4. the cloud.

Each source is streamed into the catalog inside one transaction that verifies the identity and the
entry count, and a transaction that fails rolls back, so a half-imported version is never left
behind. What happens to the source it came from depends on why it was not used:

- An `.idx` file whose identity does not match the version being asked for is a plain miss and is
  left alone — clearing it away belongs to retention and to removing the container, not here.
- An `.idx` file whose identity matches but whose body cannot be parsed (truncated by a crash
  mid-write, say) is deleted: the rolled-back import gained nothing from it, and left in place it
  would fail the same way on every later attempt. The next source is tried.
- An `.idx` file that imports cleanly is deleted once that transaction commits.
- A legacy `CachedVersionIndexes` row is dropped as soon as it has been looked at, whether or not it
  was imported: a row under a superseded identity is dead weight when the catalog is about to go to
  the cloud for the current one anyway.
- The cloud is the last resort, and is never deleted from.

A container nobody has touched since the upgrade pays for no migration at all.

**A whole history at once.** Every consumer that is about to ask the catalog about all of a
container's retained versions (a backup run before its diff, a check's reference set, a repair,
retention, the deferred-repair sweep) asks for them in one call, `EnsureVersionsAsync`, rather than
one version at a time. One read-only probe settles the versions already there. When two or more are
missing — the first run after the upgrade, migrating a container's whole history — the three
content-keyed indexes (`entries_content`, `entries_ref`, `entries_head`) are dropped for the
duration and rebuilt with one `CREATE INDEX` each once the last version is in. Those three are the
ones keyed by content rather than by path — content hash, storage ref, length — so an imported
version's rows land at random across the whole history, and keeping them up row by row means a
random page read per row per index over a tree that spans every version already in. Measured with a
64 MiB page cache: importing ten versions read 1.2 GB of pages with the three live and one page
without them; on the NAS the same migration showed up as 30 GB of block reads (ZFS serves a 4 KiB
page from a 128 KiB record) and hours of wall time for a single container. The rebuild is a sort and
a sequential write per index. A single missing version takes the same bracket only when it is at
least a hundredth of the rows already in the catalog (`VersionCatalog.PrefersRebuild`); a hundred is
the ratio between the two per-row costs — microseconds a row for the sort, a millisecond or more for
a random page on a NAS. The history's size comes from the versions table's declared counts, not from
a count over entries, so asking is free.

**The run's own import takes no bracket.** The version that has just been committed is recorded
through the same code (`ImportIntoCatalogAsync`, the Updating catalog stage), and it leaves the three
indexes standing: on this format a version inserts only its changes into them — a few thousand rows
where it used to be the container's whole file count — so dropping and re-sorting them would cost far
more than the inserts. The bracket is left to the places where a whole history goes in at once:
`EnsureVersionsAsync`'s migration above, and the format conversion below.

A migration that stops halfway (a stop pressed, a crash, one version whose index blob cannot be
read) leaves the versions that did import and no content-keyed indexes; the catalog is slower to
query until the next write open, whose schema pass (`CREATE INDEX IF NOT EXISTS`) puts them back.
It is never wrong: the indexes are derived from the rows, and no query depends on their presence.

**Converting a format-1 catalog.** A file at `user_version` 0 is the old one-row-per-version-per-path
layout, and it is converted in place, without downloading anything, by the first write open that
finds it:

1. In one transaction: the old indexes are dropped and all six old tables are renamed aside to
   `v1_versions`, `v1_entries`, `v1_dirs`, `v1_empty_dirs`, `v1_unrecoverable`, `v1_import_issues`; a
   bookkeeping table `upgrade_done` is created; and the v2 schema is created under the real names.
   `versions` goes aside with the rest, and that is the step the whole conversion turns on — the merge
   bounds a version's rows at the next version the `versions` table already lists, so with every old
   version still listed, version *k*'s rows would end at *k+1*, version *k+1* would find nothing
   current to compare against, and the result would be the format-1 shape written into the format-2
   tables: correct, and not one row smaller. The renames carry no `IF EXISTS`, because format 1's own
   schema pass created all six on every write open; a file missing one is not a format-1 catalog, and
   failing here is the right answer.
2. The three content-keyed indexes are dropped for the whole conversion, for the reason above — every
   version would otherwise insert its rows into them at random over a history growing underneath it.
3. Each version the `upgrade_done` table does not yet name, in ascending order, in a transaction of
   its own: the version's old rows (read off `v1_entries` in `path_key` order) are merged against the
   new table's rows current at the version before it — the same merge a run's import takes — its
   legacy order is written if its `seq` order is not its path order, its import issues are copied, and
   its `upgrade_done` row goes in. One transaction per version is what makes the conversion resumable
   at a version boundary.
4. The three indexes are rebuilt, before the stamp: a file that reads as converted has its indexes.
5. In one transaction: the `v1_*` tables and `upgrade_done` are dropped and `user_version` is set to
   2. **The stamp is the last thing written**, so a file without it is resumed rather than trusted.
6. `VACUUM`, to reclaim the old rows' pages.

Reads are sequential over the old rows and the cost is the old row count, once — minutes to tens of
minutes for a history of millions of rows. In a backup run it stands on a stage line of its own,
**Upgrading catalog**, ahead of the catalog check and the version load, because that is where an
operator expects to be told ([progress-display.md](progress-display.md)); every other write open
converts silently. Suspend and Stop end the run between versions, and the next open resumes from the
first version `upgrade_done` does not name: the finished versions are committed, the file is not
stamped, and the merge is idempotent per version. A read-only open of a format-1 file is refused, so
the reader falls through to the write path, which converts and then opens again.

The `VACUUM` is the one step that can fail on a catalog that is already converted, stamped and
correct — no temp room for a copy of the file, a temp volume it cannot open, a NAS I/O error, a second
writer. Every one of those is recorded and logged rather than thrown, because throwing would put the
conversion on the failure path below and have the store delete a finished catalog to re-download what
is already on disk. The file stays large and correct until the next `VACUUM` — a later conversion's,
or an operator's. A conversion that fails for any other reason *does* take that path: it is reported
as a catalog that could not be converted, and the file is deleted with a warning and rebuilt from the
cloud on demand. That costs downloads, never data.

**The catalog is derived data.** The index blobs in the container are the recovery copy; the catalog
is a queryable copy of them and can be rebuilt from them at any time. That is what lets it run with
`synchronous=NORMAL`, and it is what makes corruption a cache miss rather than an incident: a
reader that hits `SQLITE_CORRUPT` makes the next write open run `PRAGMA quick_check`, and a file that
fails it — or that SQLite refuses as `SQLITE_CORRUPT` or `SQLITE_NOTADB` outright — is deleted with a
warning and rebuilt from the cloud on demand. That holds on the read path too: the cheap read-only probe every
reader starts with treats an unreadable file as a miss and falls through to the write path, which is
where the rebuild happens — and if the probe is the one that finds the corruption, on a path this
process already opened for writing earlier, it forgets that earlier pass so the write open re-runs
the check instead of trusting it. Deleting the whole `index-cache/` directory costs downloads, never
data. In a backup run an owed `quick_check` runs on its own stage before the version load — the
first thing that opens the catalog for writing — so its cost never hides inside another stage's open.

**The long operations run on their own thread.** Microsoft.Data.Sqlite is synchronous underneath,
so a million-row import, a `CREATE INDEX` over the history or a full-file `quick_check` holds its
thread for minutes; on a thread-pool thread that was one worker gone for the duration, and Kestrel
logged thread-pool starvation through the whole of a 2026-09-13 run. Those three start on a
long-running task's dedicated thread (`VersionCatalog.OffThePoolAsync`) and, since nothing inside
truly yields, stay there to the end.

**One writer per container.** Every writer — a run's finish, retention, the check's and the
repairer's marks — takes the container's write lock for the duration, and the write open demands
that lock as an argument, so "the caller holds it" is checked by the compiler rather than promised
in a comment. The rebuild above therefore takes no lock of its own: the caller's is already what
keeps a second writer from deleting the same file at the same moment. Readers open their own
connections and, under WAL, never wait for the writer. A `VersionCatalog` wraps a single connection
and is not thread-safe, so a caller that needs two cursors at once (the run opens one for the diff
and one for dedup) opens two.

**The info file decides which versions exist.** A run reconciles the catalog against it before it
asks the catalog anything, and so does retention: every version in the catalog that the info file
does not list is removed. This is not tidiness. Dedup, collision avoidance and the prescreen all
query the catalog without a version predicate, and retention deletes a retired version's
exclusively-owned blobs from the cloud *before* it drops that version's rows — so a cleanup
interrupted in between (a Stop, a shutdown, one 5xx) leaves a catalog whose extra version points at
blobs the container no longer holds, and without the reconcile the next run would hand a new file
one of those addresses and record it as backed up.

**A patch that cannot be written invalidates the version.** The check and the repair upload the
rewritten index and commit the info file before they record the same marks in the catalog. If that
last write fails, the version is dropped from the catalog instead of being left with pre-mark rows
under an unchanged identity — which nothing would ever re-import — so the next reader migrates it
back from the cloud, marks and all.

A version index is immutable once written, so a cache hit is valid by construction. The identity
stamp — the backup's creation timestamp — detects a container deleted and recreated, where version
numbers get reused but the contents differ; a mismatch re-imports from the cloud.

**Net effect: outside import, deep check and repacking, a backup performs zero cloud reads of the
info file and version indexes.** Only when no local copy exists (a first run, before import) is the
cloud read and the copy backfilled.

**Import downloads every version index into the catalog**, after which routine backups and cleanups
download nothing.

The catalog stores **decrypted** index metadata — paths and hashes — consistent with the threat model
behind keyed addressing: the attacker has cloud list access only, while this machine is trusted and
holds the source files anyway.

> **Single-writer assumption.** Genuinely concurrent writers from two machines are not handled. The
> ETag turns that rare case into a clean abort and resync rather than lost history.

## The run's work database

A backup run keeps its own scratch database at `{tempPath}/work/{runId}.db` — `synchronous=OFF`, one
writer task fed by a channel, readers on their own connections, and, like the catalog, opened through
`unix-excl` so that no `-shm` file is involved. It holds everything about a run that
grows with the file count: the scan's rows, the draft of the new version (one row per path, carrying
the diff's verdict, the previous version's entry beside it, and the storage, tail hash and identity
the run settles on later), the content this run has already uploaded so a second file with the same
content is deduplicated against it, and the journal's records read back at the start of a resume.

One side file sits next to it, `{tempPath}/work/{runId}.aliases.db`: the run's "which path saw this
content first" table for cross-pack dedup, a database of its own rather than a table in `work.db`
because it holds one write transaction open across thousands of claims and the work database's single
writer must never queue behind that. It is created, swept and deleted on exactly the same terms.

It is deleted on every exit — Completed, Failed, Canceled and **Suspended** alike. Nothing in the
draft has reached the cloud index, so discarding it is correct, and the resume does not need it:
**the journal, not the work database, is the source of a resume** ([run-lifecycle.md](run-lifecycle.md)).
A file left behind by a killed process is named after its run, so the next startup sweeps it.

## See also

- [content-identity.md](content-identity.md) — what the hash fields mean and how they are used
- [backup-engine.md](backup-engine.md) — when each of these is written
- [packing.md](packing.md) — `PackInfo`, entry names and compaction
- [check-restore-repair.md](check-restore-repair.md) — reading it all back
- [run-lifecycle.md](run-lifecycle.md) — the journal, and what a resume actually reads
