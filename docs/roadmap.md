# Azure Storage Backup — implementation roadmap

The product is large, so it was built in milestones: each one runs design → implementation →
verification on its own and delivers something verifiable. Requirements live in
[product-requirements.md](product-requirements.md).

## Milestones

### M1 — settings infrastructure and account management (PRD 1.1)

- Storage layer extended (Account and proxy-configuration entities)
- Account CRUD: endpoint / key / name / description
- Secrets (keys, proxy passwords) stored reversibly encrypted, with a master-key scheme
- Proxy support: dedicated proxy or inherited from the docker environment, with a password; Azure region selection
- Frontend: the account settings screen
- Done when: accounts can be created, edited and deleted, and a connectivity test passes

### M2 — container management and info-file discovery (PRD 1.2–1.7)

- Container listing and CRUD
- Discovering and recognising the info file (deciding which containers belong to this tool)
- New-account onboarding (land on container listing, suggest creating one)
- Info-file schema: unencrypted and encrypted (password, reversible)
- "Import an existing backup": restore the configuration by reading the info file
- Frontend: the container screen
- Done when: containers can be managed and an existing backup can be discovered and imported

### M3 — scheduled task/group data model, defaults and global settings (PRD 2, 3, 4)

- The backup list (manual refresh)
- Group CRUD
- Backup and check task configuration per backup/group (cron plus a graphical editor)
- Defaults (tier, versions and retention, local file rules, network concurrency, notification switches)
- Global settings (retry backoff, notification server and proxy)
- Frontend screens
- Done when: everything can be configured (execution not yet wired)

### M4 — the backup engine (PRD 3.3; the hardest part, broken into subtasks)

- Local scanning plus one shared gitignore rule engine (reused for ignore / don't-compress / don't-group)
- Grouping and packing (size limit, don't-group list, 30% dead-weight compaction)
  - Compaction is wired into the cleanup pipeline (`DeadWeightCompactor`, in-place recompression, 2026-07-17); skipped for the Archive tier because it would require downloading. See [m4-backup-engine-design.md](m4-backup-engine-design.md) §6.
- 7z compression / encryption / volume splitting
- Staging management (compress-temp → staged-temp, non-concurrent, blocking over the cap)
- Tier application and version retention
- Concurrent upload with retry backoff
- Info-file writing (encrypted and unencrypted variants)
- Done when: a real backup runs to Azure Blob Storage

### M5 — check and restore (PRD 2.3, 1.5)

- Check tasks: verify backup integrity
- Restore: recovery on another machine (configure the account, pick a container, restore everything)

### M6 — the scheduler (PRD 2.2, 2.3)

- A resident background service firing on cron
- Sequential execution within a group (one finishes before the next starts)

### M7 — notifications (PRD 3.5, 4.2)

- Webhook POST/GET, placeholder substitution, proxy support
- Wired into every event trigger point

### M8 — log viewer, directories and versions (PRD 5, 6, 7)

- Logging infrastructure records from M1 onward; this milestone completes the viewing UI (filter by level, time and source; clear)
- Temp directory paths displayed
- Version display

## After M8 (by design sign-off date)

M1–M8 delivered something usable. Everything after that was surfaced by actual use: either a
security boundary, or observability and interruptibility under real data volumes. All of it is
merged into `main`; this table records only where each one went, with the design documents as
the authority.

| Date | What | Document |
|---|---|---|
| 07-25 | Recovery mode after keyring loss (canary detection plus a reset gate) | [keyring-loss-recovery-design.md](keyring-loss-recovery-design.md) |
| 07-25 | Optional UI password gate (`Auth__Password`, relaxing the PRD's "no authentication") | [auth-password-design.md](auth-password-design.md) |
| 07-26 | Local path boundary `Backup__Root` plus a directory browser | [local-path-root-design.md](local-path-root-design.md) |
| 07-26 | Frontend rework (design tokens, component system) | [web-ui-modernization-design.md](web-ui-modernization-design.md) |
| 07-26 | Backup defaults and the container picker | [backup-defaults-and-container-picker-design.md](backup-defaults-and-container-picker-design.md) |
| 07-26 → 08-09 | Run progress: per-stage counts, the in-flight breakdown, ETA, the upload speed clock, the `checking` tier, the archive-lock wait, unfinished-byte ownership, and both lines merged into one timeline | [progress-display-design.md](progress-display-design.md) |
| 07-27 | Unreadable input: marked and skipped, never treated as a deletion | [backup-unreadable-files-design.md](backup-unreadable-files-design.md) |
| 07-31 | Mobile adaptation | [mobile-adaptation-design.md](mobile-adaptation-design.md) |
| 08-01 | 7z CPU priority (Lowest by default, so it does not fight the NAS for resources) | [sevenzip-cpu-priority-design.md](sevenzip-cpu-priority-design.md) |
| 08-02 | Version start/end timestamps | [version-timestamps-design.md](version-timestamps-design.md) |
| 08-03 | Backup scope selection (a subset inside the root, `ScopeRuleSet`) | [backup-scope-selection-design.md](backup-scope-selection-design.md) |
| 08-06 | Changing the local root (a verified migration; the root is no longer immutable) | [change-local-root-design.md](change-local-root-design.md) |
| 08-07 | Cross-pack member dedup within one run (<5 MB, leader coverage, alias restore) | [m4-backup-engine-design.md](m4-backup-engine-design.md) §6.1 |
| 08-08 | Suspendable, pausable, resumable backups (journal, gates, graceful shutdown, auto-resume at startup) | [backup-suspend-resume-design.md](backup-suspend-resume-design.md) |
| 08-17 | Probe, compression and upload split into three stages, so `StagedLimitBytes` is what limits how far compression runs ahead instead of the size of the worker pool | [compression-upload-pipeline-design.md](compression-upload-pipeline-design.md) |
| 08-18 | A real Pause (holds the run in memory; Resume continues from the item it stopped on), and a stop that abandons the stages whose in-flight work it was about to discard | [pause-and-staged-stop-design.md](pause-and-staged-stop-design.md) |
| 08-18 | A resume that answers "already uploaded?" from a `stat` instead of re-reading every candidate file | [journal-mtime-fast-resume-design.md](journal-mtime-fast-resume-design.md) |
| 08-18 | A store-only unencrypted blob uploaded from the source rather than from a copy in the staging area | [raw-upload-without-staging-design.md](raw-upload-without-staging-design.md) |

## Notes

- Logging and notification **infrastructure** runs throughout (instrumented early); the full UI lands in the later milestones.
- M4 was the core difficulty and the largest risk, which is why it was broken into subtasks.
- After M8 the work stopped following milestones and became per-item delivery: design → plan → implement → review → merge to `main`. The repository keeps a single `main` line; a branch is merged and deleted as soon as it is done.
- Documents are organised **by topic, not by date**. A round that extends an existing topic is merged into that topic's document rather than filed as a new dated one — see the 07-26 → 08-09 row above, which is five rounds in one document.
- **Neither implementation plans nor completion records are kept.** Planning still happens between design and implementation, and rounds still get reviewed — but the task lists and the "what was delivered, which commits, how many tests" write-ups are scaffolding. Once the work has shipped, the code and the design document are the sources of truth, and a stale plan is worse than none: it reads like a specification while describing a shape the code has moved past. Whatever is worth surviving a round belongs in the design document; what the round cost belongs in the commit history.
