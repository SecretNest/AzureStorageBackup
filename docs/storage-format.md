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

One SQLite file per (account, container), in WAL mode, opened directly through
`Microsoft.Data.Sqlite` with pooling off and entirely separate from `app.db` — no EF context, no
migrations. It holds:

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
| `(version, parent)` | browsing one level of the tree |
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
entry count, and the `.idx` file or the legacy row is deleted only after that import commits. A
failed import rolls back and leaves the old source in place for the next attempt. A container nobody
has touched since the upgrade pays for no migration at all.

**The catalog is derived data.** The index blobs in the container are the recovery copy; the catalog
is a queryable copy of them and can be rebuilt from them at any time. That is what lets it run with
`synchronous=NORMAL`, and it is what makes corruption a cache miss rather than an incident: the
first read-write open of a path in a process runs `PRAGMA quick_check` once, and a file that fails
it — or that SQLite refuses as `SQLITE_CORRUPT` or `SQLITE_NOTADB` — is deleted with a warning and
rebuilt from the cloud on demand. Deleting the whole `index-cache/` directory costs downloads, never
data.

**One writer per container.** Every writer — a run's finish, retention, the check's and the
repairer's marks — takes the container's write lock for the duration. Readers open their own
connections and, under WAL, never wait for the writer. A `VersionCatalog` wraps a single connection
and is not thread-safe, so a caller that needs two cursors at once (the run opens one for the diff
and one for dedup) opens two.

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
writer task fed by a channel, readers on their own connections. It holds everything about a run that
grows with the file count: the scan's rows, the draft of the new version (one row per path, carrying
the diff's verdict, the previous version's entry beside it, and the storage, tail hash and identity
the run settles on later), the content this run has already uploaded so a second file with the same
content is deduplicated against it, and the journal's records read back at the start of a resume.

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
