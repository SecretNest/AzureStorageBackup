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
cd backend && dotnet test      # 1299 tests
cd frontend && npx vitest run  #   85 tests
```

The backend suite needs **two things on the machine**, and it is worth setting both up before you
trust a green run — without them most of it does not run at all, and a good part of the rest fails
for reasons that have nothing to do with your change.

**7-Zip.** Use the official `7zz` binary, not the distro's p7zip: p7zip (and 7-Zip 23.01) writes a
zero attribute for `-si` stdin input, which makes single-file blobs unrestorable. The Dockerfile
fetches it the same way.

```bash
ver="$(curl -fsSL https://www.7-zip.org/download.html \
       | grep -oE '7z[0-9]{4}-linux-x64\.tar\.xz' | grep -oE '[0-9]{4}' | sort -rn | head -1)"
curl -fsSL "https://www.7-zip.org/a/7z${ver}-linux-x64.tar.xz" | tar -xJ -C ~/.local/bin 7zz
```

Tests that compress are guarded and skip without it — but the endpoint tests are **not** guarded, and
fail with `No 7-Zip executable found on PATH` for what looks like an unrelated 500.

**Azurite**, reachable on `127.0.0.1:10000`. Tests that talk to Azure skip automatically when it is
not.

```bash
npx azurite --location /tmp/azurite --skipApiVersionCheck --silent
```

`--skipApiVersionCheck` is not optional: the Azure SDK this project pins negotiates an
`x-ms-version` newer than the list Azurite validates against, so without it **every** request comes
back `400 InvalidHeaderValue` and the whole integration suite fails rather than skipping.

A run with neither reports roughly `943 passed, 34 failed, 322 skipped`. A correct one reports
`1299 passed, 0 skipped` — if you see skips, the suite is not telling you what you think it is.

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

Every number on a running row belongs to exactly one group, and the groups do **not** share units. Mixing them up is the single most common way to misread a run that is perfectly healthy, so they are listed here in the order they appear on screen.

The expanded view is two lines, and the split between them is the one distinction worth learning: the **first** line is what has settled and will not move again, the **second** is the whole pipeline of what has not, ordered from nearly-done back to untouched.

#### The collapsed line

```
Diffing + Uploading 45% diffed · 60.6 GB uploaded (1,234 changed)
```

- A single stage shows **one percentage**, computed from **source bytes** (pre-compression), not from the object count. Object counts are meaningless as a completion measure during Uploading — one item can be a 6.8 GB single file or a pack of several hundred 5 KB files — and in practice the count reads 75% while the bytes are at 31%. When the byte total is not yet known (Scanning and Diffing do not report byte workloads) it falls back to the count.
- While **Diffing and Uploading overlap**, no single percentage is honest: Diffing has a fixed denominator (entries found by the scan), Uploading does not (the diff is still enqueueing work). So the line names whose percentage it is and puts Uploading's **absolute** completed volume beside it.
- `(N changed)` appears only once the run has reached Uploading — before the diff finishes, the number does not yet exist.

#### Counts — the unit each stage counts

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

#### The first line — what has settled

```
Uploading: 6,676 of 11,004 objects · 1.7 TB / 2.7 TB original (62%) · 1.7 TB uploaded (100% of original) · 13.3 MB/s · ~1d 10h left
```

Counts, then the bytes that will **not** change again: the items they belong to are finished and accounted for. Everything still in motion is on the second line. The dividing question is "can this number still go backwards?" — here it cannot.

During **Uploading**:

| Segment | Meaning |
| --- | --- |
| `X / Y original (Z%)` | Completed and total **source** bytes, pre-compression. Both sides are the same unit, which is the only way the fraction means anything — the compressed total does not exist until compression has run. `Z%` is the real completion figure for the run. `Y` keeps growing until the diff finishes. |
| `X uploaded (N% of original)` | Bytes this run actually pushed to Azure, post-compression and post-encryption. `N%` can exceed 100: store-only plus encryption (AES wrapping, archive headers) makes the result larger than the input, and that is worth knowing — it means this configuration costs *more* in the cloud than the source. |

During **Restoring / Verifying** the direction reverses, and the download total *is* known up front (volume sizes are recorded in the index): `X / Y downloaded · X restored`. Old indexes that predate recorded volume sizes show only the numerator.

#### The second line — the pipeline, newest first

```
In flight: +3.4 GB on the cloud · 5 volumes uploading · 2 volumes + 118 objects (9.2 GB) waiting for uploading · 1 object waiting for staging room · 4,365 objects queued
```

Everything that has **not** settled, laid out along a single timeline and read backwards: closest to done first, `queued` last. An item's forward order is

```
queued → waiting for the compressor → waiting for staging room
       → waiting for the archive slot → preparing → [archive lands on disk]
       → checking files → waiting for uploading → starting upload
       → waiting on peer → uploading → on the cloud → settled (first line)
