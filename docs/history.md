# Project history

> **This is a record of how the system got here, not of how it works.** For the latter, start at
> [README.md](README.md). Nothing in this file is authoritative: where it disagrees with a design
> document, the design document wins; where the design document disagrees with the code, the code
> wins.

The product was large, so it was built in milestones — each one running design → implementation →
verification on its own and delivering something verifiable. After the milestones ran out, work
became per-item delivery.

## Milestones

| | Scope | Now documented in |
|---|---|---|
| **M1** | Settings infrastructure and account management: storage layer, account CRUD, reversibly encrypted secrets with a master-key scheme, proxy support, the account settings screen | [operations.md](operations.md), [web-ui.md](web-ui.md) |
| **M2** | Container management and info-file discovery: container CRUD, recognising which containers belong to this tool, new-account onboarding, encrypted and unencrypted info-file variants, importing an existing backup | [storage-format.md](storage-format.md), [check-restore-repair.md](check-restore-repair.md) |
| **M3** | Scheduled task and group data model, defaults, global settings, the backup list | [configuration.md](configuration.md) |
| **M4** | **The backup engine** — the hardest part, broken into six subtasks: scanning and the rule engine, grouping and packing, 7z compression/encryption/volume splitting, staging, tiers and retention, concurrent upload, info-file writing | [backup-engine.md](backup-engine.md), [packing.md](packing.md), [pipeline.md](pipeline.md), [content-identity.md](content-identity.md), [storage-format.md](storage-format.md) |
| **M5** | Check and restore | [check-restore-repair.md](check-restore-repair.md) |
| **M6** | The scheduler: a resident background service on cron, sequential execution within a group | [configuration.md](configuration.md) |
| **M7** | Notifications: webhook POST/GET with placeholder substitution and proxy support | [configuration.md](configuration.md) |
| **M8** | Log viewer, temp directory display, version display | [web-ui.md](web-ui.md) |

M4 was the core difficulty and the largest risk, which is why it was broken up. Logging and
notification **infrastructure** ran from M1 onward; the viewing UI landed in M8.

> The `M4` prefix survived in a filename (`m4-backup-engine-design.md`) long after it meant anything
> to anyone reading it. That is the failure mode this whole documentation set was reorganised to fix:
> a name that records *which batch built it* rather than *what it covers*.

## After M8

Everything past M8 was surfaced by actual use — either a security boundary, or observability and
interruptibility under real data volumes. All of it is merged into `main`.

