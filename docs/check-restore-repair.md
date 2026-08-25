# Check, restore and repair

Getting data back, and finding out whether it is still there before you need it.

## Check

A check verifies a backup along **two independent axes**, each with its own level. They replaced a
single `deep` boolean, which could not express the combinations that matter.

### Cloud axis (`CloudCheckLevel`)

| Level | What it does | Cost |
|---|---|---|
| Skip | nothing | none |
| Metadata | compares the container against the local cache | no cloud read |
| Existence + size *(default)* | one `HEAD` per blob, compared against `VolumeSizes` in the index | cheap |
| Content | downloads and re-hashes, optionally rehydrating Archive | expensive |

> **Rationale — why existence + size is the default rather than existence alone.** Comparing the
> recorded per-volume sizes catches truncation and wrong blobs without downloading a byte. Existence
> alone would pass a blob that had been replaced by something else of a different length.

### Local axis (`LocalCheckLevel`)

| Level | What it does |
|---|---|
| Skip | nothing |
| Existence + size + permissions | metadata only |
| Content hash *(default)* | full comparison — and the criterion for "repairable from local" |

The local ladder is the same one the diff uses: length → mtime and permissions → head hash → full
hash.

**The sentinel demotes this axis and only this axis.** When the backup's sentinel path is absent —
or, with none configured, its local root — the level is forced to Skip before the run starts, while
the cloud axis proceeds untouched. Without it, checking an unmounted source reports every entry as
`Missing`: a total failure that says nothing about the data, produced by a backup that is perfectly
healthy. On the scheduled path that arrives as a failure notification every night the disk is down.