```

Counts and bytes are interleaved at the point each belongs to, rather than living on separate lines. That is not cosmetic. With two independently ordered lines, nothing on screen placed `+2.0 GB on the cloud` relative to the `100.0 MB` sitting on disk, and the natural reading — "one item, two thirds uploaded, stuck before the last volume" — was wrong in every part. They are different items at different points of the pipeline, and volumes never stall between one another: the loop that uploads them does nothing but take a slot, `PUT`, and delete the local file.

The identity **`processed + preparing + queued + waitingOnRoom + waitingOnArchive + awaitingCompression + awaitingUpload + uploading ≡ total`** always holds, in **items**. Two entries are counted in **volumes** instead and say so in their wording, because the upload gate hands out slots per volume, not per item. The byte segments never overlap either — each byte is in at most one of them. Zero segments are omitted entirely, which is why Scanning and Diffing show almost nothing here.

| What you see | Unit | Meaning |
| --- | --- | --- |
| `+X on the cloud` | bytes | Volumes already confirmed by Azure whose **item** has not finished. A large item split into many volumes would otherwise make those bytes vanish from the screen for the tens of minutes it takes. Kept per item, so an attempt that fails partway is discarded whole rather than left stranded in the total; folded into `uploaded` on the first line when the item settles. |
| `N volumes uploading` / `N downloading` | volumes / items | Transfers actually moving bytes. Upload registers one entry **per volume**, so a single large item can occupy the whole concurrency allowance on its own; download registers one per object. |
| `nothing on the wire right now` | — | This instant has no transfer in flight while an item is being prepared. It says *right now*, not *not yet*: several TB may already have gone up earlier in the run. |
| `N waiting on the same content elsewhere` | items | The identical content is already being uploaded by another item in this run; this one waits for that upload to finish rather than sending the bytes twice. Can take minutes. |
| `N volumes + N objects (X) waiting for uploading` | **volumes** + items + bytes | The upload side's queue, printed as one entry with the units keeping its two halves apart: **volumes** are queuing at the global upload gate (saturated; slots go **oldest item first**, not first-come-first-served — see below), **objects** are compressed and claimed but not yet picked up by any uploader. The bytes are measured on exactly those two waits, and they do not divide by the counts — a dedup hit, a resume hit and a raw in-place item all queue owning no archive, so a store-only run can show five figures of objects against almost nothing on disk. |
| `N starting upload` | items | The archive is verified and the bytes have not left yet, for none of the reasons above. Shown **only when no transfer is in flight**; an item that gets a slot immediately simply appears under `volumes uploading` instead. |
| `X in the uploaders' hands` | bytes | The rest of the staging pool: the unsent tail of every volume on the wire, plus the archives of items parked on a peer or not yet past their first volume. Rising means compression is outrunning the network; falling means the uploads have caught up. |
| `N checking files` | items | Local work that pushes no bytes and waits on nothing: hashing a whole file for the dedup pre-check, `stat`-ing every member of a pack before and after compression (re-hashing the ones that moved), listing leftover cloud volumes before a multi-volume upload. Each of these can run for minutes on a NAS while emitting not one progress event — without this entry the screen showed a motionless `1 object starting upload` that was neither starting nor uploading. |
| `X being checked` | bytes | The part of the staged pool belonging to those checks: compressed, on disk, **not yet cleared to upload**. Held apart from the other pool figures because the post-compression recheck can still discard the whole archive and recompress it — those bytes may never go anywhere. Zero for checks that run before any archive exists (the dedup pre-check, the pre-compression `stat` sweep). |
| `N preparing` (Uploading) | items | Holding the **global compression lock** and producing volume files. There is exactly one lock, so this is always `0` or `1`. |
| `N extracting` (Restoring / Verifying) | items | Downloaded and now extracting / re-hashing. No global lock here, so this can go up to the download concurrency. |
| `N waiting for the archive slot` | items | Picked up by a worker and queuing behind that same global lock. Split out from `queued` because the lock is global **across backups**: run two at once and one of them can sit entirely behind the other's lock. The diagnosis is then free — `preparing = 1` plus this entry means the lock is your own and the queue is moving; `preparing = 0` plus this entry means another run holds it. |
| `N waiting for staging room` | items | One step earlier, and pointing the other way: the staging pool is at its byte ceiling (`StagedLimitBytes`) and no compression may start until an **upload** frees space. The wire is the bottleneck and the throttling is deliberate — stopping anything would be the wrong move. Split out of `waiting for the archive slot`, which it used to be reported as, making that entry's `preparing = 0` diagnosis point at a lock nobody was holding. This is the state an upload-bound run spends most of its life in. |
| `N waiting for the compressor` | items | Probed — their content identity is settled — but not yet in the staging area at all, because the compressor is a single worker taking one item at a time. Capped at the hand-off channel's depth, so unlike its unbounded counterpart above it plateaus. |
| `N queued` | items | Not started: still in the queue, not yet picked up by any worker. |
| `X to go` | bytes | Restoring / Verifying only: source bytes not yet written back out. |
| `N buffered to disk` | items | Diff ran ahead and its surplus work was spilled to a temp file. Cumulative for the run — it only ever grows. See `Backup__DiffQueueMaxItems` below. |