| Date | What | Now documented in |
|---|---|---|
| 07-25 | Recovery mode after key ring loss (canary detection plus a reset gate) | [operations.md](operations.md) |
| 07-25 | Optional UI password gate, relaxing the PRD's "no authentication" | [operations.md](operations.md) |
| 07-26 | Local path boundary plus a directory browser | [operations.md](operations.md) |
| 07-26 | Frontend rework: design tokens and a component system | [web-ui.md](web-ui.md) |
| 07-26 | Backup defaults and the container picker | [configuration.md](configuration.md) |
| 07-26 → 08-09 | Run progress, over five rounds: per-stage counts, the in-flight breakdown, ETA, the upload speed clock, the `checking` tier, the archive-lock wait, unfinished-byte ownership, and both lines merged into one timeline | [progress-display.md](progress-display.md) |
| 07-27 | Unreadable input marked and skipped, never treated as a deletion | [backup-engine.md](backup-engine.md) |
| 07-27 → 07-28 | Streaming compression, hashing and restore — the pipeline's first shape | [pipeline.md](pipeline.md) |
| 07-31 | Mobile adaptation | [web-ui.md](web-ui.md) |
| 08-01 | 7-Zip CPU priority, lowest by default | [operations.md](operations.md) |
| 08-02 | Version start/end timestamps | [storage-format.md](storage-format.md) |
| 08-03 | Backup scope selection — a subset inside the root | [configuration.md](configuration.md) |
| 08-06 | Changing the local root: a verified migration; the root is no longer immutable | [configuration.md](configuration.md) |
| 08-07 | Cross-pack member dedup within one run | [packing.md](packing.md) |
| 08-08 | Suspendable, pausable, resumable backups: the journal, gates, graceful shutdown, auto-resume | [run-lifecycle.md](run-lifecycle.md) |
| 08-13 | Case-collision detection on restore | [check-restore-repair.md](check-restore-repair.md) |
| 08-14 | Inline edit panels; the Tasks tab renamed to Schedules | [web-ui.md](web-ui.md) |
| 08-16 | Deleted bytes in the run summary | [progress-display.md](progress-display.md) |
| 08-17 | Probe, compression and upload split into three stages, so the staging limit is what bounds how far compression runs ahead | [pipeline.md](pipeline.md) |
| 08-18 | A real Pause, and a stop that abandons the stages whose in-flight work it was about to discard | [run-lifecycle.md](run-lifecycle.md) |
| 08-18 | A resume that answers "already uploaded?" from a `stat` instead of re-reading every candidate file | [content-identity.md](content-identity.md), [run-lifecycle.md](run-lifecycle.md) |
| 08-18 | A store-only unencrypted blob uploaded from the source rather than from a staged copy | [pipeline.md](pipeline.md) |
| 09-07 | A pause that takes effect within a volume rather than a file, reaches the pack loop's every group and the wrap-up, and reads "Pausing…" until it has taken effect | [run-lifecycle.md](run-lifecycle.md), [progress-display.md](progress-display.md) |
| 09-07 | The prober's hand-off into a full probed queue steps out of the pause accounting: "Pausing…" no longer stands for good when the pool is full and the compressor is waiting for room | [run-lifecycle.md](run-lifecycle.md) |
| 09-07 | A pause stops the file under 7z where it is (SIGSTOP/SIGCONT) instead of waiting for it, and the time a run stands paused comes off the remaining-time estimate's clock | [run-lifecycle.md](run-lifecycle.md), [progress-display.md](progress-display.md) |
| 09-08 | Version indexes moved from files read whole into memory to a SQLite catalog per container, and a run's own bookkeeping into a scratch database, so neither grows with the file count | [storage-format.md](storage-format.md), [architecture.md](architecture.md), [operations.md](operations.md) |
| 09-08 | The catalog and the work database opened through `unix-excl`: no `-shm` file, after a NAS kernel refused its locks; opt-in `fcntl` trace in the image | [storage-format.md](storage-format.md), [operations.md](operations.md) |
| 09-08 | A container's whole history migrates with the content-keyed indexes down and rebuilt once, instead of a random page read per row; the pass has its own stage, "Loading versions", instead of sitting under Scanning | [storage-format.md](storage-format.md), [progress-display.md](progress-display.md) |
| 09-08 | "Loading versions" counts entries: each version's file count is its share of the workload, an import books rows as they land, and the completion and remaining time extrapolate from rows rather than from a version count that says nothing when versions differ a hundredfold | [progress-display.md](progress-display.md) |
| 09-08 | The catalog's once-per-process `quick_check` has its own stage, "Checking catalog", showing the file and its size, instead of running unannounced after "Loading versions 100%"; Pause greyed for it like the version load, Suspend and Stop interrupt the statement | [progress-display.md](progress-display.md), [run-lifecycle.md](run-lifecycle.md) |
| 09-09 | The check owns the local catalog: it runs `quick_check` and the version-list reconciliation on its own stage, replaces a corrupt catalog on the spot (a cache — rebuilt from the cloud on use) and drops stale versions, and says so in the report without it being a finding; no repair item, since a broken cache has no decision in it | [check-restore-repair.md](check-restore-repair.md) |
| 09-09 | The catalog `quick_check` is owed only after an unclean exit (an `.open` marker a write open leaves and a clean shutdown removes) or when a reader saw damage — an 8 GB catalog was being re-read for minutes at the start of every backup after a restart; and the stage shows no 0% / 0 B/s over a read that reports no progress | [storage-format.md](storage-format.md), [progress-display.md](progress-display.md) |
| 09-09 | Settings save per page: the row is two API resources, `/settings/defaults` and `/settings/performance`, each writing only its own half; the whole-object PUT (one page's Save overwriting the other page's fields) is gone | [web-ui.md](web-ui.md) |
| 09-08 | Pause is greyed while the versions load (the pass cannot park; a pause against it read "Paused" over a migration still running) and the pass counts as in hand; Suspend and Stop stay live and keep every version already imported | [run-lifecycle.md](run-lifecycle.md) |
| 09-08 | The version under import is named with its dates on the browser's clock, as the check and restore lists name versions | [progress-display.md](progress-display.md) |
| 09-12 | The wrap-up has names and numbers: Writing index opens as the index is serialized ("preparing the index"), Finalizing becomes Updating catalog and counts the import in entries, and the row's headline stops borrowing the upload's N-of-N for the stages after it (it read 100% → 0% → 100% for a hang that was three unnamed stretches) | [progress-display.md](progress-display.md) |
| 09-12 | One version's catalog import takes the drop-and-rebuild bracket when it is at least a hundredth of the history (`VersionCatalog.PrefersRebuild`), on the run's own import and on a single missing version alike; the live-index assumption held for a 1 GB catalog and read 1.2 GB/min for over an hour on an 8 GB one | [storage-format.md](storage-format.md) |

