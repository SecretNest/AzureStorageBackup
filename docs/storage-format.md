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
index-cache/                           # local only — cached copies of indexes/v{N}; not in the container
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
| `index-cache/{accountId}/{container}/{version}.idx` (files beside `app.db`) | serialised version indexes |

Version indexes are files, not rows, and the difference is not cosmetic. They used to be one row each
in `CachedVersionIndex`, and one row can be 100 MB for a backup of half a million files — SQLite
permits a single writer at a time (WAL only stops readers and the writer from blocking each other),
so committing an index held the database's write lock for the whole write. On a loaded disk that was
tens of seconds during which nothing else could write: the log-retention sweep failed with
`database is locked`, and editing a backup in the UI appeared to do nothing. A file needs no lock.
Each file carries a 24-byte header (magic, format, `IdentityTicks`, body length), so a stale or
truncated entry is rejected by reading 24 bytes rather than by loading the whole index first, and it
is replaced by writing a temporary file and renaming it over the target, which is atomic.
Rows written before the move are handed to the file store on the first read of that index and then
dropped, so upgrading re-downloads nothing.

A version index is immutable once written, so a cache hit is valid by construction.
`IdentityTicks` — the backup's creation timestamp — detects a container deleted and recreated, where
version numbers get reused but the contents differ; a mismatch invalidates and re-downloads.

**Net effect: outside import, deep check and repacking, a backup performs zero cloud reads of the
info file and version indexes.** Only when no local copy exists (a first run, before import) is the
cloud read and the copy backfilled.

**Import downloads every version index into the cache**, after which routine backups and cleanups
download nothing.

The cache stores **decrypted** index metadata — paths and hashes — consistent with the threat model
behind keyed addressing: the attacker has cloud list access only, while this machine is trusted and
holds the source files anyway.

> **Single-writer assumption.** Genuinely concurrent writers from two machines are not handled. The
> ETag turns that rare case into a clean abort and resync rather than lost history.

An in-memory `VersionIndexMemoryCache` sits in front of the SQLite cache, because deserialising a
large index repeatedly during one run was measurable at 500,000 entries.

## See also

- [content-identity.md](content-identity.md) — what the hash fields mean and how they are used
- [backup-engine.md](backup-engine.md) — when each of these is written
- [packing.md](packing.md) — `PackInfo`, entry names and compaction
- [check-restore-repair.md](check-restore-repair.md) — reading it all back