> The wording deliberately avoids "compressing" for `preparing`: a backup configured **not** to compress still goes through 7-Zip for packing, encryption and volume splitting, and a raw passthrough skips 7-Zip altogether — all three take the same lock, so all three are preparing.

#### The in-flight transfer list

Each concurrent transfer is listed on its own line with its own progress — `path — 41.2 MB / 220.0 MB · 18%`. The header says *parallel* explicitly because seeing two or three names at once suggests parallel compression, which never happens: compression is globally serialised behind one lock (that is the `N preparing` above), and only transfers run in parallel. The list is never truncated — its length is bounded by the upload/download concurrency setting — since the stuck transfer is usually the one that would have been folded away.

Normally the list shows **one item's volumes at a time, in volume order**, because upload slots go to the item that started uploading earliest rather than to whoever asked first. That is not cosmetic. An object only counts as uploaded once its whole set of volumes is confirmed, so several large files sharing the slots evenly would all sit half-finished at once — which is what `+X on the cloud` measures, and exactly what a stop or a crash throws away. Handing the slots out oldest-first keeps that number to roughly one item instead of one per upload stream, at no cost to throughput: the slots stay full, and whatever the oldest item cannot use goes to the next one immediately. You will therefore still see a second file appear near the end of the first — that is the leftover capacity being used, not the ordering breaking down.

### Working space

Diffing produces no files — it only reads. Its cost is memory: the file list and the previous index are held in RAM (roughly 190 MB per 500,000 files, proportional to the file count).

Disk is consumed by Uploading, under `Backup__TempPath` (`/temp` in the image):

- `compress/` — the archive currently being produced. Compression is global and serial: one archive at a time, across all backups.
- `staged/` — finished archives waiting to be uploaded. This directory is what the **staging-area limit** on the Settings page (default **2 GB**) applies to: once it is full, the next compression waits until an upload frees space. A single archive is allowed to exceed the limit if it started below it, so the peak can be somewhat higher than the configured value — size the volume with that in mind.

  When several runs share the pool, **Staging fair share** (Settings, off by default) decides what a full pool means. Off, the classic rule holds: a full pool blocks every run until it drains — absolute disk safety, but one run's oversized archive (a 100 GB media file's volume family lands whole) can freeze the others for the hours it takes to upload. On, 20% of the limit is reserved and split evenly as a per-run guarantee and the other 80% is shared first-come: nobody is ever starved completely, at the price of a larger possible overshoot when every run is handling huge files at once.

Uploads themselves run in parallel (the per-backup upload concurrency setting); only compression is serialised.

**Upload and download concurrency are counted per operation, not shared between them.** They sit under a *Global* heading, next to the staging-area limit — which really is one budget split across the runs in flight — so it is easy to read them as a shared ceiling, and they are not. Each backup gets its own upload streams and each restore or deep check its own downloads, so two backups running at once open twice the configured number of connections. Divide by however many you expect to overlap if you are sizing this against a bandwidth cap. A backup also runs one stream above the number set, which is what keeps a split archive's volumes from stalling at the hand-off between them.