### The migration that read 30 GB to write 1 GB (2026.9.8.3)

The first run of 2026.9.8.2 on the NAS appeared to hang: the UI stood at "Scanning: 110,402 entries
so far" for hours with no error. It was not hung and it was not scanning. The scan had finished, and
the run was in the step that follows it — making sure every retained version is in the container's
catalog, which on the first run after the upgrade means importing the whole history — a step that
reported nothing, so the last line the scan had published stayed on screen. `docker stats` told the
rest: 37% CPU, 54 MB of memory, and a block-read counter that climbed 4 GB a minute to pass 29 GB,
against 352 KB written, while `catalog.db-wal` grew.

The reads were the three secondary indexes whose key does not start with `version`. The five that
do append at the tail of their tree for a new version; `entries_content (full_hash, length)`,
`entries_ref (storage_ref)` and `entries_head (length, head_hash)` scatter a new version's rows
across the whole history, and each insert reads one random leaf page of a tree that no longer fits a
64 MiB cache — reproduced with SQLite's own cache-miss counter: importing ten versions read 1.2 GB
of pages with the three live and a single page without them. ZFS serving each 4 KiB page from a
128 KiB record multiplied that into the 30 GB the NAS showed. Every consumer that needs a whole
history now asks for it in one call, which takes those three indexes down for a bulk import and
rebuilds them with one sort each at the end; a single missing version, the routine case, still
inserts with the indexes live. The pass also got its own stage line, "Loading versions: N of M", so
the next long migration says what it is.

### No `-shm` files for the catalog and the work database (2026.9.8.2)

The first 2026.9.8.1 run on the NAS (QNAP QuTS hero, kernel 6.6.32-qnap, ZFS) failed three times out of
three with `SQLite Error 15: 'locking protocol'` while committing the lazy import of a `.idx` into the
catalog. SQLite returns that code from exactly one place: a WAL connection that has asked the kernel
for a read-slot lock on the `-shm` file a hundred times over ten seconds and been refused every time.
`/proc/locks`, sampled once a second through the failure, showed the writer's own WAL write lock and
nothing at all on the read-slot bytes — nobody held what the kernel refused. The same SQLite library,
copied out of the image and driven from a python process in the same container against the same
directory, committed fine; so did a step-by-step `fcntl` replay of the lock sequence; the NAS is
offline, so the refused call's errno could not be captured. The one fact every observation agreed on
is that the failure lives in byte-range locks on a file whose only purpose is to share a WAL index
between processes — and these two databases are never shared between processes. They are now
opened through SQLite's `unix-excl` VFS: the WAL index lives in the heap, no `-shm` file exists,
connections inside the process still run concurrently ([storage-format.md](storage-format.md)
§ *The catalog*). The image also carries an opt-in `fcntl` trace (`ASB_FCNTL_TRACE=1`,
[operations.md](operations.md) § *SQLite locks*) so that a recurrence arrives with its errno.

