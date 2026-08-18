# Azure Storage Backup — product requirements (PRD)

> This file records the complete requirements as given, and is the single source of truth for
> every milestone design that follows.
> Global constraints: single-user system, no authentication; Azure Storage always means Blob
> (never File Share); all UI text is in English.
>
> **Implementation note (code is authoritative; "no authentication" has been relaxed)**: it is
> still a **single-user** system — no usernames, no account model, no permission model, and that
> has not changed. What changed is that an **optional** password gate can be placed in front of
> the whole UI: set the `Auth__Password` environment variable and a login is required
> (`AuthGate`); leave it unset or empty and access stays open as before, with a warning logged at
> startup. The session cookie is signed with the Data Protection key ring under `/keys`, while the
> password itself is read straight from the environment — so losing the key ring only signs people
> out, it does not lock them out. Changing the password means changing the variable and
> restarting. See [operations.md](operations.md).

## 0. Settings storage

Settings must be persisted.

> **Decision: SQLite** (settled 2026-07-16). Accounts, groups, tasks, defaults and logs all live in
> SQLite — logs need filtering by level, time and source, tasks need querying, and a database fits
> better; the skeleton already had EF Core plus SQLite.
>
> Note: the info file (PRD 1.5/1.6) is a separate thing, stored in the Azure container, not in the
> local database.

---

## 1. Account settings

- **1.1 Account CRUD** — manage each Azure Storage account. Fields:
  - Required: endpoint, key and other necessary connection details; a name
  - Optional: description
  - Optional: whether to use a proxy; if so, either a dedicated proxy or the one from the docker environment variables. Proxies support a password
  - Where proxies are supported, an Azure region may be selected (China and other specific regions); without proxy support, only Global
- **1.2 Container management** — for a configured account, list its containers and allow creating, editing and deleting them.
- **1.3 Info-file recognition** — each container designates one filename as its *info file*. If that file exists, the container is considered one this tool uses. **A container holds at most one backup.**
- **1.4 New-account onboarding** — after an account is created, go straight to the container listing and suggest creating a container.
- **1.5 Info-file contents and recovery** — the info file holds nearly all relevant configuration and task information. To recover on another machine, configure the account and pick the container, and everything except the settings in this chapter is restored.
- **1.6 Encrypted and unencrypted variants of the info file**:
  - Two variants: one for encrypted backups, one for unencrypted
  - The info file for an encrypted backup must itself be encrypted, and may use a different filename
  - When both exist, only the unencrypted one is used
  - On finding an encrypted one, prompt for the password; loading proceeds only if it is correct (the user may cancel and skip loading)
  - Information already loaded into the tool is stored **unencrypted** — **except passwords**, which are stored reversibly encrypted
- **1.7 When the info file is read** — only when adding an existing backup to the tool, and when explicitly checking that file. Never during normal operation.

---

## 2. Scheduled tasks

- **2.1 Backup list** — list every discovered backup across accounts. Refreshed **manually**, never automatically (faster loading, fewer reads).
- **2.2 Groups** — groups can be created, each holding at least one backup. A group has a name and supports full CRUD. Scheduling a task on a group runs it against every backup in the group **in sequence**: the next one starts after the previous finishes, successfully or not.
- **2.3 Task settings** — backup and check tasks can be configured per backup or per group. Execution times use cron syntax, but a graphical editor must be provided for non-technical users.

---

## 3. Defaults

Defaults for backup and check; a backup that ticks "use default" inherits them.

- **3.1 Tier** (set separately for index files and data files):
  - Index files: Hot (default), Cool, Cold
  - Data files: Hot, Cool, Cold, **Archive (default)**
  - **Implementation note (code is authoritative)**: the `StorageTier` enum is `Hot/Cool/Cold/Archive`, **without Smart** (dropped because the Azure SDK does not offer it as an option — see §8.5 below). Data files default to **Archive** (lowest cost; rehydration before restore is expected behaviour for archival backup semantics).
