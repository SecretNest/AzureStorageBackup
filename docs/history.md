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
