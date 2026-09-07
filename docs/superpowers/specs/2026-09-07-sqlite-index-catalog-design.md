# SQLite index catalog: memory independent of file count

Date: 2026-09-07. Status: approved in discussion, awaiting spec review.

## Problem

A backup of several million files across 10+ versions drives the container to 8.4 GB of anonymous
memory, and that memory stays resident after the run is Suspended because an idle process never
collects. On the deployment host (QNAP QuTS hero, 64 GB, ZFS ARC ~18 GB, a 3 GB VM) that is enough
to push the machine into swap; every filesystem access on the NAS then goes to disk until the
container is stopped. Measured on 2026-09-07: `RssAnon` 8.36 GB at 0.01 % CPU, host swap 17 GB in
use, page cache 454 MB.

The memory is mostly live data whose size is proportional to the file count:

| Structure | Where | Size at 3M files |
|---|---|---|
| `VersionIndexMemoryCache`, 2 resident indexes | LocalIndexCache | ~2.2 GB |
| Scan result list | LocalFileScanner | ~0.6 GB |
| New version's entry list plus three path-keyed dictionaries | BackupOrchestrator.BuildEntries | ~1.1 GB, grows during the run |
| Dedup maps built from every retained version | LocalDedupResolver.Build | ~0.7 GB, plus one transient deserialized index (~1.1 GB) |
| Journal records of a suspended run | JournalResume | proportional to uploads so far |

No code path touches the NAS after Suspended; that was audited first and ruled out.

## Goal

Peak container memory under 1 GB during a backup run of several million files, with no structure
left whose size grows with the file count. Everything durable stays as it is: the cloud index
format, the info file, the journal and its suspend marks, `app.db`. A run suspended by the current
release resumes on the new one.

## Non-goals

- Changing the cloud index format, addressing, encryption, volumes or tier.
- Uploading the SQLite catalog anywhere. The cloud index is the durable copy; the catalog is
  derived from it and can be rebuilt from it at any time.
- Supporting a downgrade to a release without the catalog. Migrated `.idx` files are deleted.
- Archive-tier rehydration behavior. Downloading an index that is not in the catalog behaves as
  today.
- Streaming the scan's directory recursion or the progress ledgers; both are bounded by depth
  and pipeline width, not by file count.

## Storage layout

### `catalog.db`, one per (account, container), long-lived

Path: `/data/index-cache/{accountId}/{container}/catalog.db`, beside where the `.idx` files are
today. WAL journal mode. Raw `Microsoft.Data.Sqlite`, `Pooling=false`, entirely separate from
`app.db` and its EF context.

```sql
CREATE TABLE versions (
  version      INTEGER PRIMARY KEY,
  identity     INTEGER NOT NULL,   -- BackupInfoFile.Backup.CreatedAt.UtcTicks, as the .idx check today
  entry_count  INTEGER NOT NULL,
  imported_at  TEXT    NOT NULL
);

CREATE TABLE entries (
  version       INTEGER NOT NULL,
  path          TEXT    NOT NULL,
  parent        TEXT    NOT NULL,   -- directory part of path, '' at the root
  kind          TEXT    NOT NULL,
  length        INTEGER NOT NULL,
  mtime         INTEGER NOT NULL,   -- UTC ticks
  perms         TEXT    NOT NULL,
  head_hash     TEXT,
  tail_hash     TEXT,
  full_hash     TEXT,
  target        TEXT,
  unreadable_at INTEGER,
  storage_kind  TEXT,
  storage_ref   TEXT,
  entry_name    TEXT,
  volumes       INTEGER NOT NULL DEFAULT 1,
  raw           INTEGER NOT NULL DEFAULT 0,
  volume_sizes  TEXT,               -- JSON array, read only by the restore estimator
  PRIMARY KEY (version, path)
) WITHOUT ROWID;

CREATE INDEX entries_parent  ON entries (version, parent);
CREATE INDEX entries_content ON entries (full_hash, length);
CREATE INDEX entries_ref     ON entries (storage_ref);
CREATE INDEX entries_head    ON entries (length, head_hash);

CREATE TABLE dirs          (version INTEGER, path TEXT, parent TEXT, PRIMARY KEY (version, path)) WITHOUT ROWID;
CREATE INDEX dirs_parent ON dirs (version, parent);
CREATE TABLE empty_dirs    (version INTEGER, path TEXT, PRIMARY KEY (version, path)) WITHOUT ROWID;
CREATE TABLE unrecoverable (version INTEGER, path TEXT, PRIMARY KEY (version, path)) WITHOUT ROWID;
```

