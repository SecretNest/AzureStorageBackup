# Azure Storage Backup

A single-user application that backs up local files to an Azure Storage account (Blob only). Access is open by default; set `Auth__Password` to put a password in front of the UI.

## Stack

- **Backend**: .NET 10 Minimal API + EF Core (SQLite) + Azure.Storage.Blobs
- **Frontend**: Vite + React + TypeScript
- **Packaging**: a single multi-arch Docker image (`linux/amd64` + `linux/arm64`) in which the backend serves both the API and the built frontend. Compression uses the official 7-Zip binary (`7zz`), downloaded at image build time for the target architecture.

## Repository layout

```
.
├── backend/                          # .NET 10 Minimal API
│   └── src/AzureStorageBackup.Api/
│       ├── Endpoints/                # Minimal API endpoint groups (all under /api)
│       ├── Services/                 # Business logic (Azure storage / backup engine)
│       ├── Data/                     # EF Core DbContext + migrations
│       ├── Models/                   # Entities and DTOs
│       └── Program.cs                # Composition root
├── frontend/                         # Vite + React + TS  (src/{api,components,pages})
├── Dockerfile                        # Multi-stage, multi-arch image
├── docker-compose.yml
└── .github/workflows/docker-publish.yml
```

## Local development

**Backend** (port 5122):

```bash
cd backend
dotnet run --project src/AzureStorageBackup.Api
```

**Frontend** (port 5173; `/api` is proxied to the backend by Vite):

```bash
cd frontend
npm install
npm run dev
```

## Tests

```bash
cd backend && dotnet test
```

