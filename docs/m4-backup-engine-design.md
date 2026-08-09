# M4 — Backup engine design

> Covers PRD chapter 3 and [backup-feature-design.md](backup-feature-design.md). Settled premises:
> password = encryption (one switch), symlinks skipped by default, change detection by hash,
> tiers without Smart (Hot/Cool/Cold[/Archive]).
>
> Sections 1–13 are the original M4 design. Where the implementation has since moved on, the
> **Implementation notes** blocks are authoritative and the surrounding prose is kept for context.

## 1. Core concepts

- **Backup** — one backup inside one container, identified by `(Account, Container)` (at most one per container, PRD 1.3). Holds the configuration, several versions, indexes and data.
- **Version** — every run produces one immutable version, referencing a file manifest (the second-level index).
- **Info file** — the authoritative metadata blob inside the container: configuration + version list + pack metadata. The single source of truth for recovery on another machine (PRD 1.5). Not read during normal operation (PRD 1.7), only on import and check.
- **Local cache** — SQLite holds backup state (previous version index, pack metadata) so a run does not have to read the container. Rebuilt from the info file on recovery.

> **Implementation notes (code is authoritative, 2026-07)**
>
> - Hashing is **XxHash128** (`xxh128:` prefix). The `sha256:` examples in §2 and §3.2 read as `xxh128:` per §13.2.
> - The info file and second-level indexes are **compact binary** (§13.4, `IndexSerializer` over `BinaryWriter`); blob names still literally end in `.json[.enc]`. The JSON in §3.1/§3.2 describes the *logical* structure, not the on-disk bytes.
> - Both data blobs and packs can be **split into volumes**: `data/{hash}.001/.002…`, `packs/{id}.7z.001/.002…` (a single volume uses the base name). Read, write and cleanup all go through `VolumeBlobIO`, which treats a family as a unit.
> - **Volume count is recorded in the version file** (§7): `StorageRef.Volumes` for single-file blobs, `PackInfo.Volumes` for packs (compaction may change it, which updates the info file, not the version index). Check verifies every volume exists, catching deletion or loss on the Azure side. Volumes upload **concurrently and in any order**; `.001` was once written last as an "the family is complete" marker, and was dropped together with cloud-side existence dedup — that marker doubled upload time for 2–5 volume files, and dedup no longer asks the cloud.
> - **Hash collision avoidance**: a data blob's metadata carries the original length plus headHash; dedup only skips when the metadata matches, otherwise it is treated as a collision, gets the alternate name `data/{hash}~1/~2…`, and raises an UnrecoverableError. `StorageRef.Ref` records the actual name for restore, check and cleanup. (The residual "same hash + same length + same headHash" collision probability is negligible; contents are no longer compared byte by byte.)
> - **Keyed addressing for encrypted backups (fingerprint resistance)**: data blobs are named `data/{HMAC(key, fullHash)[:16]}` with `key = HKDF(password, BackupMeta.KdfSalt)`, and collision metadata becomes an opaque `v = HMAC(key, fullHash|len|head)` that leaks neither length nor header. Someone who can list the container still cannot use a public hash to decide whether a given file was backed up. Dedup is unaffected (same content, same address). Unencrypted backups keep plain addressing. Only the orchestrator uses the key when creating blobs; restore, check and cleanup use the address recorded in the index. Residual leak: blob count and sizes. See `BlobAddressScheme`.
> - **Raw passthrough (PRD 3.3.2, `StorageRef.Raw`)**: a single-file data blob that hits the don't-compress list, has no password and fits in one volume is copied straight to staging and uploaded as **raw bytes**, skipping the 7z wrapper. `raw=1` also goes into the blob metadata so dedup follows the existing blob (correct even when two files with identical content have different don't-compress settings). Restore writes it back directly and deep check re-hashes it directly, neither extracting. Because a content-addressed blob can be referenced by several paths, restore and check copy/verify for **every** referencing entry. Keyed (encrypted) backups are never raw. See `BackupOrchestrator.CopyRawAsync`.
> - **Scheduled tasks skip a busy target (`BackupBusyTracker`)**: busy state is tracked per account/container and set during backup, restore or check. A scheduled task whose target is busy raises a Warning and skips rather than interrupting. HTTP backup/restore refuse to run concurrently; a manual check returns 409.
> - **Three-segment hashing against dedup collisions**: collision metadata gained a trailing 4 KB hash (`IFileHasher.TailHashAsync`) on top of length and leading 4 KB. A false dedup would need fullHash (128-bit, whole file) + length + head + tail to all match, which is not achievable in practice — no byte-by-byte comparison needed. `TailHash` is stored in the index entry (`IndexEntry.TailHash`, serialisation format 2). Encrypted backups fold the tail into the opaque `v` as well.
> - **Graduated check + local repair + unrecoverable marking + restore substitution** (PRD 2.3 extended):
>   - **Two check axes** (`CheckOptions`, replacing the old `deep` boolean): cloud `CloudCheckLevel` (skip / compare metadata against the local cache / existence + size (default: `HEAD` against the `VolumeSizes` in the index, catching truncation and wrong blobs without downloading) / content (download and re-hash, optionally rehydrating Archive)); local `LocalCheckLevel` (skip / existence + size + permissions / content hash (default, and the criterion for "repairable from local")). `CheckReport` gives each file a `CloudState`, `LocalState` and `Repairable`. Scheduled checks carry both levels (`ScheduledTask.Check*Level`).
>   - **Per-volume sizes in the index**: `StorageRef.VolumeSizes` / `PackInfo.VolumeSizes` (serialisation format 3/2), feeding the existence + size level above.
>   - **Repair from local** (`BackupRepairer`, an explicit action): for a broken cloud blob, recompress from the local file (hash-verified) and **replace completely** (deleting all old volumes first). Single-file blobs update size and volume count in every referencing version (dedup shares them); packs are recompressed whole from the surviving members across all versions. If repair is impossible (local file deleted or changed) the file is marked in `VersionIndex.UnrecoverablePaths` for the affected versions. mtime inside the archive does not matter — display uses index metadata and restore resets times and permissions.
>   - **Restore substitution** (`RestoreRequest.Substitutions`, path → version): the user picks a replacement version per unrecoverable file (in bulk, nearest-first). Unrecoverable files without a substitution are skipped without an error. `GET /file-versions?path=` supplies the candidates.
> - **Single-file dedup is purely local — zero cloud reads for backups this instance created (`LocalDedupResolver`)**: the local cache already holds each blob's content identity (fullHash + length + head + tail) and its storage details (ref, raw, volume count), so a backup issues **no cloud `HEAD`** to decide dedup or collisions:
>   - Across versions: a map from content identity to existing blob, built from the retained version indexes.
>   - Within one run: a reservation table (one `TaskCompletionSource` per ref) coordinates — later arrivals with identical content wait for the first uploader and receive the same `(ref, raw, volume count)`. This incidentally fixed a latent race where two files with identical content but different compression settings each wrote their own raw flag and corrupted restore. Different content colliding on one address diverts to `…~N`. A failed upload fails the waiters too — never dedup onto a blob that was not successfully written.
>   - **Authority is local, with no cloud fallback.** Import pulls the info file and every version index into the local store (`/import`), so "no local authority" is not a reachable state. Trusting the cloud without local authority is itself dangerous: you do not know who wrote those blobs, with which password, or whether the contents are still correct — and one wrong "already exists" silently records a file that was never uploaded as backed up.
>   - **The trade-off** (consistent with "avoid reading the cloud"): a backup trusts the local index as the cloud's truth and will not re-upload a blob deleted behind its back. That drift is **check**'s job to find.