The demotion happens in `BackupChecker.CheckAsync`, the single point all three callers pass through
(the UI runner, the scheduler, and repair's internal pre-check), and the report carries
`LocalSkippedSentinel` so the result can say which half ran. It does not affect `Ok` — the cloud half
really did pass. See
[configuration.md](configuration.md#sentinel-path-refusing-to-run-on-an-unmounted-source).

### The report

Every file gets a `CloudState`, a `LocalState` and a `Repairable` flag. Scheduled checks carry both
levels.

`LocalSkippedSentinel` names the path that was not there when the local axis was demoted, and is null
otherwise. It has to be on the report rather than left for the caller to infer, for the same reason
`OrphansChecked` does: a column of `NotChecked` cannot tell "nobody asked" from "asked, and the
source was not mounted", and the dialog that knew which one it was is closed by the time anyone reads
the result.

Local verification tolerates unreadable files: one that cannot be read is `LocalState.Missing` — no
usable local copy, and unusable as a repair source — consistent with the existing "path outside the
root" case.

> **Rationale.** A check crashing on an unreadable local file is worst possible timing: "there are
> unreadable files" is precisely when a check is most needed.

A pack is checked once per archive, not once per file, so the unit on screen is objects rather than
files.

### The check is a background job

`POST /backup-configs/{id}/check` returns **202** with a run state and you poll `GET` on the same
path; `CheckRunner` owns the run, the same pattern restore and repair use.

> **Rationale — it used to be synchronous, and that was the defect.** The request was held until the
> report was produced. A content-level check downloads and re-hashes the whole backup, so for a few
> hundred GB the browser or the reverse proxy cut the connection off first: the check had run for
> hours, there was nothing to watch it with, and the result was thrown away at the end.

Two consequences are load-bearing and each is deliberate:

- **The report outlives the run.** `GET` keeps returning the last report after the check finishes,
  because the dialog closes the instant a check starts and reopening it has to bring the result back.
- **"Never checked" answers 204, not 404.** The dialog asks once as soon as it opens, and a 404 there
  leaves a red error in the browser console that reads like a malfunction — which is exactly how it
  was first reported.

Because the dialog is gone for the whole of a check's life, the backup's status row is the only thing
on screen, so it has to carry the outcome of **both** axes: the object result *and* the
unreferenced-blob scan, including when that scan ran and found nothing (`orphansChecked`, not
`orphanBlobs.length` — an empty list cannot tell "nobody asked" from "asked, container clean").

## Repair from local

An explicit action, not something a check does on its own.

For a broken cloud blob, `BackupRepairer` recompresses from the local file — hash-verified first —
and **replaces it completely**, deleting all old volumes before writing.

- **Single-file blobs** update size and volume count in **every** referencing version, since dedup
  means several versions can share one blob.
- **Packs** are recompressed whole from the surviving members across all versions.

The mtime inside the archive does not matter: display uses index metadata, and restore resets times
and permissions.

When repair is impossible — the local file was deleted or has changed — the path is recorded in
`VersionIndex.UnrecoverablePaths` for the affected versions.

> **Known limitation, shared with dedup.** Repair looks for exactly one local source, and that path
> is the entry name. When several paths reference one member, only the entry-name path is probed —
> so a byte-identical file sitting at another referencing path is not used, and every referencing
> path is marked unrecoverable together. See [packing.md](packing.md).

## Restore

The user picks a destination root, or restores in place.

| Choice | Options |
|---|---|
| Source version | any retained version |
| Selection | a lazily loaded file tree, all folders and files |
| Conflict handling | overwrite (only if changed) / skip / rename and keep both |
| Archive tier | a rehydrate priority — Standard (default) or High — is required |

"Overwrite only if changed" compares hashes against the index, not timestamps.

Empty directories are recreated. Permissions and modification times are restored.

### Restore substitution

Files marked unrecoverable in the chosen version can be substituted from another version, per path,
in bulk and nearest-first. `GET /file-versions?path=` supplies the candidates. An unrecoverable file
with no substitution chosen is skipped without an error.

> **Rationale — why unreadable and unrecoverable are treated differently.** An `UnreadableAt` entry
> holds *valid* data that is merely older than the version's timestamp, and it is the best that
> version can give — flagging it is enough, and offering a substitution picker would imply a better
> option exists. `UnrecoverablePaths` is data that is actually broken, and substitution is exactly
> right there.

### Path safety

Restore combines the target root with each entry's path, and **validates that the result is still
inside the target root**.

> **Rationale — this was a real defect, not a theoretical one.** The conversion to a local path only
> substituted separators and validated nothing about `..`, and the entry path comes from the **cloud
> index**. The `/import` endpoint accepts any container, so importing a backup of unknown provenance
> and restoring it could write a file anywhere the container process can reach.

An out-of-bounds entry is **skipped and counted in `FailedFiles`** rather than aborting the restore,
consistent with the existing per-group tolerance. Symlink creation is treated the same way. This
holds **even with no `Backup__Root` configured** — it is independent of that boundary, which is
covered in [operations.md](operations.md).

### Case-sensitivity

Restoring onto a case-insensitive filesystem can silently merge two entries that differ only in case.
The target is probed for case sensitivity, and colliding entries are detected rather than one quietly
overwriting the other.

## Import

`/import` reads a container's info file, restores the configuration from it, and **downloads every
version index into the local cache**. After that, routine operation reads nothing from the cloud.

The user supplies the account, the container, the password if the backup is encrypted, and the local
path — everything else comes from the info file.

`(accountId, container)` is checked **before** the cloud is read: a question the local database can
answer should not cost a network round trip first, and an import destined for rejection should not
seed the cloud info file into local state on its way.

## Progress

Restore and verification both report progress through the same machinery as a backup, including the
in-flight list and the speed clock. Their stage units differ:

| Stage | Unit | One unit is |
|---|---|---|
| Restoring | objects | one pack archive, or one single-file blob |
| Cloud (check) | objects | one `HEAD` per pack, not per file |
| Verifying (check) | objects | a pack downloaded, extracted and re-hashed |
| Local (check) | files | one index entry |
| Listing (check) | blobs | one blob in the container, orphan or not — it counts what it lists, not what it finds |

See [progress-display.md](progress-display.md).

## See also

- [storage-format.md](storage-format.md) — what is being read back
- [packing.md](packing.md) — extracting a member from a pack
- [backup-engine.md](backup-engine.md) — the unreadable-input handling check has to survive
- [operations.md](operations.md) — the path boundary
