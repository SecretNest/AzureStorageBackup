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

Every volume small enough to buffer (≤ `BlobUploader.LabelMemoryLimit`, 256 MB) carries its own
xxh128 in blob metadata — `x-ms-meta-xxh128`, value `xxh128:<32 hex>` — written **with** the upload
request so the label commits atomically with the bytes it describes; larger files stream unlabelled
unless the caller already holds the hash (the raw route). The label's only consumer is the upload
path's skip decision — resume and repair verify a cloud volume in place instead of re-sending it;
check never reads it. Legacy volumes carry none and therefore always read as "different". The full
argument is [volume-identity.md](volume-identity.md).

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
`unix-excl` and would bring the `-shm` file back for every connection on the file. It holds:

- `versions` — one row per version the catalog knows: the version number, its identity stamp, its
  entry count and when it was imported.
- `entries` — one row per index entry, keyed `(version, path)`, carrying the entry's own columns
  (kind, length, mtime, permissions, the three hashes, symlink target, unreadable mark, storage
  reference) plus four derived ones: `seq`, the position the entry had in the index stream;
  `parent`, the directory part of the path; `path_fold`, the upper-cased path; and `path_key`, the
  path's UTF-16 big-endian bytes, whose byte order is ordinal string order.
- `dirs` — every directory implied by an entry path, plus every empty directory, so listing one
  level of the tree is a point lookup on `(version, parent)` rather than a scan of the subtree.
- `empty_dirs` — the directories the version records as empty, in the order the index lists them.
- `unrecoverable` — the paths a check or a repair declared beyond recovery, likewise in index order.
  The same fact is mirrored in an `entries.unrecoverable` flag, written in the same transaction, so
  the flag and the list cannot disagree about a path.
- `import_issues` — what an import had to drop rather than store. Today that is one case: a version
  whose index lists the same path twice, which the primary key cannot hold. The first entry wins and
  the loss is recorded, because it means that version can no longer be serialised byte-identically.

The secondary indexes are the point of the file — each one exists because something asks it a
question that would otherwise be a scan of every entry in every version:

| Index | Who asks |
|---|---|
| `(full_hash, length)` | dedup: "is this content already in the cloud?" |
| `storage_ref` | collision avoidance ("is this address taken?") and retention ("does any retained version still reference this blob?") |
| `(length, head_hash)` | the prescreen, before a full hash is paid for |
| `(version, parent)` on `entries` | the files in one directory, when browsing one level of the tree |
| `(version, parent)` on `dirs` | the subdirectories of that same directory, the other half of that listing |
| `(version, path_key)` | the diff cursor, which streams a whole version in ordinal path order |
| `(version, seq)` | serialising the version back out in its original order |
| `(version, path_fold)` | restore's case-collision check |
| `(version, storage_kind, storage_ref, seq)` | grouping a restore's or a check's downloads by the object they live in |

**`seq` is why the catalog can be written back to the cloud at all.** A version's entries are stored
in the index in the order the run emitted them, which is not path order, and a repair rewrites a
version by serialising the catalog rather than the file it downloaded. Without the original position
the rewritten index would be a resorted copy — the same information, different bytes — so the
position is stored as a column and the serialiser reads by it.

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

**The catalog is derived data.** The index blobs in the container are the recovery copy; the catalog
is a queryable copy of them and can be rebuilt from them at any time. That is what lets it run with
`synchronous=NORMAL`, and it is what makes corruption a cache miss rather than an incident: the
first read-write open of a path in a process runs `PRAGMA quick_check` once, and a file that fails
it — or that SQLite refuses as `SQLITE_CORRUPT` or `SQLITE_NOTADB` — is deleted with a warning and
rebuilt from the cloud on demand. That holds on the read path too: the cheap read-only probe every
reader starts with treats an unreadable file as a miss and falls through to the write path, which is
where the rebuild happens — and if the probe is the one that finds the corruption, on a path this
process already opened for writing earlier, it forgets that earlier pass so the write open re-runs
the check instead of trusting it. Deleting the whole `index-cache/` directory costs downloads, never
data. In a backup run, the first write open of a container's catalog is the start-of-run reconcile
against the info file (below), so `quick_check`'s one-time cost lands there, before anything is
scanned — not at end-of-run import, which is where a run's only other write of the catalog used to
fall before the reconcile moved in front of it.

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