A restore downloads each archive under `restore/` before writing files out. Single-file blobs — anything above the *single-file threshold* — are decompressed straight into the destination file, so they cost only the downloaded archive; the file's own bytes are never written twice. Packs are still extracted to disk first, so their peak is the downloaded pack plus its uncompressed contents, times the download concurrency.

### Stopping, pausing and resuming

A run that ends early no longer throws away what it already uploaded. Every object is recorded in a **journal** on disk — one file per run under `/data/journal/` — and the line is appended only *after* Azure confirms the write, never before. A later run reads that journal back and adopts what is already in the cloud instead of sending it again. An interrupted backup therefore costs the compression a second time, but not the bandwidth — and for a file nothing has touched since, not even a second read: the journal records the file's last-write time, so the next run answers "already uploaded?" from a `stat` rather than by hashing the file again. Anything whose length or timestamp moved is hashed as before.

Three buttons sit on a running backup:

| Button | What it does |
| --- | --- |
| **Pause** | Holds the run where it is. Each stage finishes the one item it has in hand and then stops; nothing is discarded and nothing is flushed. The run stays **Running**, holding its staged output, until you press **Resume**. |
| **Suspend** | Stops taking new work, lets the transfers already in flight finish, flushes the journal, and hands back the compression lock and the staging quota. The run ends as **Suspended**. |
| **Cancel** | Asks how first. *Stop now* kills the in-flight transfers immediately and deletes the half-uploaded volumes they left behind; *Finish current files* lets each file that is already uploading finish all of its volumes. The run ends as **Canceled**. |

**Pause is not Suspend, and the difference is what resuming costs.** Suspend tears the run down: the next one re-scans, re-diffs and re-checks every file before it reaches where the last one stopped. Pause keeps the run alive in memory, so Resume continues from the exact item it stopped on, with the work already compressed still sitting in the staging area waiting to go.

That waiting output is the price. A paused run keeps holding the staging disk — the row tells you how much, next to how long it has been paused — and that quota is shared across every backup on the machine, so a run paused overnight is a run holding it overnight. There is deliberately no timeout: a pause that turned itself into a suspend while you were away would be exactly the wrong thing to do with a button you pressed on purpose. From a pause you can still press **Suspend** or **Cancel**, and both behave as they would have.

Being memory-only also means a paused run does not survive a container restart. It does not restart itself either — see *Restarts and upgrades* below.

**A stop no longer waits for work it is about to throw away.** Suspend and *Finish current files* let the uploads in flight finish, because a volume that completes gets written to the journal and skipped next time. They no longer wait for a hashing or a compression to finish: neither leaves anything behind — the archive goes back to the staging pool, the hash is recomputed next run — so the only thing waiting achieved was making you wait, sometimes for several minutes on a large file.

Neither button returns until the run has really settled — journal on disk, temporary files gone, locks released. That is deliberate: an endpoint that returned early would let your next action (edit the config, delete it, start another run) collide with a run that had not finished dying.

**A wind-down can still be escalated, and Stop stays available while one is under way.** Suspend and *Finish current files* both wait for the file in hand — every volume of it — with the run still reporting itself as Running, which on a slow uplink is minutes. If you find out what it is waiting on and would rather not wait, press **Stop** and choose *Stop now*: the stop kinds form a ladder, a stronger one always wins, and only *Stop now* interrupts the transfer already on the wire. The other buttons do go quiet — Retry now, Resume and Pause all act on the pause gate, which any stop has already released for good, and Suspend is a step back down the ladder that would be ignored.

Both keep what finished uploading, and both keep the journal, so the next run picks those objects up rather than re-uploading them. The difference is what the UI offers next: a **Suspended** run shows **Resume** and **Discard**, a **Canceled** one goes back to a normal **Run** button. *Resume* is simply Run — every run adopts a still-valid journal on the way in, so there is no separate resume mode. *Discard* throws the recovery point away; the objects it was protecting stop being reserved and the next cleanup removes them.

A journal is only adopted if the run it describes still matches: same local root, same baseline version, same encryption identity. If any of those changed, it is dropped rather than trusted, and a file only counts as already-uploaded when **both** its path and its content hash match — a file edited since the crash is uploaded again.