`dirs` holds every directory implied by an entry path plus every empty directory, so browsing
one level of the tree is two point lookups on `(version, parent)` rather than a subtree scan.

There is no separate dedup table. Dedup, collision avoidance and the head prescreen are point
queries on the three secondary indexes; a version's rows arrive when the version is written and
leave when retention deletes it, so the catalog cannot drift from the indexes.

### `work.db`, one per backup run, deleted when the run ends

Path: `/temp/work/{runId}.db`. `synchronous=OFF`, WAL. Deleted on Completed, Failed, Canceled
and Suspended alike: the journal, not the work database, is the source of a resume.

```sql
CREATE TABLE scan (
  path TEXT PRIMARY KEY, kind TEXT NOT NULL, length INTEGER NOT NULL, mtime INTEGER NOT NULL,
  perms TEXT NOT NULL, target TEXT
) WITHOUT ROWID;

-- The new version as it settles. Same columns as catalog.entries minus version, plus state.
CREATE TABLE draft (
  path TEXT PRIMARY KEY, parent TEXT NOT NULL, kind TEXT NOT NULL, length INTEGER NOT NULL,
  mtime INTEGER NOT NULL, perms TEXT NOT NULL, head_hash TEXT, tail_hash TEXT, full_hash TEXT,
  target TEXT, unreadable_at INTEGER, storage_kind TEXT, storage_ref TEXT, entry_name TEXT,
  volumes INTEGER NOT NULL DEFAULT 1, raw INTEGER NOT NULL DEFAULT 0, volume_sizes TEXT,
  state INTEGER NOT NULL               -- pending / confirmed / unreadable, for the finish's sanity check
) WITHOUT ROWID;

-- Content this run has already finished: replaces LocalDedupResolver._run for completed items.
CREATE TABLE reservations (
  content_key TEXT PRIMARY KEY, ref TEXT NOT NULL, raw INTEGER NOT NULL,
  volumes INTEGER NOT NULL, volume_sizes TEXT
) WITHOUT ROWID;
CREATE TABLE reserved_heads (head_key TEXT PRIMARY KEY) WITHOUT ROWID;   -- replaces _runHeads ("length\nhead")

-- The journal's records, one row per record: replaces JournalResume's dictionaries.
CREATE TABLE resume_blobs (
  ref TEXT PRIMARY KEY, kind TEXT NOT NULL, path TEXT, full_hash TEXT, head_hash TEXT, tail_hash TEXT,
  length INTEGER NOT NULL, raw INTEGER NOT NULL, mtime INTEGER, store_only INTEGER NOT NULL,
  volumes INTEGER NOT NULL, volume_sizes TEXT
) WITHOUT ROWID;
CREATE INDEX resume_blobs_path    ON resume_blobs (path);
CREATE INDEX resume_blobs_content ON resume_blobs (full_hash, length);

CREATE TABLE resume_packs (
  ref TEXT PRIMARY KEY, members_key TEXT NOT NULL,   -- canonical hash of the member list, the comparison FindPack makes today
  volumes INTEGER NOT NULL, volume_sizes TEXT
) WITHOUT ROWID;
CREATE INDEX resume_packs_members ON resume_packs (members_key);
CREATE TABLE resume_pack_members (
  pack_ref TEXT NOT NULL, path TEXT NOT NULL, entry_name TEXT NOT NULL, full_hash TEXT NOT NULL,
  length INTEGER NOT NULL, PRIMARY KEY (pack_ref, path)
) WITHOUT ROWID;
```

`draft` absorbs the orchestrator's `storageByPath`, `tailByPath` and `overrides` dictionaries as
columns. Pack alias backfill and re-hash re-queues become `UPDATE`s.

`LocalDedupResolver.Reservation` is a synchronization object (a `TaskCompletionSource` a second
arrival with the same content waits on), not data. Reservations that are still in flight stay
in-memory objects, keyed by content key and bounded by the pipeline's width; when one completes
its result is written to `reservations` and the object is dropped. Today the dictionary keeps
every reservation for the whole run, which on a first run is one per file.

### Connections and memory budget

`cache_size` is capped at 64 MB per connection. A run holds at most five connections (catalog
read cursor for the diff, catalog read for dedup, work writer, two work readers), so SQLite page
cache stays under 256 MB and is the largest remaining allocation. Everything else in the pipeline
is already bounded: DiffWorkQueue spills to disk, the three stage queues have depth limits, the
progress ledgers hold in-flight items only.