Integration tests that talk to Azure use [Azurite](https://github.com/Azure/Azurite); they are skipped automatically when Azurite is not reachable on `127.0.0.1:10000`.

## How a backup runs

A backup goes through three stages. The **Details** panel on the Backups page names the stage it is in, what it is working on right now, and a speed — but the speed means something different in each stage, so it is worth knowing what you are looking at.

| Stage | What it does | Writes to disk? |
| --- | --- | --- |
| **Scanning** | Walks the local root and lists every file (path, size, modified time, permissions). | No |
| **Diffing** | Decides which files changed since the last backup, hashing content where needed. | No |
| **Uploading** | Compresses changed files into 7-Zip archives and uploads them. | Yes — see *Working space* below |

Scanning finishes before anything else starts, but **Diffing and Uploading overlap**: whether a file is packed with its neighbours or stored on its own depends only on its path and size, both known as soon as the scan ends. A large file therefore starts uploading the moment the diff decides it changed, and the Details panel shows two lines while both are running. Files that get packed together still wait for their whole directory to be diffed — until then it is not known which of them actually changed.

### Why the first backup is slow, and later ones are not

Diffing compares each file against the previous backup's index:

- **Same size, same modified time, same permissions → unchanged.** The file is not opened at all. This is the case for almost every file in a routine incremental backup, which is why Diffing then finishes in a fraction of the time.
- **Different size → changed.** The content is read and hashed.
- **Same size but the modified time or permissions changed →** the content is read and hashed to find out whether it really changed. If the hash turns out to be identical, only the index entry is updated and **nothing is re-uploaded**.

A first backup has no previous index to compare against, so every file takes the slow path. That is the one run where Diffing reads every byte you own.

> Two consequences worth planning around:
>
> - Anything that rewrites modified times without changing content — `touch`, some sync tools, restoring files from another backup, copying across filesystems — makes the next backup re-read and re-hash those files. It will not re-upload them, so no bandwidth is wasted, but the disk work comes back.
> - A file whose content changes while its size **and** modified time stay the same is not detected. This is the inherent trade-off of size+timestamp comparison and applies to every incremental backup tool. Run a **content-level Check** periodically if you need to catch it; that mode re-reads and re-hashes everything.

### What the speed number means

**During Diffing it is local disk throughput** — bytes read and hashed per second. It has nothing to do with the network. Files that were skipped as unchanged count zero bytes, so on an incremental run the file counter races ahead while the speed stays low; that is the expected shape, not a stall.

**During Uploading it is end-to-end throughput, not network speed.** Three differences matter:

- The bytes counted are **compressed and encrypted** bytes, not the original file sizes, so the number is normally well below what the source data would suggest.
- The elapsed time **includes compression**, which is still one-at-a-time across the whole run. A slow compressor drags this number down even on a fast link.
- Bytes are credited **when an object finishes uploading**, not continuously. Large packs therefore make the figure jump: flat for a while, then a spike.

So a low Uploading figure does not by itself mean the network is the bottleneck. While both stages are running you can read the two figures side by side in the Details panel — Diffing is disk, Uploading is compression plus network — which is usually enough to tell which one is holding things up.

### Reading the Details panel

Every number on a running row belongs to exactly one of four groups, and the groups do **not** share units. Mixing them up is the single most common way to misread a run that is perfectly healthy, so they are listed here in the order they appear on screen.

#### The collapsed line

```
Diffing + Uploading 45% diffed · 60.6 GB uploaded (1,234 changed)
```

- A single stage shows **one percentage**, computed from **source bytes** (pre-compression), not from the object count. Object counts are meaningless as a completion measure during Uploading — one item can be a 6.8 GB single file or a pack of several hundred 5 KB files — and in practice the count reads 75% while the bytes are at 31%. When the byte total is not yet known (Scanning and Diffing do not report byte workloads) it falls back to the count.
- While **Diffing and Uploading overlap**, no single percentage is honest: Diffing has a fixed denominator (entries found by the scan), Uploading does not (the diff is still enqueueing work). So the line names whose percentage it is and puts Uploading's **absolute** completed volume beside it.
- `(N changed)` appears only once the run has reached Uploading — before the diff finishes, the number does not yet exist.

#### Counts — `Stage: N of M …`

The unit word is chosen per stage because each stage counts a **different kind of thing**. A backup that packs 46,624 files into 4,995 archives is not losing anything; the two stages simply do not count the same objects.

| Stage | Unit | What one unit is |
| --- | --- | --- |
| Scanning | `entries` | A filesystem entry found by the walk. The total is unknown until the scan ends, so the line reads `N entries so far`. |
| Diffing | `files` | A source file, whether or not it later gets packed. |
| Uploading / Restoring | `objects` | A stored object: one pack archive, or one single-file blob. |
| Cloud (check) | `objects` | A stored object — one `HEAD` per pack, not per file. |
| Verifying (check) | `objects` | A pack that gets downloaded, extracted and re-hashed. |
| Local (check) | `files` | An index entry, i.e. a source file. |
| Orphans (cleanup) | `blobs` | A blob in the container. |

The speed figure and `~hh:mm:ss left` follow on the same line. The speed is shown whenever bytes are moving **or** a transfer is in flight — a stalled transfer is deliberately displayed as `0 B/s` rather than disappearing, because a missing line cannot be told apart from a finished one.

#### The in-flight breakdown

The middle segment splits the items that are neither finished nor untouched. It exists because `N objects processed` alone cannot distinguish work from a hang: during Uploading an item first goes through 7-Zip (tens of seconds for a 100 MB pack) before a single byte is pushed, and during that window nothing is on the network.

The identity **`processed + preparing + queued + uploading ≡ total`** always holds, in **items**. Two entries are counted in **volumes** instead and say so in their wording, because the upload gate hands out slots per volume, not per item:

| What you see | Unit | Meaning |
| --- | --- | --- |
| `N buffered to disk` | items | Diff ran ahead and its surplus work was spilled to a temp file. Cumulative for the run — it only ever grows. See `Backup__DiffQueueMaxItems` below. |
| `N volumes uploading` / `N downloading` | volumes / items | Transfers actually moving bytes. Upload registers one entry **per volume**, so a single large item can occupy the whole concurrency allowance on its own; download registers one per object. |
| `nothing on the wire right now` | — | This instant has no transfer in flight while an item is being prepared. It says *right now*, not *not yet*: several GB may already have gone up earlier in the run. |
| `N waiting on the same content elsewhere` | items | The identical content is already being uploaded by another item in this run; this one waits for that upload to finish rather than sending the bytes twice. Can take minutes. |
| `N volumes waiting for an upload slot` | **volumes** | The global upload gate is saturated. |
| `N starting upload` | items | Compression is done and the bytes have not left yet, for none of the reasons above — the item is doing the local work between the two: re-`stat`ing each pack member, looking up the dedup map (a dedup hit never uploads at all). Shown **only when no transfer is in flight**; an item that gets a slot immediately simply appears under `volumes uploading` instead. |
| `N preparing` (Uploading) | items | Holding the **global compression lock** and producing volume files. There is exactly one lock, so this is always `0` or `1`; everything queuing behind it counts as `queued`. |
| `N extracting` (Restoring / Verifying) | items | Downloaded and now extracting / re-hashing. No global lock here, so this can go up to the download concurrency. |
| `N queued` | items | Not started: still in the queue, plus items already picked up but waiting for the compression lock. |

The entries are ordered **backwards along the timeline** — closest to "bytes are on the wire" first, earliest last. An item's forward order is `queued → preparing → starting upload → waiting on peer/slot → uploading`, so reading the line right to left follows one item through the run. (`buffered to disk` and `nothing on the wire right now` are not stages and stay at the head.)

> The wording deliberately avoids "compressing" for `preparing`: a backup configured **not** to compress still goes through 7-Zip for packing, encryption and volume splitting, so it is preparing either way.

#### The byte line

Bytes get their own line, and the segments never overlap — each byte is in at most one of them. Zero segments are omitted, which is why Scanning and Diffing have no byte line at all.

During **Uploading**:

| Segment | Meaning |
| --- | --- |
| `X / Y original (Z%)` | Completed and total **source** bytes, pre-compression. Both sides are the same unit, which is the only way the fraction means anything — the compressed total does not exist until compression has run. `Z%` is the real completion figure for the run. `Y` keeps growing until the diff finishes. |
| `X uploaded (N% of original)` | Bytes this run actually pushed to Azure, post-compression and post-encryption. `N%` can exceed 100: store-only plus encryption (AES wrapping, archive headers) makes the result larger than the input, and that is worth knowing — it means this configuration costs *more* in the cloud than the source. |
| `+X uploaded in unfinished objects` | Bytes already in the cloud that belong to an item which has not finished. `uploaded` is credited per item so that it reconciles with the per-item source-byte accounting; a large item split into many volumes would otherwise make those bytes vanish from the screen for the tens of minutes it takes. Folded into `uploaded` when the item completes. |
| `X ready to upload` | Compressed archives sitting in `staged/` waiting for their turn. Rising means compression is outrunning the network; falling means the uploads have caught up. This is the number the **staging-area limit** applies to. |

During **Restoring / Verifying** the direction reverses, and the download total *is* known up front (volume sizes are recorded in the index): `X / Y downloaded · X restored · X to go`. Old indexes that predate recorded volume sizes show only the numerator.

#### The in-flight transfer list

Each concurrent transfer is listed on its own line with its own progress — `path — 41.2 MB / 220.0 MB · 18%`. The header says *parallel* explicitly because seeing two or three names at once suggests parallel compression, which never happens: compression is globally serialised behind one lock (that is the `N preparing` above), and only transfers run in parallel. The list is never truncated — its length is bounded by the upload/download concurrency setting — since the stuck transfer is usually the one that would have been folded away.

### Working space

Diffing produces no files — it only reads. Its cost is memory: the file list and the previous index are held in RAM (roughly 190 MB per 500,000 files, proportional to the file count).

Disk is consumed by Uploading, under `Backup__TempPath` (`/temp` in the image):

- `compress/` — the archive currently being produced. Compression is global and serial: one archive at a time, across all backups.
- `staged/` — finished archives waiting to be uploaded. This directory is what the **staging-area limit** on the Settings page (default **2 GB**) applies to: once it is full, the next compression waits until an upload frees space. A single archive is allowed to exceed the limit if it started below it, so the peak can be somewhat higher than the configured value — size the volume with that in mind.

Uploads themselves run in parallel (the per-backup upload concurrency setting); only compression is serialised.

A restore downloads each archive under `restore/` before writing files out. Single-file blobs — anything above the *single-file threshold* — are decompressed straight into the destination file, so they cost only the downloaded archive; the file's own bytes are never written twice. Packs are still extracted to disk first, so their peak is the downloaded pack plus its uncompressed contents, times the download concurrency.

## Docker

The image is self-contained: the backend hosts the API and the compiled SPA on **one** HTTP port (`8080`). Rehydration of Archive-tier blobs, 7-Zip compression, restore and repair all run inside this container, so the host directories you want to back up (and restore into) must be mounted.

Build and run locally:

```bash
docker build -t azurestoragebackup .
docker run -d --name asb -p 8080:8080 \
  -v asb-data:/data -v asb-keys:/keys -v asb-temp:/temp \
  -v /path/to/files:/backup-source \
  azurestoragebackup
```

Then open <http://localhost:8080>. Inside the app, set a backup's *local root* (and any restore target) to a path that exists **inside the container**, e.g. `/backup-source`.

Or with Docker Compose (`docker compose up --build`), after copying `.env.example` to `.env`.

### Environment variables

ASP.NET Core maps nested config keys with a double underscore (`Section__Key`). All are optional; defaults shown are the in-container defaults set by the image.

| Variable | Purpose | Default (image) |
| --- | --- | --- |
| `ConnectionStrings__Sqlite` | SQLite connection string (app database). | `Data Source=/data/app.db` |
| `DataProtection__KeysPath` | Directory for the Data Protection key ring used to encrypt secrets at rest (account keys, backup passwords). **Must be persisted** — losing it makes stored secrets undecryptable. | `/keys` |
| `Backup__TempPath` | Working area root: compression, staging, restore, check, dead-weight compaction, and verbose logs live under here. Can grow large during a backup/restore. | `/temp` |
| `Backup__Root` | Confines every local path — backup source, restore target, and the folder picker — to this directory. Unset = no limit. | *(unset)* |
| `Backup__IndexCacheSize` | How many deserialised version indexes to keep in memory. Trades RAM for responsiveness when browsing large backups — see below. `0` disables it. | `2` |
| `Backup__SevenZipMethodArgs` | Compression method switches handed to `7zz` — see below. Only `-m…` switches are accepted. | `-mx9` |
| `Backup__MaxPackMembers` | Largest number of files the app will put into one pack archive — see below. Caps how much memory `7zz` needs for member metadata. | `20000` |
| `Backup__MaxPackPathBytes` | Largest total size, in bytes, of the member paths handed to `7zz` on one command line — see below. Guards against the kernel's argument-list limit. | `1000000` |
| `Backup__DiffQueueMaxItems` | How many pending work items the diff→upload queue keeps in memory before buffering to disk — see below. | `2000` |
| `Backup__DiffQueueMemoryBytes` | Memory budget, in bytes, for that queue. Whichever of the two limits is reached first wins. | `67108864` |
| `Scheduler__Enabled` | Enable the cron scheduler for scheduled backup/check/cleanup tasks. | `true` |
| `Scheduler__TimeZone` | IANA time-zone id used to evaluate cron expressions. | `UTC` |
| `Auth__Password` | Password required to open the UI. Unset or empty = no authentication (the app logs a warning at startup). There is no username. | *(unset)* |
| `Cors__AllowedOrigins__0` | Allowed browser origin. Not needed for the single-image deployment (frontend is same-origin); relevant only when hosting the SPA separately, in which case every origin must be listed explicitly. `*` is ignored (it cannot be combined with cookie credentials) and logs a warning at startup. | *(none — no cross-origin request is allowed)* |
| `Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command` | Set to `Information` to log every SQL statement the app runs. Off by default — it is very noisy and drowns out everything else in `docker logs`. | `Warning` |
| `ASPNETCORE_URLS` | Listen address. | `http://+:8080` |
| `ASPNETCORE_ENVIRONMENT` | ASP.NET environment. | `Production` |

> The image runs with `ASPNETCORE_ENVIRONMENT=Production`, where **no** cross-origin browser request is allowed unless you list origins yourself. The Vite dev-server origin `http://localhost:5173` is preconfigured in `appsettings.Development.json` only, so it applies to `dotnet run` during development and never to the image.

> Every EF Core SQL statement is logged at `Information`, which is the framework default and makes `docker logs` almost unreadable — the app's own messages get lost among `Microsoft.EntityFrameworkCore.Database.Command[20101]` lines. It is therefore raised to `Warning` here. To turn it back on for a debugging session, set `Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Information`. The same pattern works for any category: `Logging__LogLevel__<Category>=<Level>`.

> Azure credentials are **not** configured through environment variables — each storage account is added in the UI and its key is encrypted at rest with the Data Protection key ring in `/keys`. If that directory is lost, the app starts in recovery mode and asks you to re-enter each credential; see [keyring-loss-recovery-design.md](docs/keyring-loss-recovery-design.md).

> Tuning values such as the staging-area limit, retention defaults and the dead-weight compaction threshold live in the database, not in environment variables — change them on the **Settings** page and they take effect immediately, without a restart.

> `Backup__IndexCacheSize` trades memory for responsiveness, and which way you want it depends entirely on how much RAM the machine has.
>
> A version index lists every file in a backup. It is stored compactly on local disk, so reading one means rebuilding the whole list in memory — and the restore dialog does that every time you expand a folder, as do the check and version screens. On a 500,000-file backup one expansion measured **~0.9 s and ~350 MB of allocation** just to return the handful of entries in that folder. Keeping the rebuilt index around makes every later read of the same version nearly instant.
>
> The cost is resident memory: roughly **190 MB per cached index at 500,000 files**, proportional to the file count (a 50,000-file backup is nearer 19 MB). The default of `2` keeps the version you are browsing plus one more, so comparing two versions stays fast.
>
> | Situation | Suggested value | Effect |
> | --- | --- | --- |
> | Normal machine (default) | `2` | Browsing and version comparison stay fast. |
> | Small-memory host (e.g. 1 GB NAS / Raspberry Pi) with a large backup | `0` | No index is held in memory. Every folder expansion rebuilds the index, so the restore dialog gets slower, but peak memory stays low. |
> | Small-memory host, or only ever browsing one version at a time | `1` | Most of the speed-up for half the memory. |
>
> Only browsing and reporting are affected. Backup, restore and check themselves read each index once and do not depend on this setting, so `0` never makes a backup slower — and it never changes what gets backed up or restored.

> `Backup__SevenZipMethodArgs` replaces the compression settings the app passes to `7zz`, which are `-mx9` (maximum LZMA2 compression) by default. Anything 7-Zip accepts as a method switch works, so you can trade ratio for speed or memory:
>
> | Value | Effect |
> | --- | --- |
> | `-mx9` (default) | Smallest archives, slowest, ~700 MB of RAM per compression at the default dictionary size. |
> | `-mx5` | Roughly half the time for a few percent more size. A good default on a NAS. |
> | `-mx1` | Fastest real compression; useful when the upload link, not the CPU, is the bottleneck. |
> | `-mx9 -md=256m` | Better ratio on large files, at ~2.5 GB of RAM per compression. Do not use on a small-memory host. |
> | `-mx9 -mmt=2` | Caps compression at two threads. Lower peak RAM and leaves CPU for everything else on the box. |
> | `-mx9 -m0=PPMd` | Better ratio than LZMA2 on text-heavy data, worse on everything else. |
>
> **Only method switches (`-m…`) are accepted**, and a bad value stops the app at startup rather than halfway through a backup. Everything else about the command line — file names, encryption, volume splitting, and the switches that govern how the app reads 7-Zip's output — stays under the app's control, because changing those would break how archives are located and verified. Files matched by a *don't compress* rule are still stored uncompressed (`-mx0`) regardless of this setting.
>
> The setting affects only archives written from then on. Existing backups keep whatever they were compressed with — 7-Zip records that in the archive — so changing it is safe and never invalidates anything already uploaded.
>
> If your goal is simply to keep compression from crowding out everything else on the machine, reach for **7-Zip CPU priority** on the Settings page first. It is set to *Lowest* out of the box, needs no restart, and applies to every `7zz` process — backup, restore, check and repair. `-mmt=N` caps how many cores compression uses; the priority setting decides who wins when they are all busy, which is what actually makes a NAS feel slow.

> `Backup__MaxPackMembers` and `Backup__MaxPackPathBytes` bound how many files end up in a single pack archive. **On a normal set of files neither one ever fires** — the per-backup *group cap* (100 MB by default, on the backup's own settings page) is reached long first. They exist for one situation: a folder holding an enormous number of very small files.
>
> The group cap is a size, so the smaller the files, the more of them fit. At 5 KB each a 100 MB pack holds about 20,000; at 1 KB, 100,000; at one byte, a hundred million. The whole member list of a pack is held in memory at once — compression needs it, the post-compression re-verification needs it, and a retry needs it again — so an unbounded member count is an unbounded amount of memory.
>
> | Limit | What it guards | Measured basis |
> | --- | --- | --- |
> | `Backup__MaxPackMembers` (`20000`) | Memory. `7zz` needs roughly **1.3 KB per member** for archive metadata, independent of compression level, plus about 0.4 KB on the app's side. 20,000 members ≈ 51 MB per pack. | `7zz` peak RSS measured at 1k/5k/10k/20k/34k members: 17 / 18 / 28 / 43 / 56 MB. |
> | `Backup__MaxPackPathBytes` (`1000000`) | A hard failure. Member paths are passed to `7zz` as individual command-line arguments, and exceeding the kernel's limit fails the compression outright with `E2BIG`. | A single `exec` accepted **1.73 MB** of arguments (`ARG_MAX` 2 MB, 8 MB stack): 34,218 members of 52-character paths passed, 34,375 failed. The default leaves ~40% headroom. |
>
> The second limit is counted in **bytes, not files**, because the wall moves with path length: the same 1.73 MB holds thirty-odd thousand 52-character paths but only about twelve thousand 150-character ones. A fixed file count would still hit it on deep directory trees.
>
> Raising either is safe as long as the machine has the memory; lowering them produces more, smaller archives. Neither changes anything already uploaded — they only affect how future packs are split, and packs are self-describing.

> `Backup__DiffQueueMaxItems` and `Backup__DiffQueueMemoryBytes` size the queue between the diff stage and the compress/upload stage. Diff decides what changed far faster than compression and upload can consume it, so it runs ahead; work it has queued but that has not been picked up yet is held in memory, and anything over these limits is buffered to a temp file under `Backup__TempPath` and read back as space frees up.
>
> Diff is never blocked by the queue. That matters for more than throughput: the upload stage cannot show a time estimate until diff has finished — that is when the total becomes known — so a queue that stalls the diff also delays the estimate until the very end of the run. Buffering to disk instead lets diff finish early and the estimate appear early.
>
> Both limits apply, whichever is reached first, for the same reason as the pack limits above: one work item is either a single large file or a whole pack, and a pack of small files can hold tens of thousands of entries. A count alone does not bound memory. The defaults (2,000 items / 64 MB) are enough that a backup of a few hundred thousand files typically never touches the disk buffer; if the progress line reports items *buffered to disk*, that is the signal to raise `Backup__DiffQueueMaxItems`.

> Setting `Auth__Password` puts a single password in front of the whole UI — there is no username, and changing the password means changing the variable and restarting. The session cookie is signed with the Data Protection key ring in `/keys`, so losing that directory signs you out and you will have to log in again; the password itself is read straight from the environment, so a lost key ring never locks you out.
>
> **Serve this behind an HTTPS reverse proxy in production.** Over plain HTTP both the password and the session cookie travel in the clear — the password gate keeps out people who do not know it, not anyone who can watch the traffic.

> `Backup__Root` is a **safety filter only**: it never rewrites or shortens a path, and it is not a base for relative paths. Paths are stored and displayed in full — with a root of `/nas`, a backup source still reads `/nas/photos/2024`. It constrains paths **inside the container**, so use it together with your volume mounts: mount everything you want to back up beneath that one directory.
>
> Symbolic links are resolved before the check, so a link inside the root that points outside it is rejected — including when the link is a middle segment of the path. Backup configurations whose local root falls outside the root are kept but refuse to run, so setting this on an existing install tells you which ones need attention instead of silently dropping them.
>
> **Restores are confined to the restore target, not to `Backup__Root`.** A restore never writes through a symbolic link that leads out of the target directory — not even one pointing somewhere else inside `Backup__Root` — because the index being restored is not trusted to describe where writes should land. If the target already contains such a link, every file under it is skipped and counted in the restore's failed-file total, and the run reports it. Restore into an empty directory, or remove links from the target first.
>
> A relative `Backup__Root` is resolved once at startup against the container's working directory and is shown in full from then on, so the folder picker and the paths it hands back are always absolute. Prefer an absolute value.

### Volumes / mounts

| Container path | Purpose | Persist? |
| --- | --- | --- |
| `/data` | SQLite database (`app.db`). | **Yes** |
| `/keys` | Data Protection key ring. Losing it makes stored account keys/passwords undecryptable. | **Yes** |
| `/temp` | Backup/restore working area (compress, staged, restore, check, compact, verbose logs). Safe to discard, but needs free space. | Optional (needs disk space) |
| *(your choice, e.g. `/backup-source`)* | Host directories to back up. Mount **read-only** if you only back up. A backup's *local root* is set to this in-container path. | Bind mount |
| *(your choice, e.g. `/restore-target`)* | Where restores write. Mount read-write. | Bind mount |

`GET /api/system/paths` returns the resolved absolute paths at runtime (PRD §6 "Directories"), useful when configuring Docker volume mappings.

## Published image (GHCR)

Multi-arch images are published to the GitHub Container Registry:

```
ghcr.io/secretnest/azurestoragebackup:latest
```

Publishing is a **manual** GitHub Action (`.github/workflows/docker-publish.yml`, `workflow_dispatch`) that builds for `linux/amd64` and `linux/arm64` with Buildx and pushes to GHCR.

## Documentation

Roadmap: `docs/roadmap.md`. Full requirements: `docs/product-requirements.md`. Backup engine design: `docs/m4-backup-engine-design.md`.