#### Repairs pause and suspend too

A running repair carries the same three buttons in the same order — **Pause · Suspend · Stop** — and its pause is even finer-grained than a backup's: the gate is checked before every volume, so a pause answers within seconds even in the middle of a 100 GB family. Suspend saves the repair's selection; Resume replays it against a fresh assessment, so anything healed in the meantime falls out on its own and volumes already uploaded are verified and skipped rather than re-sent. At the very start of a repair, every problem file is marked unrecoverable and the marks are persisted **before any work begins** — so however the run ends, backups and restores running afterwards always see the truth about which content is still broken; each repaired object then clears its own marks.

#### Archive-tier restores never touch the originals

Restoring content whose volumes sit in the **Archive** tier does not rehydrate them in place. Each archived volume is *copied* (Azure's Copy Blob) to a temporary Hot copy under the container's `restore-tmp/` directory, the download is served from the copies, and the copies are deleted when the group finishes — the originals never change tier, so their 180-day archive clock never resets and no re-archive step is ever owed. The copy out of Archive takes as long as a rehydration would (hours, at the rehydrate priority you chose) and the wait shrugs off transient network errors rather than failing the restore. Whatever a crash leaves under `restore-tmp/` is deleted automatically at the next startup — if you see that directory in your container, it is disposable by definition.

#### Network trouble suspends the run instead of failing it

Transient errors — connection resets, timeouts, `5xx`, `408`, `429`, and the `AggregateException` the Azure SDK raises when its own retries are exhausted — no longer end a backup. The worker that hit the wall waits at a gate while every other worker keeps going, and the gate reopens on a ladder of **30s → 1m → 5m, then every 5 minutes**. **Retry now** opens it immediately. While anything is waiting the row reads `Paused — <error> (attempt N)` — with **Retry now** rather than the **Resume** a pause you pressed yourself offers, because there is nothing here for you to lift — and the run's status is still Running: it is holding its staging quota and its slot, and the details of the workers still transferring stay on screen beside it.

That is also why the wait is bounded. The staging limit is global, so a stuck run sitting on 2 GB of finished archives can freeze an unrelated backup — different account, different network path — because nothing will ever free the space. After **10 minutes** with no progress the run therefore gives itself up: journal flushed, quota and locks released, everything else unblocked, and its own progress kept. It ends as **Suspended**, and the row says so (`Suspended after repeated network errors`). Both suspending and downgrading send a notification.

Errors a retry cannot fix — a wrong password, a full disk, a 7-Zip crash, a bad configuration — still fail the run immediately. Pressing **Cancel** during a pause is also never mistaken for a network timeout, even though both surface as the same exception type.

#### Restarts and upgrades

On `SIGTERM` — `docker stop`, a container upgrade — every running backup is suspended, its journal is flushed with `fsync`, and a marker recording *why* it stopped is written beside the journal. The app waits up to **20 seconds** for those runs to settle; anything still uploading a large file when the clock runs out is left mid-flight and comes back as an ordinary interrupted run. Give Docker enough grace period for this to happen at all — see *Shutdown grace period* under **Docker** below.

On the next start, **Resume interrupted backups on startup** (Settings page, on by default) picks those runs back up about 15 seconds after boot, one at a time so they do not queue behind each other on the global compression lock.

**Only runs carrying the planned-shutdown marker are resumed automatically**, and only when *every* journal left for that backup carries it. Everything else waits for you to press Run: a run you suspended by hand (resuming it would erase the intent behind the button), **one that was paused when the shutdown arrived** (the pause itself is memory-only and does not survive, but the intent behind it does — the shutdown records that run as user-requested, so it waits for you rather than starting itself during an upgrade), one that downgraded itself after network trouble (the outage is probably still there, so it would just hit the wall again), and one that died without writing a marker at all. That last group is deliberately broad — a `SIGKILL`, a power cut, a shutdown that timed out, and a cancel all look identical on disk, and at least one of them is you saying *stop*. When a backup is skipped for this reason the log says which journal blocked it and why. The Backups page lists interrupted runs it finds either way, so you can Resume or Discard them yourself.

## Notifications

One HTTP request per event, to a URL you supply. Which events fire is a set of checkboxes; the request itself is
either a `GET` with the values in the URL, or a `POST` with a body template.

Four placeholders are substituted, case-insensitively:

| Placeholder | Substituted as |
|---|---|
| `{Title}` | escaped for where it lands — percent-encoded in a URL, JSON-escaped in a JSON body |
| `{Body}` | the same |
| `{TitleRaw}` | verbatim, no escaping |
| `{BodyRaw}` | the same |

**Use `{Title}` and `{Body}`.** The escaping is what makes a template survive real messages: a backup's closing
notification carries the run summary, which is several lines, and a newline inside a JSON string literal is a
control character that JSON forbids. Substituted raw it produces a payload the receiver answers `4xx` to — and
since the opening notification carries only a container name, which happens to be JSON-safe, the symptom is the
confusing one: *the start notification arrives, the finish notification never does*. A quote in a backup name does
the same thing sooner.

Escaping follows the configured **Content-Type**: `application/json` gets JSON escaping, anything else is left
alone (escaping `text/plain` would put a literal `\n` into the message where a line break belongs). Non-ASCII is
never escaped, so arrows and non-Latin text arrive readable rather than as `\uXXXX`.

The `Raw` pair exists for the one case escaping would break: a template where the placeholder *is* a piece of JSON
structure rather than a value inside a string. Inside an ordinary JSON string it produces exactly the invalid
payload described above.

A working POST template:

```json
{"title":"NASBackup - {Title}","body":"{Body}"}
```

with Content-Type `application/json`. For GET, put the placeholders in the query string
(`https://hook.example/notify?t={Title}&b={Body}`) — they are percent-encoded, so a multi-line body is safe there
too, though some receivers cap URL length.

> A notification that fails is never allowed to fail the backup that triggered it. It is recorded in the
> **operation log** at Warning level with the error, so a receiver rejecting the payload is visible from the UI
> rather than only in the container log.

## Run only if this exists

If your local root is a mount — a NAS share, an external disk, anything that can be absent — set
**Run only if this exists** on that backup, and point it at something that appears *only once the mount is up*:
a marker file inside it, or a subdirectory that is always there when the data is. Pick it with **Browse** (files
are selectable here) or type it; it has to live under that backup's local root, and the line under the box tells
you whether it is there right now.

The reason to bother: an unmounted share is not a missing folder. The mount point is still there, empty. The
scan walks it, finds nothing, and the diff concludes — correctly, by its own rules — that you deleted every
file. That round finishes green, with a tidy summary saying a few hundred thousand files were removed, and
retention starts counting down on the versions that still hold your data. Nothing about it looks wrong until you
need it.

When the path is not there:

- **Backups don't run.** The round is recorded as *Skipped*, in amber rather than red, with one line in the
  operation log. Nothing is written and nothing is recorded, including the backup's own error status — so a
  failure from last week still shows as a failure.
- **Checks drop their local half and keep the cloud half.** Verifying the cloud copy is still worth doing; the
  local comparison would report every file as missing, which is the same false alarm in different clothing. The
  result says which half ran.
- **Restore and repair are unaffected.**

Leave the box empty and the local root itself is used, which is almost always what you want if the root *is* the
mount point: a root that is not there cannot be backed up either way.

## Rule lists, and why each one has two boxes

Four settings take a list of patterns — **Ignore**, **Don't compress**, **Don't group**, **Pack across
directories** — in gitignore syntax, one per line, matched against paths *relative to the backup's local root*.
`!` negates, a trailing `/` restricts a rule to directories, and the **last matching rule decides**.

Each of them is two boxes, and the difference is case:

| Box | Matches | Put here |
|---|---|---|
| the first | exactly | paths — `Photos/2024/`, `Temp/` |
| the second | ignoring case | extensions — `*.mp4`, `*.wmv`, `*.zip` |

The split exists because the two genuinely want different treatment and nothing about a pattern reliably says
which it is. `*.mp4` names a *kind of file*, and a camera writing `.MP4` or an old Windows box writing `.WMV`
produces the same kind — so a rule that only matched lower case would silently miss most of them. A path is the
opposite: on Linux `Temp/` and `temp/` are two different directories, and folding case would quietly widen every
path rule ever written.

Both boxes feed one rule set — the exact ones first, then the case-insensitive ones — so `!` still works across
the pair. One thing it cannot do is re-include a file underneath an excluded directory; that is gitignore's own
rule, not a limitation of the split.

> **There is no character class.** `[wW]` matches those three characters literally: everything that is not `*`,
> `**` or `?` is taken as text. Case-insensitivity is what the second box is for; it cannot be written into a
> pattern.

### Why this matters most for *Don't compress*

Video, audio and already-compressed archives do not get smaller — a real 412 GB run uploaded 47.5 GB out of
48.9 GB of source, a saving of 3%. Compressing them anyway costs more than the wasted CPU: compression is
**globally serial** (one lock across all backups), so every large incompressible file holds that lock while the
upload pipeline sits idle with nothing to send. On that run the in-flight line read *"nothing on the wire right
now · 1 object preparing · 4 objects waiting for the archive slot"* — the network was doing nothing at all.

Listing those extensions under *Don't compress* stores them as-is, and when the backup is unencrypted and the
file fits in one volume it skips the 7-Zip wrapper entirely and uploads the original bytes.

## Docker

The image is self-contained: the backend hosts the API and the compiled SPA on **one** HTTP port (`8080`). Rehydration of Archive-tier blobs, 7-Zip compression, restore and repair all run inside this container, so the host directories you want to back up (and restore into) must be mounted.

Build and run locally:

```bash
docker build -t azurestoragebackup .
docker run -d --name asb -p 8080:8080 --stop-timeout 45 \
  -v asb-data:/data -v asb-keys:/keys -v asb-temp:/temp \
  -v /path/to/files:/backup-source \
  azurestoragebackup
```

Then open <http://localhost:8080>. Inside the app, set a backup's *local root* (and any restore target) to a path that exists **inside the container**, e.g. `/backup-source`.

Or with Docker Compose (`docker compose up --build`), after copying `.env.example` to `.env`.

### Shutdown grace period

`--stop-timeout 45` (`stop_grace_period: 45s` in Compose) is not decoration. Three deadlines are nested, and they have to stay in that order:

```
docker's grace period 45s  >  the app's shutdown timeout 30s  >  the wait for backups to park 20s
```

On `SIGTERM` a running backup parks itself and flushes its journal so it can be resumed later. Docker's default grace period is **10 seconds**, which expires first and turns the stop into a `SIGKILL` — and a killed run is exactly the case the app refuses to resume on its own, because it cannot tell that from a power cut. Raising it to 45s costs nothing when nothing is running: a container with no backup in flight still stops immediately.

### Environment variables

ASP.NET Core maps nested config keys with a double underscore (`Section__Key`). All are optional; defaults shown are the in-container defaults set by the image.

| Variable | Purpose | Default (image) |
| --- | --- | --- |
| `ConnectionStrings__Sqlite` | SQLite connection string (app database). | `Data Source=/data/app.db` |
| `DataProtection__KeysPath` | Directory for the Data Protection key ring used to encrypt secrets at rest (account keys, backup passwords). **Must be persisted** — losing it makes stored secrets undecryptable. | `/keys` |
| `Backup__TempPath` | Working area root: compression, staging, restore, check, dead-weight compaction, and verbose logs live under here. Can grow large during a backup/restore. | `/temp` |
| `Backup__Root` | Confines every local path — backup source, restore target, and the folder picker — to this directory. Unset = no limit. | *(unset)* |
| `Backup__IoPriority` | Block-IO priority for the whole process: `Normal`, `Low` or `Idle`. Set it if backups make the rest of the machine unresponsive — but read the note below first, because most kernels ignore it. | `Normal` |
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

> Azure credentials are **not** configured through environment variables — each storage account is added in the UI and its key is encrypted at rest with the Data Protection key ring in `/keys`. If that directory is lost, the app starts in recovery mode and asks you to re-enter each credential; see [operations.md](docs/operations.md).
>
> **One endpoint, one account entry.** Adding the same storage endpoint twice is refused: two local entries for one real storage account would let two operations (say, a backup and a retention cleanup) run against the same cloud container at the same instant, each blind to the other. Existing duplicates in an old database are left alone, but new additions and edits are checked.

> Tuning values such as the staging-area limit, retention defaults and the dead-weight compaction threshold live in the database, not in environment variables — change them on the **Settings** page and they take effect immediately, without a restart.

> **`Backup__IoPriority`: what it is for, and why it is so often inert.**
>
> The complaint it answers is the machine, not the backup: a NAS that also serves SMB, where a directory listing stalls for seconds while a backup runs. That is contention for the *disk*, and the **7-Zip CPU priority** setting cannot help with it — on Linux that sets `nice`, which is the CPU scheduler and has no reach into the block-IO queue.
>
> `Low` still gets a share of a contended disk, just the smallest one. `Idle` takes disk time only when nobody else wants any — the strongest yield, and enough to starve a backup outright behind a busy fileserver. It covers everything the app reads: the diff, the dedup probe, compression and the uploads alike. It is an environment variable rather than a Settings row because it has to be applied before the app creates its second thread — IO priority is inherited from the creating thread, so there is no later moment at which it could be made to cover the work already running.
>
> **Two things commonly make it do nothing at all, and neither is detectable from inside the container:**
>
> 1. **Only the BFQ scheduler acts on IO priority.** Under `mq-deadline`, `kyber` or `none` the value is accepted and then never consulted. Check which one is in force — the one in brackets — and whether BFQ is even offered:
>    ```
>    cat /sys/block/sda/queue/scheduler
>    ```
>    `none [mq-deadline] kyber` means this setting will change nothing on that disk. Many NAS and virtualised kernels do not ship BFQ at all.
> 2. **It applies to block devices, not to network filesystems.** If the backup source is an SMB/CIFS or NFS mount, those reads never touch a block-IO queue on this machine and no IO priority can rank them. The same is true of a virtual disk inside a VM: the guest can only order requests among themselves before they all funnel through one device to the host, which schedules by its own rules.
>
> The startup log records what was requested and whether the kernel accepted the call — but "accepted" is not "acted on", and only the two checks above can tell you that.

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
| `/data` | SQLite database (`app.db`) **and the backup journals** (`journal/`). | **Yes** |
| `/keys` | Data Protection key ring. Losing it makes stored account keys/passwords undecryptable. | **Yes** |
| `/temp` | Backup/restore working area (compress, staged, diff-spill, restore, check, compact, verbose logs). Safe to discard, but needs free space. | Optional (needs disk space) |
| *(your choice, e.g. `/backup-source`)* | Host directories to back up. Mount **read-only** if you only back up. A backup's *local root* is set to this in-container path. | Bind mount |
| *(your choice, e.g. `/restore-target`)* | Where restores write. Mount read-write. | Bind mount |

`GET /api/system/paths` returns the resolved absolute paths at runtime (PRD §6 "Directories"), useful when configuring Docker volume mappings.

> Journals live next to `app.db` rather than under `Backup__TempPath`, and that is on purpose: `/temp` is the one directory the deployment instructions call *safe to discard*, while the journal is the only thing that lets an interrupted backup pick up where it stopped. Following the database means it lands on a volume you were already persisting, without a second environment variable to get right. It is also never cleared at startup — its whole content is "already in the cloud, not yet in the index".

## Published image

Multi-arch images are published to **two** registries in the same run, under the same tags:

```
ghcr.io/secretnest/azurestoragebackup:latest              # GitHub Container Registry
crpi-xck5ot77uijk4gvl.cn-shenzhen.personal.cr.aliyuncs.com/secretnest/azurestoragebackup:latest       # Azure Container Registry
```

Publishing is a **manual** GitHub Action (`.github/workflows/docker-publish.yml`, `workflow_dispatch`) that builds `linux/amd64` and `linux/arm64` with Buildx and pushes both, tagging each with the tag you type (required, defaults to `latest`) plus the commit SHA. The ACR host and its credentials come from the `ACR_REGISTRY`, `ACR_USERNAME` and `ACR_PASSWORD` repository secrets — the repository path within it is fixed — while the GHCR push uses the workflow's own `GITHUB_TOKEN`. The build cache is kept in GHCR (`:buildcache`) rather than the GitHub Actions cache, because exporting a two-architecture `mode=max` cache to `type=gha` runs *after* the push and has stalled a finished publish for eleven minutes.

The examples elsewhere in this README use the GHCR name; either image is the same build.

## Documentation

Design documentation lives in [`docs/`](docs/README.md), organised by topic. Start with
[`docs/architecture.md`](docs/architecture.md) for what the system is and the principles the rest
follows from, then [`docs/backup-engine.md`](docs/backup-engine.md) for a run end to end.
Full requirements are in [`docs/product-requirements.md`](docs/product-requirements.md), and
[`docs/history.md`](docs/history.md) records how the project got here.
