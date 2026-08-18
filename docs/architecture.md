# Architecture

What the system is, the principles the rest of the design follows from, and how the pieces fit
together.

## What it is

A self-hosted backup tool that pushes local directories into Azure Blob Storage. It runs as a single
Docker container on a NAS: an ASP.NET Core backend, a React SPA served from the same origin, SQLite
for local state, and the 7-Zip CLI for compression and encryption.

One container, one user, any number of backups. Each backup is one local root pushed into one
container, identified by `(account, container)` — **a container holds at most one backup**.

## The four principles

Almost every decision in the other documents follows from one of these.

### 1. Local state is authoritative; a run reads nothing from the cloud

A backup issues **no cloud read** to decide what changed, what already exists, or where to put
anything. The local SQLite cache holds a copy of the info file and every version index, and dedup,
collision avoidance and volume counts are all decided from it.

> **Rationale.** Data can sit in Cold or Archive, where reads cost money and Archive reads require
> rehydration first. A design that asks the cloud "does this blob exist?" once per file turns a
> routine incremental backup into a bill. Import pulls everything into the local store, so "no local
> authority" is not a reachable state.
>
> Trusting the cloud without local authority is also dangerous in itself: you do not know who wrote
> those blobs, with which password, or whether the contents are still correct — and one wrong
> "already exists" silently records a file that was never uploaded as backed up.

**The trade-off is stated rather than hidden**: a backup trusts the local index as the cloud's truth
and will not re-upload a blob deleted behind its back. Finding that drift is
[check's](check-restore-repair.md) job, and repairing it is
[repair's](check-restore-repair.md).

The one thing that *is* read from the cloud on every write is the info file's ETag, used for
`If-Match` so an external change is detected rather than overwritten.

### 2. Content addressing

A data blob's name is derived from its content, so identical content lands at the same address and
dedup costs nothing. That has consequences everywhere:

- Uploading is **idempotent** — re-uploading the same content is a no-op, which is what makes an
  interrupted run cheap to resume.
- **The name is a claim about the content.** If the bytes reaching the cloud are not the bytes that
  produced the address, the container holds an object whose name contradicts its content — and
  nothing downstream would notice, because dedup, restore and check all read the index, and the index
  agrees with the name. Several guards exist solely to protect this property.
- Several index entries, in one version or across versions, can reference one blob. Restore and check
  therefore act **per referencing entry**, not per blob.

### 3. Never treat "cannot read" as "deleted"

A file that cannot be read this round carries its previous entry forward, marked. It never enters the
deletion set.

> **Rationale.** Otherwise, after retention rolls over a few rounds, a permanently locked file
> silently disappears from every version — and the data blobs behind it are collected as orphans.
> Whenever "what should happen when X cannot be read" comes up, ask first: does this handling make
> the diff judge it deleted? If so, it is data loss.

The same rule scales up: an unreadable **directory** registers its whole subtree rather than being
skipped, because skipping it would let one permission failure wipe an entire subtree out of the
index.

### 4. Single user, optional gate

No usernames, no account model, no permission model, and none of that is planned. An optional
password gate can be placed in front of the whole UI, and it is **access control only** — it is never
used as a master key for any data.

Backup encryption is a separate thing entirely: **a password on a backup means encryption**, one
switch covering both the info file and the 7z archives.

## Components

```
                         ┌───────────────────────────────┐
  Browser ──HTTP──────▶  │  ASP.NET Core (one container) │
                         │                               │
                         │  Endpoints                    │
                         │  Scheduler ──┐                │
                         │  BackupRunner│                │
                         │       │      │                │
                         │  BackupOrchestrator           │
                         │   ├─ LocalFileScanner         │
                         │   ├─ BackupDiffer             │
                         │   ├─ GroupingPlanner          │
                         │   ├─ StagingArea ──▶ 7zz      │
                         │   ├─ LocalDedupResolver       │
                         │   ├─ VolumeBlobIO ────────────┼──▶ Azure Blob Storage
                         │   └─ BackupJournal ──▶ disk   │
                         │                               │
                         │  RestoreOrchestrator          │
                         │  BackupChecker / Repairer     │
                         │  RetentionCleaner             │
                         │                               │
                         │  SQLite (app.db)              │
                         │  Data Protection key ring     │
                         └───────────────────────────────┘
```

### Process-wide singletons

Three things are shared across **every** backup in the process, and each has consequences the design
had to answer:

| Singleton | Consequence |
|---|---|
| The compression lock | production is globally serial; concurrent backups are measurably **slower** than sequential ones |
| The staging pool | one run holding compressed output blocks every other run's compression |
| `BackupBusyTracker` | a scheduled task whose target is busy skips rather than interrupting |

The second is why a transient-error pause has a patience threshold and degrades to a suspension: a
paused run holding the whole pool would otherwise take unrelated backups hostage. See
[run-lifecycle.md](run-lifecycle.md).

## The shape of a run

```
Scan → Diff → Plan ──▶ DiffWorkQueue ──▶ prober ──▶ compressor ──▶ uploaders ──▶ WriteIndex → Finalize → Cleanup
```

The left half is sequential and local. The right half is three concurrent stages connected by
bounded queues, because compression is CPU-bound and globally serial while uploading is
network-bound and parallel, and neither should ever wait on the other.

Everything that survives an interruption is written to a **journal** as it is confirmed, which is
what makes a resume cheap.

## Where the data lives

| | Local | Cloud |
|---|---|---|
| Configuration, schedules, logs | SQLite `app.db` | — |
| Secrets (account key, proxy and backup passwords) | SQLite, encrypted by the key ring | — |
| Info file (versions, pack metadata, settings snapshot) | cached copy + ETag | authoritative for recovery |
| Version indexes | cached, decrypted | authoritative for recovery |
| Data blobs and packs | — | the backup itself |
| Journals | `data/journal/…`, plain text | — |
| Temp (compress, staged, verbose logs) | `{tempPath}/…`, cleared at startup | — |

**Device-local configuration is deliberately not written to the cloud**: the local root, the ignore
rules and the scope rules describe *this machine*, and a recovery on another machine will have
different ones.

## Technology choices

| Choice | Instead of | Reason |
|---|---|---|
| XxHash128 | SHA-256 | faster, half the length, negligible collision probability for this use |
| Compact binary index | JSON | hashes as 16 raw bytes rather than prefixed hex text |
| SQLite | files | logs need filtering, schedules need querying; EF Core was already there |
| Official `7zz` binary | distro p7zip | p7zip writes a zero attribute for `-si` input, making single-file blobs unrestorable |
| Text-file journals | SQLite rows | one DB write per file becomes the bottleneck of a very large backup |
| Polling | SSE / WebSocket | no reconnection or reverse-proxy problems, negligible cost |
| Hand-written CSS | a framework | zero runtime dependencies |

## Reading order

If you are new to this codebase, read in this order:

1. **[architecture.md](architecture.md)** — you are here
2. **[backup-engine.md](backup-engine.md)** — the run, end to end
3. **[content-identity.md](content-identity.md)** — how change and duplication are decided
4. **[storage-format.md](storage-format.md)** — what ends up in the container
5. Then whichever of [pipeline.md](pipeline.md), [packing.md](packing.md),
   [run-lifecycle.md](run-lifecycle.md) or [check-restore-repair.md](check-restore-repair.md) is
   relevant to what you are changing.