## 2. Storage layout (blobs inside the container)

```
azurestoragebackup.index.json          # info file (unencrypted variant; contents are binary, see above)
azurestoragebackup.index.json.enc      # encrypted variant (one or the other; both in use per PRD 1.6)
indexes/v{N}.json[.enc]                # second level: one file manifest per version (binary)
data/{xxh128}[.001,.002,...]           # data blobs, content-addressed (dedup for free); large ones split
packs/{packId}.7z[.001,.002,...]       # grouped / split 7z packs
```

**Two index levels** (PRD note B):

- Level one is `versions[]` inside the info file (version number, timestamp, reference to the second level, statistics) — small, appended and updated once per run.
- Level two is `indexes/v{N}.json` — the full manifest for that version. A new version writes only its own file and never rewrites older ones (except where dead-weight compaction forces it, §6).

This avoids repeatedly rewriting one enormous index. The index files are themselves compressed and encrypted (PRD note B).

## 3. Data model

### 3.1 Info file schema (draft)

```jsonc
{
  "schemaVersion": 1,
  "backup": {
    "name": "...", "description": "...",
    "sourceRootHint": "/data/photos",     // hint only; the user re-specifies on recovery
    "encrypted": true,
    "createdAt": "...",
    "settings": { /* settings in force for this backup: a snapshot of the resolved defaults */ }
  },
  "versions": [
    { "version": 1, "createdAt": "...", "indexBlob": "indexes/v1.json.enc",
      "stats": { "files": 1200, "bytes": 3.4e9, "changedFiles": 12, "changedBytes": 5e7 } }
  ],
  "packs": {
    "p0001": { "blob": "packs/p0001.7z", "members": ["sha_a","sha_b"], "originalBytes": 900000, "deadBytes": 0 }
  }
}
```