- **3.2 Versions and time** — maximum version count (100 default), maximum age (180 days default), and how the two combine (both reached / either reached / count only / age only).
- **3.3 Local files**:
  - **3.3.1 Ignore** — matching paths and files are ignored, with support for exceptions (re-inclusion), using gitignore-style syntax. If an existing backup contains these files, new versions exclude them (as if deleted).
  - **3.3.2 Compression and encryption** (via 7z) — if the user selects neither compression nor encryption and does not use grouping, the original file is used directly; otherwise everything goes through a 7z wrapper.
    - **3.3.2.1** Compression level defaults to maximum.
    - **3.3.2.2 Don't-compress list** — matches are not compressed but are still included in the backup; same syntax as 3.3.1.
    - **3.3.2.3 Pack size** — target pack size defaults to 100 MB, which is also the volume size used when compressing.
    - **3.3.2.4 Staging area size** (1 GB default) — files to be backed up are compressed into this area for upload and deleted from it once uploaded. Compression must target a separate *compress-temp* directory first, and the result (possibly several volumes) is then moved here, so that no volume can be modified mid-compression. While the area is over its limit, no new file is compressed until it is under again; below the limit, the next compression task is dispatched. Compression tasks never run concurrently, not even across different backups. This design may let the area exceed its limit temporarily, by one newly added result.
    - **3.3.2.5 Encryption** — the user may set a password.
  - **3.3.3 Grouping** (optional, on by default) — merge several small files from one directory (excluding subdirectories) into one pack to reduce file count.
    - **3.3.3.1 Size limit** (5 MB default) — larger files are excluded from grouping and handled individually. Applies to newly added files only; already grouped or ungrouped files do not change state because of it.
    - **3.3.3.2 Don't-group list** — designated directories (including subdirectories) or files are handled individually; same syntax as 3.3.1. Applies to newly added files only.
    - **3.3.3.3 Per-group cap** (100 MB default, measured before compression).
    - **3.3.3.4 Dead-weight compaction ratio** (30% default) — when files inside a group are deleted or changed, the old data cannot immediately be removed from the pack. Once that ratio (by original file size) exceeds the threshold, the pack's remaining files are treated as not included in the backup and reprocessed along with everything else (re-deciding grouping by the current size limit and don't-group list), and the old pack is deleted after that run completes. **A file only counts as dead weight when no valid version contains it any more.**
  - **3.3.4 Symlink handling** — include or skip. Default to be confirmed. (Added 2026-07-16.)