### The index catalog (2026.9.8)

A backup of several million files drove the container to 8.4 GB resident, and it stayed there after
the run was suspended: an idle process never collects. Almost all of it was live data whose size was
the file count — two deserialised version indexes, the scan's list, the new version's entries and
the dictionaries built from them, the journal's records on a resume.

Those are all gone. Each container now keeps a SQLite catalog of every retained version's index
entries under `data/index-cache/`, and each run keeps a scratch database under `{tempPath}/work/`
for its scan, its draft and its resume records. Dedup, browsing, retention, restore and check ask
queries instead of holding indexes; the diff merges two cursors; the new index is serialised
straight to a file. Nothing durable changed — the cloud index bytes, the info file, the journal and
its suspend marks, `app.db` — and a run suspended by 2026.9.7 resumes on this release and commits an
index with the same entries, in the same order, referencing the packs the interrupted run had
already uploaded. That is what the cross-release fixture test in the suite exists to prove: it
replays a half-finished run recorded on the old build, `.idx` cache and all, and finishes it here.

What an operator needs to know:

- **Downgrading past this release is not supported once a container's indexes have been migrated.**
  A version is pulled into the catalog the first time something reads it, and the source it came
  from goes: an `.idx` file once its import commits (or straight away if its body turns out to be
  unparsable), a legacy `app.db` row as soon as it has been looked at. An older image would have to
  re-download every index from the cloud, which for an Archive-tier index means rehydration.
- **`Backup__IndexCacheSize` is retired.** It sized the in-memory index cache, which no longer
  exists. If it is still set, one startup log line says it is ignored.
- **`/temp` needs a little more room**: roughly 500 bytes per scanned file for the run's work
  database — and about **twice that while the run is in flight**, because the diff holds a cursor
  over the scan and that cursor pins a read snapshot, so the draft rows written beside it pile up in
  the write-ahead log instead of being folded back into the file. All of it is released when the run
  ends. [operations.md](operations.md) § *Temp space*.
- The process runs workstation GC rather than server GC, and does one compacting, decommitting
  collection at the end of every run, so a machine that has just finished a backup gets the memory
  back instead of seeing the high-water mark until the next one.

Measured over a 200,000-file run: peak managed heap fell from 550.0 MB to 94.3 MB, and the heap the
run genuinely holds — read after a forced collection — to 47.0 MB, and then to 39 MB once the work
database's write queue was bounded, while the working set left behind after the run rose from
191.4 MB to about 380 MB, live managed data traded for native residue, with no change in how long
the run took. [operations.md](operations.md) § *Memory* has both tables, the remaining ~70 bytes per
file, and the budget to watch.

## Working conventions

- The repository keeps a **single `main` line**. A branch is merged and deleted as soon as it is
  done.
- Everything written into the repository — commit messages, documents, code comments — is in English.
- Design documents are organised by topic. See the rules at the end of [README.md](README.md).

## Documentation reorganisation (2026-08-18)

The documentation set was rebuilt from 24 per-round documents into the current 15. What changed:

- Files named after the batch that built them (`m4-…`) were renamed after what they cover.
- Nine documents describing separate rounds of the backup path were merged into five describing the
  backup path.
- The `The problem → Starting point → Design → What this does not do → Tests` proposal skeleton was
  replaced by current-state description, with the reasoning preserved in `Rationale` blocks.
- `roadmap.md` — a milestone-and-date record — became this file, and is explicitly marked as history.

One correction was made in passing: the engine document described the diff's hash ladder as
`head → full`, while the code had had a `head → tail → full` ladder since the tail hash was
introduced. That is now correct in [content-identity.md](content-identity.md).