## Backup pipeline data flow

1. **Scan.** The scanner inserts into `work.scan` as it walks, a few thousand rows per
   transaction. Recursion, ignore rules and unreadable marking are unchanged. It reports the count
   at the end as today.
2. **Previous version.** The diff opens `SELECT … FROM entries WHERE version = ? ORDER BY path`
   on the catalog and `SELECT … FROM scan ORDER BY path` on the work database and merges the two
   cursors. `BackupDiffer`'s comparison logic (mtime/length, head and tail hashes, deferred full
   hash, unreadable handling) moves into the merge loop unchanged. `FileChange` items still flow
   into the existing `DiffWorkQueue`; the prober, compressor and uploaders are untouched.
3. **Dedup.** `LocalDedupResolver` is constructed over a catalog read connection and the work
   database instead of `Build(indexes)`. Its four questions map to four queries: existing blob by
   `(full_hash, length)`, ref collision by `storage_ref`, prescreen by `(length, head_hash)`, this
   run's reservations from `work.reservations`. One query per changed file, milliseconds even off
   cache, two orders of magnitude below the hash and compression on either side of it.
4. **Resume.** `OpenJournalAsync` streams the journal's records into `work.resume_blobs` and
   `work.resume_packs`. `FindBlob`, `FindUntouchedBlob`, `FindPack` and `ConfirmedBlobs` become
   queries. The journal's write side and format are untouched, so a journal written by the current
   release is read by the new one.
5. **Draft index.** A confirmed upload writes its entry into `work.draft`. `BuildEntries` and its
   three dictionaries disappear; the same decisions become columns and one `ORDER BY path` read.
6. **Finish**, in today's order, without an in-memory index:
   1. Stream `draft` in path order through the new streaming serializer into a temp file, then
      hand that file to `WriteIndexAsync` (volumes, encryption, tier unchanged).
   2. Write the info file as today.
   3. `INSERT INTO catalog.entries SELECT … FROM draft` plus `dirs`, `empty_dirs`,
      `unrecoverable` and one `versions` row, in one transaction.
   4. Delete `work.db`.
7. **Suspend / Stop.** Same trigger points, same journal fsync, same exception types. The work
   database is deleted; the next run rebuilds from the journal. Nothing in `draft` has reached the
   cloud index, so discarding it is correct.

## Browsing and maintenance consumers

Rule: read-only consumers become queries; consumers that rewrite an index go patch table → cloud
→ catalog, the same order as the run's finish. The catalog is never written before the cloud.

Read-only:

- **Tree browsing** (`VersionTreeService`): `dirs` and `entries` by `(version, parent)`.
- **Per-path history** (`/file-versions`): one point lookup per version instead of one whole index per version. (There is no version-compare endpoint; the earlier draft of this spec assumed one.)
- **Single-file lookup, restore estimate, local-root migration preview**: point or prefix-range
  queries. `LocalRootMigration.Inspect` stays static and pure; its `baseline` parameter becomes a
  lookup delegate instead of a `VersionIndex`.
- **Restore**: entries streamed by path range; restore logic unchanged.
- **Dead-weight compactor, deferred repair candidates**: queries by path set.
- **Retention cleaner**: the reference set of retained versions becomes one SQL set difference on
  `storage_ref`. Catalog rows for deleted versions are removed after the cloud deletions succeed.
- **Checker**: iterating a version's entries becomes a cursor; the cross-version referenced-blob
  set becomes `SELECT DISTINCT storage_ref`.

Rewriting:

- **Checker** (unrecoverable marks) and **repairer** (clear or set marks) write changes into a
  `patch(version, path, unreadable_at, unrecoverable)` table in a work database, serialize the
  version as catalog `LEFT JOIN patch`, upload, then apply the patch to the catalog. An upload
  failure leaves the catalog matching the cloud.
- **Import an existing container** and **lazy migration**: the downloaded index is streamed into
  the catalog through the streaming reader; no `VersionIndex` object is built.
- **Write-back verification** (`VerifyRoundTrip`): count entries while streaming the readback.

About a dozen services change; each swaps its data source and keeps its decision logic.

## Serialization, migration, compatibility

