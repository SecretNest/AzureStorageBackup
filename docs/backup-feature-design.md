# Backup feature design

> Provided by the user on 2026-07-16. Covers milestones **M3** (scheduled tasks, backup list UI),
> **M4** (the backup engine) and **M5** (check and restore). Supplements chapter 2 of
> [product-requirements.md](product-requirements.md).

## The backup list and status

List every backup in the tool. Each one can be created, deleted, run, inspected (per-run results) and shown with its current status.

**Status values:**

- **Normal** — the last task ended cleanly
- **Error** — the last task failed, or a check found something wrong
- **Backing up** / **Restoring** / **Checking**

## 1. Creating a backup

**Step 1 — basics** (immutable after creation, except name and description):

- Account plus container (a container can be created here)
- The local root path (exactly one)
- Name
- Description (optional)
- Password (optional)
- Tiers: one for index files (including the info file), one for data files

**Step 2 — per-backup settings derived from the defaults:**

- Every item can be set individually or ticked as "use default"
- **Symlink handling**: include or skip (must be present in the defaults)
- **Run-record retention**: by run count or by age, whichever is reached first (must be present in the defaults)

**On completion**: either "back up now" or "not yet" (configurable later through scheduled tasks).

## 2. Run progress (backup / restore)

Progress is visible while running:

- A percentage
- The count and size of this run's changed files (**uncompressed, before grouping**; deletions do not count towards size)
- Backups must include **empty directories**, and restore must recreate them

The full design of what is displayed and why is in [progress-display-design.md](progress-display-design.md).

## 3. Resetting the error state

A backup sitting in **Error** can be reset back to **Normal**.

## 4. Editing

- Outside a run, everything except the items marked immutable at creation can be edited
- A renamed backup must be reflected in the UI

## 5. Deleting

Deleting a backup asks whether to delete the container as well.

- Independent container CRUD lives in the account settings screen (PRD 1.2).
- The delete-backup flow itself does **not** offer standalone container deletion — only the "delete it too?" question.

## 6. Importing an existing backup

- Pick the account, down to the container
- If the backup is encrypted, enter the password
- Pick the corresponding local path
- Everything other than account, password and local path is held in the info file and restored from it

## 7. Restore

- The user picks a destination root, or "restore in place"
- Conflict handling when a file already exists: **overwrite (only if changed)** / **skip** / **rename and keep both**
- The source version is selectable
- A file tree (**lazily loaded**) lets the user choose, and includes all folders and files
- If any of the data is in the **Archive** tier, the user must specify a **rehydrate priority: Standard (default) or High**
- Restore starts on confirmation

## 8. Check

When a check runs (including from a scheduled task), the method is selectable:

- Whether to check **cloud file existence** (listing; default yes)
- Whether to check **cloud file content** (downloading; default no)
- Whether to check **local file existence** (date and length; when the date differs but the length matches, compare hashes)

## 9. Historical version cleanup

Cleaning up versions that are no longer needed, triggered:

- When a backup completes
- By a new "cleanup" scheduled task type

---

## Key technical constraints

### A. Index contents and comparison logic

- The index must hold, for each file: **permissions, modification date, length, hash**
- When comparing: if the **length matches but the date or permissions differ**, compare hashes
- If **only permissions or date changed** (content did not): the next backup **only updates the index** and does not re-upload data

### B. Index distribution and two levels

- The index must be **distributed** so that no single index becomes enormous or is rewritten repeatedly
- Index files are themselves **compressed and encrypted**
- Consider **splitting by version into a two-level index**

### C. Atomicity and safety

- Because grouped files can change, **the index of a historical version can change too**
- Such changes must be safe: **a network failure must not corrupt the whole thing** (atomic update, or write-new-then-switch)

### D. Post-processing recheck and repeat protection

- After processing a file (read / compress / pack), **check its modification time and permissions again**
- If they changed → **recompute the hash**
- If the hash changed too → **reprocess the file**
- If one file is reprocessed up to the threshold (**5** by default, configurable by environment variable) → record a **warning** and save the file's **current** version, stopping retries
- **Grouped files**: after compressing a group, run the same recheck against the group's **original files**; a changed one is moved out of the group and into the **next group for that directory**, or handled as a single file if that directory has no other group
- **Final sweep**: after all processing and **before uploading the index**, run the recheck once more across everything, **skipping files that already warned**

---

## Settled questions (2026-07-16)

1. **Password means encryption**: a backup created with a password uses an encrypted info file plus 7z encryption; without one, nothing is encrypted anywhere. It is a single switch.
2. **Symlinks are skipped by default.**
3. **Run-record retention and version retention are two independent policies** (execution logs versus data versions).
4. **Restore's "overwrite only if changed" compares hashes**, aligned with the hashes in the index.
5. **Tiers and "Smart"** (verified against the Azure .NET SDK): `AccessTier` supports only Hot/Cool/Cold/Archive. "Smart tier" is an *account-level* auto-tiering feature, not a tier settable on an individual blob, so the tool offers no Smart option:
   - Index file tier: Hot (default) / Cool / Cold
   - Data file tier: Hot / Cool / Cold / Archive (default)
   - Cold requires SDK ≥ 12.15.0 (12.29.1 in use satisfies this).

## Deferred to the M4 design

- The full info-file schema
- The concrete structure of the two-level index (version level → file level)
- Boundary details of the staging scheduler (PRD 3.3.2.4)
- Whether permissions count towards "local file exists" during a check

All four are answered in [m4-backup-engine-design.md](m4-backup-engine-design.md).