- **3.4 Network concurrency** — upload/download concurrency, 5 by default.
- **3.5 Notifications** — whether to notify on unrecoverable errors, and on backup / restore / check start, success and failure.
- **3.6 Run-record retention** — distinct from 3.2: that one retains data versions, this one retains run records and logs.
  - **Implementation (code is authoritative, 2026-07-17, a two-tier model)**: logs are either **durable** or **ephemeral**.
    - **Durable** (task start/end/error audit, `LogEntry.Ephemeral=false`; `AppendAsync` defaults to Warning and above, and engine start/stop events are explicitly durable): kept until the backup is **deleted** (deleting a configuration deletes its logs, `DeleteForContainerAsync`) or until **manually cleared by time** (`DELETE /api/logs?before=<time>` removes everything older).
    - **Ephemeral** (info/debug diagnostics, `Ephemeral=true`): cleared automatically once expired, default `GlobalSettings.LogEphemeralMaxAgeDays` = 14 days (the scheduler's per-minute `TrimAsync` only removes expired ephemeral entries).
    - **Verbose (debug) logging**, which includes filenames, is **off** by default and can be enabled per backup (`BackupConfig.VerboseLogging`) or globally (`DefaultVerboseLogging`). When on, per-file logs do **not** go to SQLite but to text files partitioned by backup and by date: `{tempPath}/verbose-logs/{container}/{yyyyMMdd}.log` (`VerboseFileLog`). This keeps one DB write per file from becoming the bottleneck of a very large backup, and separates high-frequency diagnostics from the queryable audit log. The text files are trimmed by date on the same window as ephemeral logs (the scheduler's per-minute `Trim`, 14 days by default), and the path is shown on the Directories page so it can be mapped to a docker volume.
    - The original "retain by run count" was never implemented — the two-tier model (durable until the backup is deleted, ephemeral for 14 days) covers the intent.

---

## 4. Global settings

- **4.1 Network retry backoff** — 5 s, 30 s, 90 s, 300 s by default, then every 300 s, capped at 2 hours total.
- **4.2 Notifications** — one server address accepting POST or GET as the push destination, with proxy support.
  - GET: the address may contain placeholders such as `{Title}` and `{Body}`
  - POST: everything GET does, plus a text body (with the same placeholders) and a content type

---

## 5. Logs

View operation logs with filtering by level, time and source (a specific backup, for example), and allow clearing.

---

## 6. Directories

List every temp directory path (read-only) so the user can set up docker path mappings correctly.

---

## 7. Version

Display the current tool version, for verification.

---

## 8. Backup feature requirements

> Provided by the user on 2026-07-16, supplementing chapter 2. Covers the backup list UI, the backup
> engine, and check and restore.

### 8.1 The backup list and status

List every backup in the tool. Each one can be created, deleted, run, inspected per run, and shown
with its current status: **Normal** (the last task ended cleanly), **Error** (the last task failed,
or a check found something wrong), or **Backing up** / **Restoring** / **Checking**.

A backup sitting in Error can be reset back to Normal. Outside a run, everything except the fields
marked immutable at creation can be edited, and a rename must be reflected in the UI. Deleting a
backup asks whether to delete the container as well; standalone container deletion lives in the
account settings screen.

### 8.2 Creating a backup

**Step 1 — basics**, immutable after creation except name and description: account plus container (a
container can be created here), the local root path (exactly one), name, description, an optional
password, and the two tiers (index files including the info file, and data files).

**Step 2 — per-backup settings derived from the defaults**, each settable individually or ticked as
"use default", including symlink handling and run-record retention.

On completion, either "back up now" or "not yet".

### 8.3 Run progress

Progress must be visible while running: a percentage, plus the count and size of this run's changed
files — **uncompressed, before grouping**, with deletions not counting towards size.

Backups must include **empty directories**, and restore must recreate them.

### 8.4 Restore, check and cleanup

**Restore.** The user picks a destination root or restores in place. Conflict handling is overwrite
(only if changed) / skip / rename and keep both. The source version is selectable, and a lazily
loaded file tree covering all folders and files lets the user choose. If any data is in the Archive
tier, a rehydrate priority is required: Standard (default) or High.

**Check.** The method is selectable, including from a scheduled task: whether to check cloud file
existence (listing; default yes), whether to check cloud file content (downloading; default no), and
whether to check local file existence (date and length, comparing hashes when the date differs but
the length matches).

**Historical version cleanup** is triggered when a backup completes, and by a dedicated cleanup
scheduled task.

### 8.5 Settled questions (2026-07-16)

1. **Password means encryption**: a backup created with a password uses an encrypted info file plus
   7z encryption; without one, nothing is encrypted anywhere. It is a single switch.
2. **Symlinks are skipped by default.**
3. **Run-record retention and version retention are two independent policies** — execution logs
   versus data versions.
4. **Restore's "overwrite only if changed" compares hashes**, aligned with the hashes in the index.
5. **Tiers and "Smart"**, verified against the Azure .NET SDK: `AccessTier` supports only
   Hot/Cool/Cold/Archive. "Smart tier" is an *account-level* auto-tiering feature, not a tier
   settable on an individual blob, so the tool offers no Smart option. Index tier: Hot (default) /
   Cool / Cold. Data tier: Hot / Cool / Cold / Archive (default). Cold requires SDK ≥ 12.15.0.

### 8.6 Key technical constraints

**A. Index contents and comparison logic.** The index must hold, for each file, its permissions,
modification date, length and hash. When comparing: if the length matches but the date or permissions
differ, compare hashes. If only permissions or date changed, the next backup **only updates the
index** and does not re-upload data.

**B. Index distribution.** The index must be distributed so that no single index becomes enormous or
is rewritten repeatedly. Index files are themselves compressed and encrypted. Splitting by version
into a two-level index is to be considered.

**C. Atomicity and safety.** Because grouped files can change, the index of a historical version can
change too — and such changes must be safe: a network failure must not corrupt the whole thing.

**D. Post-processing recheck and repeat protection.** After processing a file, check its modification
time and permissions again; if they changed, recompute the hash; if the hash changed too, reprocess
the file. After a threshold of repeats (5 by default, configurable by environment variable), record a
warning and save the file's current version, stopping retries. For grouped files, run the same
recheck against the group's original files after compressing; a changed one is moved out of the group
and into the next group for that directory, or handled as a single file if there is none. Finally,
after all processing and **before uploading the index**, run the recheck once more across everything,
skipping files that already warned.

---

## Open points (resolved during the relevant milestone design)

- The exact schema and filename convention for the info file
- Where the master key for reversible encryption comes from (generated vs. a user master password)
- Whether the SDK actually supports a "Smart" tier
- Boundary details of the staging scheduler in 3.3.2.4
- How runtime progress for backup, check and restore is reported in the UI