**Streaming serializer, same format.** `IndexStreamWriter` takes a path-ordered entry sequence
and writes the existing schema-version-1 byte layout to a `Stream`; `IndexStreamReader` yields
entries from a `Stream`. Both are verified byte-for-byte and entry-for-entry against the current
`IndexSerializer`, which moves to the test project as the oracle. `WriteIndexAsync` accepts a
temp file path instead of an object; `ReadIndexAsync` returns a stream.

**Lazy migration**, run the first time any path touches `(account, container, version)`:

1. `catalog.versions` has the row and the identity matches → use it.
2. `{version}.idx` exists → stream-import inside one transaction, verify identity and entry count,
   delete the file on success.
3. The legacy `app.db` row exists (today's `MigrateLegacyRowAsync` source) → same, delete the row.
4. Otherwise download from the cloud and stream-import. Archive-tier behavior as today.

A failed import rolls back and leaves the old source in place for the next attempt.

**Retired**: `VersionIndexMemoryCache`, `VersionIndexFileStore`, `LocalIndexCache`,
`LocalDedupResolver.Build`, `JournalResume`'s dictionaries, `BuildEntries` and the three
path-keyed dictionaries. `Backup__IndexCacheSize` is ignored; startup logs one line saying so.

**Compatibility contract**, each line covered by a test:

- Journal files and suspend marks: format, path, write timing unchanged.
- Info file and its local authoritative copy (`LocalBackupState`): unchanged.
- Cloud index blobs: byte format, volumes, encryption, tier unchanged.
- `app.db`: no new tables, no altered tables, no EF migration.
- A run suspended by the current release: its journal streams into `work.resume_*`, the versions
  it references migrate lazily, and the run resumes.

**Acceptance test**: fixtures produced by the current release's code (a half-finished run's
journal, mark, `.idx` cache, info file and Azurite blobs), resumed to completion by the new code.
The resulting cloud index must be byte-identical to what the current code produces from the same
fixtures.

**GC settings**: `ServerGarbageCollection=false`, `ConcurrentGarbageCollection=true` in the csproj,
and one compacting full collection at the end of every run so a Suspended process returns its
garbage instead of holding it until the next allocation.

## Error handling

- **Corrupt `catalog.db`** (open failure or `SQLITE_CORRUPT`): delete it, log a warning, rebuild
  lazily from the cloud. It is derived data; deletion is always safe.
- **Cloud index written, catalog insert fails**: retry once; on a second failure log an error and
  report the run as succeeded. The next read re-imports from the cloud, which for an Archive-tier
  index means a rehydration, hence the loud log.
- **`work.db` fails mid-run**: the run fails like any other error; confirmed uploads are in the
  journal; the next run resumes.
- **`/temp` sizing**: `work.db` shares the staging disk. Scan plus draft for several million files
  is 1 to 1.5 GB; documented in the capacity guidance. It is not counted against the staging quota,
  which governs backpressure, because it is not subject to backpressure and is deleted at the end.

## Concurrency

- **One catalog writer**: a per-container async lock inside the catalog service. Writers are the
  run's finish, retention, and the checker's and repairer's patch application, all already
  serialized by the busy tracker; the lock is the second guard. UI readers use their own
  connections and are never blocked under WAL.
- **The diff's long read transaction** on the catalog blocks WAL checkpointing for the run's
  duration. Catalog writes during a run are few and serialized, so WAL growth is negligible.
- **`work.db` writes** from the diff, prober, compressor and uploaders funnel through one writer
  task fed by a channel, batching commits without fsync, the same shape as `DiffWorkQueue`'s
  pump. Readers open their own connections.

## Testing

- **Equivalence unit tests**, old implementations kept in the test project as oracles: streaming
  serializer vs `IndexSerializer` (both directions, including empty index, unreadable entries,
  symlink targets, pack refs, volume sizes); merge diff vs `BackupDiffer` on generated
  scan/previous pairs; catalog dedup queries vs `LocalDedupResolver.Build` on generated index
  sets; `resume_*` queries vs `JournalResume`'s four lookups; retention reference sets vs the old
  algorithm.
- **Integration tests** (7zz and Azurite required): backup, suspend, resume; the cross-release
  fixture test above; import an existing container; checker and repairer rewrite paths; the three
  lazy-migration sources.
- **Memory benchmark script**, not in CI: a synthetic one-million-entry run recording peak RSS,
  the evidence for the 1 GB target.

## Delivery

One release. The release note states that downgrading past it is unsupported once `.idx` files
have been migrated, and gives the `/temp` sizing guidance. The docs under `docs/` that describe
the local cache (`architecture.md`, `storage-format.md`) are updated to describe the catalog.
