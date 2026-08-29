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

The `HEAD`s run as one flat pool across every object and volume, under the **Check HEAD
concurrency** setting (default 20) — its own budget, separate from `DownloadConcurrency`, because a
`HEAD` costs a round-trip and no bandwidth: sizing the download budget against a bandwidth cap must
not throttle a stage that is latency-bound. One missing volume settles its family's verdict, and the
family's remaining probes are skipped rather than paid for.

### Local axis (`LocalCheckLevel`)

| Level | What it does |
|---|---|
| Skip *(dialog default)* | nothing |
| Existence + size + permissions | metadata only |
| Content hash | full comparison — and the criterion for "repairable from local" |

The local ladder is the same one the diff uses: length → mtime and permissions → head hash → full
hash.

> **Rationale — why the dialog defaults to Skip.** A content-level local pass reads the whole tree
> and can run for hours, out of all proportion to a handful of findings; in practice a check is a
> question about the cloud. Repairability of whatever the cloud half finds is answered afterwards,
> per file, by the **Hash now** button in the findings table — or by repair itself, which hashes
> every candidate before touching anything. (`CheckOptions` the record still defaults to Content,
> so callers that ask for a full check get one.)

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

**What "repairable from local" means — and does not mean.** Repairable is a statement about the
**local file against the version's recorded content**: the file at the entry's path hashes to the
FullHash that version recorded, so repair can rebuild the cloud object from it. It says nothing
about what survives in the cloud. Surviving volumes of a damaged family play no part in the verdict
and are never reused in the repair — 7z output is not byte-reproducible, so a fresh compression
cannot splice into an old family; replacement is always whole. A file can be repairable with zero
volumes left in the cloud, and unrepairable with 999 of 1000 still there.

A problem whose local side was never checked reads **Unknown**, never "no": repairability is a
verdict only where the local content was actually hashed, and printing "0 repairable" after a
cloud-only check is a verdict where there was only an unanswered question (it sent a real user away
from the repair that would have fixed things). The summaries distinguish assessed counts,
"N not assessed", and "not assessed" outright.

**Hash now** answers the question for one file: the row's button hashes that single path against
the version's recorded content, instead of demanding a content-level pass over the whole tree. The
length is compared first, so a file that grew or shrank answers instantly without reading a byte —
which matters when the file is 100 GB.

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

- **The report outlives the run — and the process.** `GET` keeps returning the last report after the
  check finishes, because the dialog closes the instant a check starts and reopening it has to bring
  the result back. The last **completed** report is also persisted (`LastCheckRuns`), so pulling a
  new image does not force a re-run just to see a result that was already computed; a failed run
  carries no report and never clobbers the last real one.
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

### The plan comes first, and consent is per file

Clicking **Repair…** does not run anything: it prices the repair. The plan is built from the last
check report, the local index and one stat per problem file — no cloud request and not a byte read,
so it answers instantly even when every problem file is 100 GB. Each row states its fate:

- a file whose local length still matches the recorded length can be **re-uploaded now** (ticked by
  default, its object size on the row — subject to the hash gate below once the repair actually runs);
- a file whose local content changed **cannot be repaired from local**, and is marked damaged and
  left to the next backup version.

Everything unticked is deferred the same way: marked, not touched, no probing and no upload. The
confirm button carries the bill ("re-uploads X, defers N") before anything is spent. The hash runs
only inside the confirmed repair, only for ticked files — it answers "is this local byte-for-byte
the content the version recorded", the one question a stat cannot, and the one that must be answered
before any bytes may impersonate a recorded identity.

**What "repairable" is about — and not about.** Repair rebuilds the version's recorded content from
the local file and replaces the damaged object whole. Surviving cloud volumes play no part and are
never reused: 7z output is not byte-reproducible, so a fresh compression cannot splice into an old
family, and "just re-create the missing volume" does not exist.

### Deferral is a promise, and the next backup keeps it

Marking a file is not a shrug — it is "leave it to the next backup version", and that only means
something because the engine acts on it. Content addressing would otherwise make the damage
permanent for unchanged files: diff sees "unchanged", dedup sees "already in the cloud", and no
future backup would ever re-upload the broken blob on its own.

So after every **completed** backup run (manual or scheduled), the marks across the retained
versions are collected, filtered by a stat — only paths whose local length still matches their
recorded length can heal, so nothing unhealable triggers a nightly repair-and-renotify loop — and
handed to a repair scoped to exactly those paths. A healed blob is every referencing version's blob
at once, and the marks come off with it (a verdict overturned comes off the record). A file whose
content moved on never heals its old version this way; its current content is in the newer versions,
which is where it lives now.

### Prefix recovery: deliberately not offered

A file that only ever grows can hold a damaged version's content as its live prefix, and a repair
could in principle rebuild the blob from a truncated copy. The feature existed briefly and was
removed on review: in repair, prefix knowledge cannot shrink the work — losing volumes means the
archive must be rebuilt and re-uploaded whole either way, so the choice collapses to "full re-upload
or defer", which the plan already expresses without a special case. What the special case added was
surface: another verdict for the UI to explain, and version-consistency edges (packs worst of all,
where a member's prefix maps to nothing in the archive). If append-aware storage is ever worth
having, it belongs in the backup engine as a first-class capability, not in a repair corner.

The manual escape hatch costs nothing and needs no feature: any later version of an append-only file
contains the older content as a prefix, so "restore the newer version, truncate to the recorded
length" reproduces the old snapshot byte-for-byte without any upload.

### Unrecoverable is a verdict, and verdicts can be overturned

When repair is impossible — the local file was deleted or has changed — the path is recorded in
`VersionIndex.UnrecoverablePaths` for the affected versions. A **later repair that heals the path
removes the mark** in every version it repairs: left in place it would outlive the damage, and
restore would keep routing the healed file through version substitution as if it were still lost.

### Repair beside a suspended backup

Repairing while a backup run sits suspended mid-flight is safe, and sequential: repair takes the
same busy lock, so a resume attempted during a repair is refused until it finishes — nothing is
corrupted, just queued behind a click. The suspended run's uploads are protected from the orphan
sweep: they are in the cloud but in no version index, and only the journal records that they exist,
so the sweep (and the check's orphan listing) honours **active journal refs** exactly as the
retention cleaner always has. Deleting them would make the eventual resume re-upload everything it
had already sent.

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
| Restoring | objects, completion by bytes | one pack archive, or one single-file blob; the percentage comes from declared source bytes |
| Cloud (check) | volumes | one `HEAD` — the stage's unit of real work, so a thousand-volume object does not freeze the bar as one tick |
| Verifying (check) | objects, completion by bytes | a pack downloaded, extracted and re-hashed; the percentage comes from declared bytes, since one group can be a 100 GB file or a box of small ones |
| Local (check) | files | one index entry |
| Listing (check) | blobs | one blob in the container, orphan or not — it counts what it lists, not what it finds |

See [progress-display.md](progress-display.md).

## See also

- [storage-format.md](storage-format.md) — what is being read back
- [packing.md](packing.md) — extracting a member from a pack
- [backup-engine.md](backup-engine.md) — the unreadable-input handling check has to survive
- [operations.md](operations.md) — the path boundary