### 3.2 Second-level index schema (`indexes/v{N}.json`)

```jsonc
{
  "version": 1,
  "entries": [
    { "path": "sub/a.txt", "kind": "file",
      "length": 123, "mtime": "...", "permissions": "0644",
      "headHash": "sha256:...", "fullHash": "sha256:...",
      "storage": { "kind": "blob", "ref": "data/{fullHash}" } },
    { "path": "sub/small.txt", "kind": "file", "length": 40,
      "headHash": "...", "fullHash": "...",
      "storage": { "kind": "pack", "ref": "p0001", "entryName": "sub/small.txt" } }
  ],
  "emptyDirs": ["sub/empty1", "sub/empty2"]   // empty directories (backed up, recreated on restore)
}
```

- Permissions, mtime, length and hashes are all recorded (PRD note A).
- Symlinks: skipped by default; if the user opts in, `kind:"symlink"` plus a `target` field.

### 3.3 Local state (new SQLite tables)

> **Implemented (2026-07, `CachedVersionIndex` table + `LocalIndexCache`)**
>
> - **Version indexes are cached** (they are the large ones), keyed by `(AccountId, Container, Version)` as serialised bytes. A version index is immutable once written, so a hit is valid by construction. `IdentityTicks` (the backup's creation timestamp) detects a container deleted and recreated — version numbers get reused but the contents differ — and a mismatch invalidates and re-downloads.
> - **The info file is locally authoritative too** (`LocalBackupState` + `TrackedInfoStore`): it is no longer read from the cloud each run, since it may sit in Cold where reads cost money. A serialised copy plus the cloud ETag is kept locally; a backup writes with `If-Match` to detect external changes (another machine, a recreated container), and on conflict it clears local state and reports, resyncing next time. Only when no local copy exists (first run, before import) is the cloud read and the copy backfilled. **Net effect: outside import, deep check and repacking, a backup performs zero cloud reads of the info file and version indexes.** (Single-user assumption: genuine concurrent writers are not handled; the ETag turns that rare case into a clean abort and resync rather than lost history.)
> - Hits and backfills: the orchestrator's diff reads the previous version index from the cache and backfills the new one after writing; retention cleanup reads retained indexes from the cache and evicts retired versions. **Import downloads every version index into the cache**, after which routine backups and cleanups no longer download version indexes or data.
> - The cache stores **decrypted** index metadata (paths, hashes), consistent with the keyed-addressing threat model: the attacker has cloud list access only, while this machine is trusted and holds the source files anyway.
>
> The original draft (field layout not adopted, kept for intent): `LocalBackupState` (AccountId, ContainerName, LastVersion, LastIndexCacheJson, UpdatedAt) and `PackState` (packId, members, originalBytes, deadBytes). The local cache is an optimisation; authority is the container's info file.

## 4. Backup flow (state machine)

```
Scan → Diff → Plan(group/dedup) → Compress → Upload → WriteIndex → Finalize → Cleanup
```

1. **Scan** — walk the local root applying gitignore rules (§5), producing entries (path/kind/length/mtime/permissions). Collect empty directories.
2. **Diff** (PRD note A) — compare against the previous version index:
   - Different length → changed, needs processing.
   - Same length, different mtime or permissions → compare **headHash** first (a leading slice, 4 KB by default, configurable): different → changed; same → compare **fullHash**; different → changed; same → update index metadata only (no re-upload).
   - Present in the previous version, absent now → deleted (excluded from the new version).
   - **Entries classified as single-file blobs skip fullHash** (`BackupDiffer`'s `fullHashDeferred`): on that path the hash is computed as a by-product of the compression read (`StreamAndStageAsync`) and then overwrites what diff recorded, so having diff read it too means reading every large file twice end to end. Classification depends only on path and length (§6) and is settled as soon as the scan ends. This applies **only** to entries already known to have changed (added, and modified with a different length); the two-level hash path for "same length, mtime or permissions changed" is untouched — there, fullHash is the only thing distinguishing MetadataOnly from Modified.
3. **Plan** — decide grouping vs single file for changed files (§6), and check for dead-weight compaction.
4. **Compress** — 7z compression / encryption / volume splitting through the staging state machine (§7), with a post-processing recheck (§9).
5. **Upload** — upload data and pack blobs concurrently, setting the tier (index tier for indexes, data tier for data) with retry backoff (PRD 4.1).
6. **WriteIndex** — write `indexes/v{N}.json` (upload first, confirm success).
7. **Finalize** — update the info file atomically (§8): write the new contents to a temporary blob, then overwrite on success, so a network failure cannot corrupt the whole thing (PRD note C).
8. **Cleanup** — apply the retention policy to expired versions and the data only they referenced (§10).

Progress reporting (PRD backup design §2): percentage plus changed file count and size (uncompressed, pre-grouping, deletions excluded). The full design is in [progress-display-design.md](progress-display-design.md).

## 5. The gitignore rule engine (shared component)

Reused in three places (ignore 3.3.1 / don't-compress 3.3.2.2 / don't-group 3.3.3.2) with identical syntax (gitignore style, including negation with `!`).

- Input: a rule set plus a relative path → a match decision.
- One implementation; each of the three sites holds its own rule set.

## 6. Grouping, packing and dead-weight compaction (PRD 3.3.3)

- **Grouping** — by default, small files in one directory (excluding subdirectories) are merged into one 7z pack to reduce blob count.
  - **Cross-directory packing** (2026-07-27, `CrossDirGroupRules`, global default plus per-backup override, empty by default): matching paths are packed by full-path order, ignoring directory boundaries. This came from measurement: under hash-sharded directory trees (Emby/Jellyfin metadata, Git objects, assorted caches — very many directories with one or two files each), splitting by directory drives the pack count towards the file count. 46,624 files produced over ten thousand packs, each costing a 7z process and a billable upload request, which defeats grouping entirely. Path ordering keeps same-directory files adjacent, so locality is not lost. Precedence: **don't-group > cross-directory > by-directory**. Empty by default, i.e. byte-for-byte identical to the historical behaviour.
  - Size limit (5 MB default): larger files are handled individually. Applies to newly added files only.
  - Don't-group list (gitignore syntax): matches are handled individually.
  - Per-group cap (100 MB default, pre-compression).
- **Dead-weight compaction** (30% default) — when files inside a pack are deleted or changed, the old data remains. Once the dead ratio (by original size) exceeds the threshold, the pack's **still-live** files are reprocessed (re-deciding grouping by the current size limit and don't-group list) and the old pack is deleted after the run completes.
  - **Dead-weight criterion**: only when no valid version references the file any more (affected by the retention policy, §10).

> **Implementation notes (code is authoritative)**
>
> - `GroupingPlanner` applies the size threshold and don't-group list uniformly to all changed files (Added **and** Modified).
> - **Compaction is wired into the cleanup pipeline** (`DeadWeightCompactor` + `RetentionCleaner`, 2026-07-17), using **in-place recompression** rather than "reprocess through planning": when a pack's dead ratio exceeds the threshold (30% default, `GlobalSettings.DeadWeightThresholdPercent`), the pack is downloaded, extracted, recompressed from **only the still-live members**, and written over the same packId (deleting old volumes). Because packs are referenced by `packId + entryName` and live members keep their entryName, **no version index needs rewriting** — simpler than the original §6 plan and avoiding cross-version index edits. Triggered only when a version retires, since that is the only time dead weight grows.
> - **Member content comes from local first.** Before recompressing, check whether each live member has identical content **locally** (hash-confirmed, even when length, time and permissions match): if so, use the local file and **skip the download**. Only members missing locally require fetching the old pack from the cloud.
>   - Consequently **a pack in the Archive tier can still be compacted when every live member is available locally** (no cloud read).
>   - When members are missing locally, whether to download is decided **per data tier**: `GlobalSettings.RepackDownload{Hot,Cool,Cold,Archive}` (defaults true/true/true/**false** — Archive off, to avoid expensive retrieval and rehydration). If downloading is not permitted, **the repack is abandoned** for that pack (the dead weight stays, recorded as `DeadBytes` for observability).
>   - Existence is checked before hashing (a short-circuit). See `DeadWeightCompactor`.

### 6.1 Cross-pack member dedup within one run (`PackAliasTable`)

Small files being packed used to have only two layers of dedup:

- **Within one pack** — 7z's solid archive dictionary matches across members, so duplicates cost almost nothing.
- **Across versions** — `LocalDedupResolver.TryFindPackMember` looks up the indexes of retained versions; a hit points the new entry straight at the existing member without compressing, uploading or packing it.

What was missing is **within one run, across packs**. The member table was only built from the historical `VersionIndex` passed into `LocalDedupResolver.Build`, so packs sealed during this run never entered it. On a first backup, or one adding many duplicate small files, identical content landing in different packs really was stored once per pack — compression dictionaries are not shared between packs.

**The criterion is four-way strict equality**: `fullHash` + `length` + `headHash` + `tailHash`. Any one differing or missing disqualifies. This matches `TryFindPackMember` exactly, for the reason recorded there: the criterion is either all four or it is not one, and making an exception for compatibility means leaving an ambiguous case in the one place that must not be ambiguous — "is this the same content?". All four are guaranteed present for pack candidates, since Added and Modified are computed by a single `ContentIdentityAsync` read; only unchanged entries can lack them, and they never take this path.

**Where the decision happens** — one more tier after the existing `TryFindPackMember` lookup:

```
existing-pack hit (cross-version, unchanged)  → write the StorageRef directly, file = null
    ↓ miss
this-run alias hit                            → record as an alias, file = null, not packed
    ↓ miss
register self as leader                       → packed as usual
```

The alias branch ends **exactly like** the existing-pack hit (`file = null`), taking the established "this entry did not change" path: directory counters decrement as usual, sealing timing is unaffected, no upload slot is taken and nothing needs settling. As a result the consumer side — `ProcessPackAsync`, `RecordPack`, `CompressPackTolerantAsync`, `UploadStagedPackAsync` — needs **no changes at all**.

Ordering cannot conflict with cross-version dedup: if the leader hits an existing pack, later files with the same content use the same member table and the same four-way criterion and also hit the first tier, never reaching the alias table. So any leader in the alias table is one newly packed in this run.

**Backfill happens at the end**, after all consumers join and before entries are built. By then everything has stopped and the decision is purely synchronous. For each leader:

```
storageByPath[leader] is { Kind: "pack" }
  and leader ∉ overrides
  and leader ∉ postDiffUnreadable
      → copy that StorageRef to every alias of this leader
otherwise
      → all its aliases are orphaned
```

The three veto conditions map to the three real ways a leader goes astray:

| Condition | Meaning |
|---|---|
| `overrides[leader]` set | The content changed inside the compression window (a new hash was written). **The alias's content no longer equals the leader's** — this is the correctness red line of the whole feature |
| `postDiffUnreadable[leader]` set | The leader was unreadable on the second attempt too and was downgraded in place, producing no blob |
| `storageByPath[leader]` not a pack, or missing | It grew past the threshold and became a single-file blob, or the whole group was unreadable |

The decision looks only at the **final state** and never tracks intermediate steps, so there is no race where the diff thread attaches an alias just as a consumer condemns the leader. That is the entire point of deferring backfill to the end, and it is why no new concurrency primitive is needed.

**Orphaned aliases are re-run.** When a leader goes astray, the alias files themselves are usually fine and should not be dragged down; they are pushed through as ordinary files and the first one naturally becomes a new leader. The upload gate, staging lease and upload scope are all still in scope at that point and are reused directly. They are split into two pools by compression mode first — one pack has one mode, and that cut must happen before packing — and `storeOnly` is evaluated against the **alias's own path**, since rules match by path and an alias may live in a different directory from its leader. Orphaned aliases are **not** deduplicated against each other: reaching this path requires the leader to be rewritten or become unreadable inside the compression window, which is rare to begin with, and storing a few extra copies on a rare path buys a linear, readable, testable finish.

**Progress counting pairs to zero.** Aliases neither `Enqueue` nor report an item — both sides are zero, which balances by construction, since an alias genuinely corresponds to no work. The orphan re-run passes a no-op item callback for the same reason: `Enqueue` is "once per work item", while `ProcessPackAsync` calls back once per group and how many groups `GroupIsFull` produces is its own decision, which the caller cannot declare in advance. On screen: with no orphans the behaviour is identical to before (the path does not execute); with orphans, the bar has already reached 100% and the finish runs silently for a while. The trade-off is deliberate — better a brief unreported stretch on an extremely rare path than any chance of a wrong denominator on the normal path.

**Read-only guarantees for existing backups:**

1. The alias table is built only from **this run's** changes and never reads or writes the previous index. Old indexes are untouched.
2. The reference written is `{Kind="pack", Ref=packId, EntryName=leaderPath}` — byte-for-byte the same shape `RecordPack` and cross-version dedup already wrote. No schema change, no new field.
3. Unchanged entries always carry their storage forward and never take this path. **No existing reference is released**, so existing packs' `deadBytes` do not move at all.
4. Every consumer was verified individually: retention collects live packs by `Storage.Ref` and groups live members by `EntryName`; the compactor's `liveBytes` deduplicates by EntryName against an `OriginalBytes` that counts actual members, so `liveBytes ≤ OriginalBytes` always holds and `deadBytes` cannot go negative or trigger compaction spuriously; restore copies each entry from `extractDir/EntryName` to its own path; check looks up each `entryName` and passes when both entries find the same item; repair collects by `(packId, EntryName)`.
5. **Two entries in one version index pointing at the same `(packId, EntryName)`** was already producible when cross-version dedup shipped. This feature introduces no new shape, it only makes that one common.
6. **No retroactive merging.** Duplicates already in history stay as they are until their versions retire. Merging them would mean rewriting old packs — a destructive operation on already-backed-up data.

**Aliases make a member harder to kill, not easier.** Live members are grouped by EntryName, so a member survives as long as **any** referencing path survives; an alias is one more pin. That is deliberate — references collecting on older packs make those packs less likely to be rewritten by compaction. The corollary is that **after the leader's own file is deleted, an alias must still restore**: at that point the entryName is kept alive by the alias entry alone, the pack is not deleted, the member does not die, and `extractDir/leaderEntryName` is still extractable. Every link in that chain was verified, but it is the part most likely to be quietly broken by a future refactor, so it is pinned by a test.

**Known trade-off: aliases degrade "repairable from local".** `BackupRepairer` looks for exactly one local repair source when fixing a member, and that path is `entryName` — the leader's path. Once the local file at the leader's path is gone or changed, the member cannot be repaired, and **every** path referencing it (the leader plus all aliases) is marked unrecoverable — even when a byte-identical file is sitting at one of the alias paths. `DeadWeightCompactor` probes the same single path and degrades the same way. This is not a new category: cross-version dedup has always had it, since local repair only ever looks at `entryName` regardless of how many entries reference the member. But this feature turns it from **occasional** into **routine**: cross-version hit rates depend on files being renamed or moved between versions, whereas within-run dedup fires whenever a backup contains duplicate small files. The fix is cheap and safe — the reference set already contains every path pointing at the member, so trying them in turn as local sources costs only a few extra `File.Exists` calls and hash computations, and `LocalMatchesAsync` verifies by hash anyway. It touches local probing in both the repairer and the compactor, so it is recorded here rather than done in passing.

**`PackInfo.Members` narrowed in meaning.** It lists the `fullHash` of each member. It used to double as "does this pack contain this content" and roughly as "how many references does this pack have". Now identical content is registered once per pack (aliases point at the same `EntryName` and create no new member), so `Members` equals neither the number of index entries referencing the pack nor a way to tell whether a piece of content appears twice. No consumer relies on that today — they all go by `EntryName` or `Ref` — but anyone reaching for `Members.Count` to estimate "how much did dedup save" will get a number that is too low.

## 7. The staging state machine (PRD 3.3.2.4)

Two directories:

- **compress-temp** — 7z's output target.
- **staged-temp** (1 GB default cap) — compression results are moved here for upload.

Rules:

- Compression writes into compress-temp and **moves** the whole volume set into staged-temp on completion (so a volume cannot be modified mid-compression).
- **Compression never runs concurrently, not even across backups** — one global queue and lock.
- Below the cap, the next compression is dispatched; over it, new compressions pause until uploads free space.
- One newly added result is allowed to overshoot the cap temporarily.
- A completed upload deletes from staged-temp immediately.

## 8. Index and info-file atomicity (PRD note C)

- Data and pack blobs are content-addressed: upload data first, then update the index; re-uploading is equivalent (idempotent).
- Second-level indexes: a new version writes a new file and never overwrites an old one.
- Info file updates: write to a temporary blob, verify, then overwrite the real name (or use blob versioning / ETag for optimistic concurrency). On network failure the old file is still intact.

## 9. Post-processing recheck and repeat protection (PRD note D)

- After processing a file, re-read mtime and permissions; if changed, re-hash; if the hash changed, reprocess.
- After a threshold of repeats (5 by default, configurable by env), raise a warning, save as-is and stop retrying.
- Grouped files: after compression, recheck the group's original files; changed members are moved out of the group into the next group for that directory, or handled individually if there is none.
- Final sweep: after all processing and before uploading the index, recheck once more, skipping anything already warned about.

## 10. Version retention and cleanup (PRD 3.2, 9)

- Retention policy: maximum version count (100 default) plus maximum age (180 days default), with a configurable rule for how the two combine (both / either / count only / age only).
- Triggered when a backup completes, and by the scheduled Cleanup task.
- Deleting a version deletes its second-level index plus any data blob or pack no longer referenced by a valid version.
  - **Volume-aware cleanup (code is authoritative)**: `RetentionCleaner` normalises `data/{hash}.NNN` back to the base name when comparing references, and groups packs by `packId` over the `packs/` prefix, so a volume family is deleted as a unit and a referenced volume is never deleted by mistake (§7).

## 11. Frontend: the new-backup flow (PRD backup design §1)

A two-step wizard:

1. Basics (immutable after creation, except name and description): account + container (a new container can be created), local root path, name, description, optional password (= encryption), index tier, data tier.
2. Per-backup settings derived from the defaults (individually, or "use default"): ignore / compression / grouping rules, version retention, concurrency, symlinks (skipped by default), run-record retention.

Then "back up now" or "not yet".

## 12. Subtask breakdown (historical, all delivered)

- **M4a — scanning and index foundations**: the gitignore engine, local scanning, index schema and serialisation (including encryption), info-file read/write with atomic updates, local state cache.
- **M4b — the diff engine**: version diff (length/mtime/permissions/hash), metadata-only changes updating the index alone.
- **M4c — compression and staging**: the 7z wrapper (compression, encryption, volume splitting), the staging state machine (non-concurrent, blocking over the cap, temp-then-move), the post-processing recheck.
- **M4d — grouping and dead weight**: pack grouping, dead-weight compaction.
- **M4e — upload and retention**: concurrent upload with retry backoff and tiers, version retention cleanup.
- **M4f — orchestrator and frontend**: the full pipeline with progress reporting, and the new-backup wizard.

## 13. Decisions (settled 2026-07-16)

1. **7z implementation** — the **official 7-Zip**: the image fetches the official `7zz` binary for the target architecture at build time. Command: `7zz a -p{pwd} -mhe=on -v{size} out.7z ...` (AES-256, header encryption, volume splitting); extraction is `7zz x out.7z.001`. The distro package was used first and had to be abandoned: p7zip / 7-Zip 23.01 write a zero attribute for `-si` stdin input, which makes single-file blobs unrestorable.
2. **Hash algorithm** — **XxHash128** for both fullHash and headHash (`xxh128:` prefix, 16 bytes). Originally SHA-256, changed to the non-cryptographic XxHash128: faster and shorter (halving index size), and 128 bits makes collision probability negligible for content-addressed dedup at personal-backup scale. **Not CRC**: its collision rate is far too high to use as a dedup key without losing data.
3. **Dedup and change detection** — whole-file dedup keyed on fullHash. Change detection uses **two hash levels**: the index stores headHash (a leading slice, 4 KB default, configurable) and fullHash, and diff compares headHash first as a fast filter. Content-defined chunking is deferred.
4. **Index serialisation** — **compact custom binary** plus 7z compression. Originally JSON, changed to binary for size: hashes stored as 16 raw bytes rather than `xxh128:`+hex text, with fixed-width encoding for enums, times and lengths. `IndexSerializer`'s public API is unchanged (byte-array round trip), so backup, restore and blob storage are unaffected.
5. **"Local file exists" during check** — same ladder as the §4 diff (length → mtime/permissions → headHash → fullHash).
6. **Frontend progress** — polling first, simple and reliable. (See [progress-display-design.md](progress-display-design.md) for where that went.)
