# SQLite Index Catalog: Memory Benchmark

Task 23 of `2026-09-07-sqlite-index-catalog.md`. Measures peak process memory of a real backup run before and after
the plan, at two file counts, so a reader can see whether peak managed heap scales with the file count.

## Method

`MemoryBenchmarkTests.cs` (`[SkippableFact]`, gated on `ASB_BENCH=1` then Azurite/7z reachability) generates a
deterministic tree (100 000 files / 1 000 directories, and 200 000 files / 2 000 directories) under
`~/.cache/asb-bench` (not `/tmp`, which is a 7.6 GB tmpfs on this machine), then runs one backup against a real
Azurite instance with an `IBlobUploader` substitute that discards every byte but returns a plausible result (uploads
are not the subject of this benchmark; the index and info-file writes still go through `IBackupInfoStore` to the
real Azurite — small enough not to matter). A background loop samples `Process.WorkingSet64` and
`GC.GetTotalMemory(false)` every 2 seconds and tracks the peak of each; after the run, an aggressive, compacting,
blocking `GC.Collect()` (the same shape as production's `BackupRunner.ReleaseRunMemory`) is forced and the
post-collection values are recorded as the "post-run" figures. Two copies of the same test file exist, differing
only in how `BackupOrchestrator` is constructed:

- **AFTER** (`backend/tests/AzureStorageBackup.Api.Tests/MemoryBenchmarkTests.cs`, labelled `dd201d3` in rounds 1–2
  and `task25` in round 3 — the file's own `Commit` constant, which is what lands in the JSON): the orchestrator
  takes `IVersionCatalogs` (`new TestLocalAuthority(store).Catalogs`, backed by SQLite) and a `RunWorkDbFactory`
  (`TestWorkDbs.New()`) for the scan/diff/plan work database.
- **BEFORE** (run from a plain export of `82bed38`, the release this plan started from): the orchestrator takes
  `ILocalIndexCache` (`new TestLocalAuthority(store).IndexCache`, one in-memory index per container) and no work-db
  factory — that stage did not exist yet.

Run command (both checkouts): `ASB_BENCH=1 dotnet test --filter FullyQualifiedName~MemoryBenchmarkTests`.

**Two generators, two datasets.** The first pass through this benchmark generated every file with identical
1-byte content. That was a mistake in the benchmark, not in the product: with every file byte-for-byte the same,
every file after the first is a pack **alias** of the first (same four-part content hash), so `PackAliasTable` —
which the design deliberately keeps in memory for the run, bounded by the number of duplicate-content files — holds
one entry per file, a property of the benchmark's data and not of a realistic tree. The generator was changed to
give every file unique content (its own relative path, UTF-8-encoded, e.g. `d0042/f0017`), same file counts, same
directory layout, still tiny. **The unique-content runs are the primary result below; the identical-content runs are
kept as a labelled worst-case second table.** Each generator writes to its own output file
(`benchmark-unique.json`, `benchmark-identical.json`) so the two datasets never overwrite each other.

**Round 2: separating live data from uncollected garbage.** `GC.GetTotalMemory(false)`, used for the "peak managed
heap" column below, can include garbage the collector has not reclaimed yet, and under Workstation GC gen2 can grow
with allocation rate rather than reflect true live-data size — so a rising "peak heap" does not, by itself, prove
that *live* data scales with the file count. The AFTER test was extended with a second background sampler that
calls `GC.GetTotalMemory(forceFullCollection: true)` every 10 seconds (forcing a full blocking collection before
reading, at the cost of perturbing the run slightly) and tracks its peak as `PeakForcedHeapBytes`, alongside one
`GC.GetGCMemoryInfo().HeapSizeBytes` committed-heap reading taken at the same moment (`PeakForcedHeapCommittedBytes`)
as a single cross-check, not a repeated series. A third forced-collection reading (`PostRunForcedHeapPreCleanupBytes`)
is taken immediately after the run ends and before the aggressive/compacting cleanup collect, isolating "what one
ordinary forced GC sees the instant the run ends" from whatever the LOH-compacting aggressive collect additionally
reclaims a moment later. This round re-ran AFTER only, at both file counts, with the same unique-content generator;
BEFORE was not re-measured (the question is specific to whether AFTER's live data scales, and the round-1 evidence
already established BEFORE scales by the same mechanism).

**Round 3: the leader map moves out of memory.** Round 2's forced-collection reading established that the residual
growth is *live* data, and the follow-up traced it to a specific structure: `PackAliasTable._leaderByContent`, a
dictionary holding the four-part content key of **every** packed member rather than only of duplicates — one entry
per small file on a first backup. Task 25 moves that half of the table into a per-run SQLite file (`PackLeaderStore`,
`{runId}.aliases.db` beside the run's work database), leaving only `_aliasesByLeader` — the half that genuinely is
bounded by duplicates — on the heap. This round re-ran AFTER only, unique content only, on the same machine with the
same command. Its rows are labelled `task25` rather than by hash because the numbers necessarily predate the commit
that carries them (a commit cannot contain its own hash); the commit is the one whose subject is
`feat(pack): keep the alias table's leader map in a per-run SQLite file`, the sole commit on this branch after
`b0378c4`. The identical-content (worst-case) table was **not** re-measured — see the note under it.

**Round 3, follow-up: four more benchmark invocations to answer two questions the first one raised.** The first
round-3 run came back ~10 s slower than round 2 at both file counts, and it is not visible in a memory table, so
it was chased down rather than left in a cell: (a) the unique-content pair was re-run unchanged, (b) the same pair
was run from a detached checkout of **`b0378c4` — this branch's parent, i.e. the build immediately before Task 25 —
in the same session, on the same machine, within the same hour**, which is the only comparison that separates "the
code got slower" from "the machine is in a different state than it was in round 2", and (c) the store's
`cache_size` was swept over 4 MiB, 16 MiB and 64 MiB, one full pair of runs each, because a per-claim SQLite cost
would be the obvious suspect for a slowdown and cache size is the one knob that changes it. All of it is reported
below under "round 3, durations" and "round 3, cache size".

## Machine facts

- 15 GiB RAM, 8 logical CPUs, `/` on a real disk (not the 7.6 GB `/tmp` tmpfs used for scratch work only).
- .NET SDK 10.0.111, runtime 10.0.11 (`Microsoft.NETCore.App` 10.0.0 rollup), both checkouts.
- GC mode: Workstation, concurrent GC on, for both checkouts — the test host's own `runtimeconfig.json` sets no GC
  properties on either commit, so both fall back to the .NET default for a console/test entry point. (The AFTER
  API project's `.csproj` sets `<ServerGarbageCollection>false</ServerGarbageCollection>` explicitly, documented
  there as a NAS-sharing consideration, but that setting lives in a referenced class library, not the test host's
  own entry assembly, so it has no additional effect here — both checkouts already run Workstation GC by default.)

## Results — primary: unique content per file

| Commit | Checkout | Files | Peak working set | Peak managed heap | Live heap (forced GC) | Post-run working set | Post-run managed heap | Duration |
|---|---|---|---|---|---|---|---|---|
| `82bed38` | before | 100,000 | 494.6 MB | 322.4 MB | — (not re-measured, round 2) | 176.6 MB | 17.3 MB | 24.6 s |
| `82bed38` | before | 200,000 | 737.9 MB | 550.0 MB | — (not re-measured, round 2) | 191.4 MB | 19.4 MB | 42.9 s |
| `dd201d3` | after, before Task 25 | 100,000 | 398.5 MB | 101.1 MB | **64.1 MB** | 285.8 MB | 18.7 MB | 36.7 s |
| `dd201d3` | after, before Task 25 | 200,000 | 534.2 MB | 171.4 MB | **119.7 MB** | 340.3 MB | 20.7 MB | 63.1 s |
| `task25` | after | 100,000 | 385.6 MB | 47.5 MB | **32.5 MB** | 308.1 MB | 18.6 MB | 46.6 s |
| `task25` | after | 200,000 | 478.4 MB | 94.3 MB | **47.0 MB** | 378.9 MB | 21.0 MB | 73.9 s |
| `task25` (2nd run) | after | 100,000 | 371.7 MB | 38.9 MB | **32.7 MB** | 306.1 MB | 18.6 MB | 47.2 s |
| `task25` (2nd run) | after | 200,000 | 479.6 MB | 58.2 MB | **47.5 MB** | 389.4 MB | 20.9 MB | 73.1 s |
| `b0378c4` | before Task 25, **same session** | 100,000 | 390.4 MB | 101.9 MB | **67.8 MB** | 289.5 MB | 18.5 MB | 47.0 s |
| `b0378c4` | before Task 25, **same session** | 200,000 | 517.4 MB | 175.8 MB | **117.8 MB** | 323.6 MB | 20.8 MB | 73.2 s |

The `task25` rows are the current AFTER measurement; the `dd201d3` rows are kept as the same build **before**
Task 25, so the effect of moving the leader map out of memory can be read straight off the table (live heap
64.1 → 32.5 MB at 100k, 119.7 → 47.0 MB at 200k). The `dd201d3` rows are themselves from the round-2 re-run and
supersede round 1's AFTER figures (412.5/94.6/289.1/18.6 MB at 100k, 569.8/164.7/379.1/20.8 MB at 200k), which
differ by ordinary run-to-run variance plus the expected slight perturbation from the added forced collections.
BEFORE rows are unchanged from round 1 — no round asked for a BEFORE re-run, and the round-1 finding that BEFORE
scales by the same mechanism as AFTER does not depend on the forced-collection reading.

`PeakForcedHeapCommittedBytes` (the single `GC.GetGCMemoryInfo().HeapSizeBytes` cross-check, taken at the same
sample as the peak forced-heap reading): **49.7 MB at 100k, 58.5 MB at 200k** in round 3, against 69.4 MB and
133.3 MB before Task 25 — it still tracks the live-data reading rather than diverging from it, and its own growth
across the two file counts has dropped from ~1.92x to ~1.18x.
`PostRunForcedHeapPreCleanupBytes` (one more forced collection immediately after the run, before the aggressive
compacting cleanup): **18.6 MB at 100k, 21.0 MB at 200k** (18.7 / 20.7 MB before Task 25) — indistinguishable from
`PostRunManagedHeapBytes` at both file counts, confirming the aggressive/compacting LOH collect reclaims almost
nothing beyond a single ordinary forced collection once the run has ended.

Raw `benchmark-unique.json` (AFTER, round 3 — Task 25, from
`backend/tests/AzureStorageBackup.Api.Tests/bin/Debug/net10.0/benchmark-unique.json`):

```json
[
  {
    "Commit": "task25", "Checkout": "after", "Files": 100000, "Dirs": 1000,
    "PeakWorkingSetBytes": 404381696, "PeakManagedHeapBytes": 49832624,
    "PeakForcedHeapBytes": 34109608, "PeakForcedHeapCommittedBytes": 52069696,
    "PostRunForcedHeapPreCleanupBytes": 19528256,
    "PostRunWorkingSetBytes": 323059712, "PostRunManagedHeapBytes": 19531792,
    "DurationSeconds": 46.5569603
  },
  {
    "Commit": "task25", "Checkout": "after", "Files": 200000, "Dirs": 2000,
    "PeakWorkingSetBytes": 501608448, "PeakManagedHeapBytes": 98902624,
    "PeakForcedHeapBytes": 49294376, "PeakForcedHeapCommittedBytes": 61336624,
    "PostRunForcedHeapPreCleanupBytes": 22010272,
    "PostRunWorkingSetBytes": 397336576, "PostRunManagedHeapBytes": 22010112,
    "DurationSeconds": 73.8648732
  }
]
```

Raw `benchmark-unique.json` (**`b0378c4`, the commit before Task 25, run in the same session as round 3** — this
is the A/B that separates Task 25's effect from the machine's state; its `Commit` field reads `dd201d3` because that
is the label the constant carried at that commit):

```json
[
  {
    "Commit": "dd201d3", "Checkout": "after", "Files": 100000, "Dirs": 1000,
    "PeakWorkingSetBytes": 409407488, "PeakManagedHeapBytes": 106811976,
    "PeakForcedHeapBytes": 71141128, "PeakForcedHeapCommittedBytes": 91569520,
    "PostRunForcedHeapPreCleanupBytes": 19378488,
    "PostRunWorkingSetBytes": 303558656, "PostRunManagedHeapBytes": 19378360,
    "DurationSeconds": 47.0054966
  },
  {
    "Commit": "dd201d3", "Checkout": "after", "Files": 200000, "Dirs": 2000,
    "PeakWorkingSetBytes": 542507008, "PeakManagedHeapBytes": 184296808,
    "PeakForcedHeapBytes": 123544536, "PeakForcedHeapCommittedBytes": 161111392,
    "PostRunForcedHeapPreCleanupBytes": 21851112,
    "PostRunWorkingSetBytes": 339275776, "PostRunManagedHeapBytes": 21846840,
    "DurationSeconds": 73.1767712
  }
]
```

Raw `benchmark-unique.json` (AFTER, round 2 — the same build before Task 25, measured in an earlier session):

```json
[
  {
    "Commit": "dd201d3", "Checkout": "after", "Files": 100000, "Dirs": 1000,
    "PeakWorkingSetBytes": 417886208, "PeakManagedHeapBytes": 105986392,
    "PeakForcedHeapBytes": 67176160, "PeakForcedHeapCommittedBytes": 72752928,
    "PostRunForcedHeapPreCleanupBytes": 19627952,
    "PostRunWorkingSetBytes": 299728896, "PostRunManagedHeapBytes": 19627880,
    "DurationSeconds": 36.7468253
  },
  {
    "Commit": "dd201d3", "Checkout": "after", "Files": 200000, "Dirs": 2000,
    "PeakWorkingSetBytes": 560140288, "PeakManagedHeapBytes": 179674200,
    "PeakForcedHeapBytes": 125472736, "PeakForcedHeapCommittedBytes": 139750512,
    "PostRunForcedHeapPreCleanupBytes": 21705264,
    "PostRunWorkingSetBytes": 356782080, "PostRunManagedHeapBytes": 21705192,
    "DurationSeconds": 63.0995457
  }
]
```

Raw `benchmark-unique.json` (BEFORE, round 1, from the `/tmp/asb-before` export — not re-measured this round):

```json
[
  {
    "Commit": "82bed38", "Checkout": "before", "Files": 100000, "Dirs": 1000,
    "PeakWorkingSetBytes": 518574080, "PeakManagedHeapBytes": 338091832,
    "PostRunWorkingSetBytes": 185139200, "PostRunManagedHeapBytes": 18148712,
    "DurationSeconds": 24.6409743
  },
  {
    "Commit": "82bed38", "Checkout": "before", "Files": 200000, "Dirs": 2000,
    "PeakWorkingSetBytes": 773627904, "PeakManagedHeapBytes": 576670280,
    "PostRunWorkingSetBytes": 200704000, "PostRunManagedHeapBytes": 20367024,
    "DurationSeconds": 42.8951394
  }
]
```

## Results — worst case: every file has identical content (every file is a pack alias; the alias table is the one per-file structure the design keeps in memory)

| Commit | Checkout | Files | Peak working set | Peak managed heap | Post-run working set | Post-run managed heap | Duration |
|---|---|---|---|---|---|---|---|
| `82bed38` | before | 100,000 | 426.8 MB | 262.9 MB | 172.5 MB | 15.5 MB | 20.3 s |
| `82bed38` | before | 200,000 | 671.7 MB | 435.1 MB | 183.9 MB | 15.9 MB | 37.2 s |
| `dd201d3` | after  | 100,000 | 385.3 MB | 76.5 MB  | 259.1 MB | 17.0 MB | 38.9 s |
| `dd201d3` | after  | 200,000 | 475.3 MB | 130.3 MB | 309.4 MB | 17.1 MB | 66.4 s |

**Not re-measured in round 3, and deliberately so.** The identical-content generator was removed from the test file
in round 1 (the unique-content generator replaced it), so re-running it would mean re-adding a second generator and
a second pair of facts. It would also be the one dataset Task 25 cannot help: with every file byte-identical there
is exactly **one** leader for the whole run — the row count Task 25 moved to disk is 1 — while all 200 000 latecomers
become aliases and hang off that leader in `_aliasesByLeader`, which is the half that stays in memory by design. The
numbers above therefore still describe this build's worst case, and Task 25 is expected to leave them essentially
unchanged rather than to improve them.

Raw `benchmark-identical.json` (AFTER):

```json
[
  {
    "Commit": "dd201d3", "Checkout": "after", "Files": 100000, "Dirs": 1000,
    "PeakWorkingSetBytes": 403984384, "PeakManagedHeapBytes": 80177368,
    "PostRunWorkingSetBytes": 271683584, "PostRunManagedHeapBytes": 17835184,
    "DurationSeconds": 38.8880147
  },
  {
    "Commit": "dd201d3", "Checkout": "after", "Files": 200000, "Dirs": 2000,
    "PeakWorkingSetBytes": 498335744, "PeakManagedHeapBytes": 136652776,
    "PostRunWorkingSetBytes": 324390912, "PostRunManagedHeapBytes": 17952544,
    "DurationSeconds": 66.3724239
  }
]
```

Raw `benchmark-identical.json` (BEFORE):

```json
[
  {
    "Commit": "82bed38", "Checkout": "before", "Files": 100000, "Dirs": 1000,
    "PeakWorkingSetBytes": 447516672, "PeakManagedHeapBytes": 275650104,
    "PostRunWorkingSetBytes": 180834304, "PostRunManagedHeapBytes": 16257776,
    "DurationSeconds": 20.3268034
  },
  {
    "Commit": "82bed38", "Checkout": "before", "Files": 200000, "Dirs": 2000,
    "PeakWorkingSetBytes": 704315392, "PeakManagedHeapBytes": 456297888,
    "PostRunWorkingSetBytes": 192786432, "PostRunManagedHeapBytes": 16694488,
    "DurationSeconds": 37.2273675
  }
]
```

## Reading

The pack-alias theory does not survive the primary (unique-content) data. If `PackAliasTable` — the one structure
the identical-content generator was suspected of stressing artificially — were the dominant source of the residual
growth, giving every file unique content should have flattened the curve (unique content means every file becomes
its own leader in `_leaderByContent`, none becomes an alias, and `_aliasesByLeader` stays empty). It did not:
AFTER's peak managed heap still rises ~1.7x when file count doubles — closer to the identical-content run's ~1.70x
(76.5 MB → 130.3 MB, +53.9 MB/100k) than away from it. BEFORE shows the same pattern: ~1.71x under unique content
(322.4 MB → 550.0 MB, +227.6 MB/100k) versus ~1.66x under identical content (262.9 MB → 435.1 MB, +172.3 MB/100k) —
and BEFORE has no `PackAliasTable` at all (that class does not exist before this plan), yet it scales at almost
exactly the same *ratio* as AFTER in both datasets. A structure that is not there in BEFORE cannot be the
explanation for a scaling ratio BEFORE also shows.

What both checkouts share, and what both datasets share, is a single backup run's transient plan for the files it
is about to act on: `LocalFileScanner`/`BackupDiffer` still have to produce one record per scanned file for this
run (path, hashes, decision) before anything can be packed, staged, or written to the index — call it the run's
diff/plan set. That is not the structure the sqlite-index-catalog plan targeted (its target was the *historical*,
cross-version index that used to live in `ILocalIndexCache` for the whole process lifetime), and this benchmark
never exercises that target at all: every run backs up into a brand-new, empty container, so there is zero prior
version history to hold in memory in either checkout. The ~3.2–3.4x absolute reduction seen at every file count
(BEFORE 322.4 MB → AFTER 101.1 MB at 100k; BEFORE 550.0 MB → AFTER 171.4 MB at 200k, round-2 AFTER figures) is the
plan's real, measured win — the fixed and per-file cost of tracking a run's own diff/plan state dropped by roughly
that factor once it moved off an in-memory dictionary onto a SQLite-backed work database and catalog. But the
*shape* of the curve — some cost still proportional to how many files are in a single run's plan — persists in both
designs, just at very different constants. Given the similar ratio in BEFORE (no `PackAliasTable`, no
`RunWorkDbFactory`) and AFTER (both exist), the most likely remaining structure is this shared per-run diff/plan
set itself (in AFTER, most plausibly the portion of it not yet flushed to `work.db` — the unbounded
`Channel<WriteOp>` in `RunWorkDb.cs:254` has no capacity limit, so if the single writer thread drains slower than
the diff/scan producer enqueues, a backlog of per-file `WriteOp` closures can sit on the heap in proportion to file
count at the moment of peak; `StageTracker` was checked and ruled out — its ledgers are bounded (a 256-sample cap
and an `_active` dictionary sized to in-flight items, not total items processed), and the `DiscardUploader`
substitute retains nothing per call, no captured collection, so it is not a candidate either).

**Round 2: does live data actually scale, and by how much?** `GC.GetTotalMemory(false)` — the "peak managed heap"
column above — can overstate live data, because it can include not-yet-collected garbage, and under Workstation GC
gen2 is allowed to grow with allocation rate rather than track live-data size precisely. The forced-collection
sampler added this round answers the question directly: `PeakForcedHeapBytes`, sampled every 10 s by calling
`GC.GetTotalMemory(forceFullCollection: true)` (a full blocking collection immediately before the read, so
uncollected garbage cannot inflate it), rises from **64.1 MB at 100k files to 119.7 MB at 200k files — a ~1.87x
increase, or about +55.6 MB per additional 100,000 files.** Live data does scale with file count in the AFTER
build; this is not a garbage-collector or allocation-rate artifact. The single `GC.GetGCMemoryInfo().HeapSizeBytes`
committed-heap cross-check taken at the same moment corroborates this rather than diverging from it: 69.4 MB at
100k, 133.3 MB at 200k, a ~1.92x increase — tracking the live-data reading closely (the gap between committed and
live grows only modestly, from 5.3 MB at 100k to 13.6 MB at 200k), which is what you'd expect if the extra
committed space is ordinary segment/generation overhead around genuinely growing live data, not runaway
fragmentation. **This paragraph's original attribution was wrong, and round 3 measured why.** It named the run's own diff/plan
set (`diff.Changes`, one record per scanned file) as the structure with the strongest evidence, on the strength of a
comment in `PackAliasTable.cs` that cited "diff.Changes already holds one FileChange per scanned entry" as an
acknowledged O(file-count) baseline. That reasoning leaned on a comment rather than on the code as it then stood:
`diff.Changes` as a retained per-run collection no longer existed in AFTER — the diff streams its changes through a
callback and into the work database — and the comment was a leftover from before that. The structure actually
holding one live entry per file was in the very class that comment belonged to: `PackAliasTable._leaderByContent`,
the content-key → first-path map, which was populated for **every** packed member and not only for duplicates. The
paragraph above was also too quick to clear `PackAliasTable` on the grounds that unique content leaves
`_aliasesByLeader` empty: that is true of one half of the table and says nothing about the other. Round 3 moved that
half into SQLite and re-measured; see below.

**Round 3: what the leader map was worth, and what is left.** Moving `_leaderByContent` into `PackLeaderStore`'s
per-run SQLite file cut the live (forced-collection) heap from 64.1 MB to **32.5 MB** at 100 000 files and from
119.7 MB to **47.0 MB** at 200 000 — a 2.0x and 2.6x reduction of the same reading, on the same machine, with only
that structure changed. More to the point, it cut the *slope*: live data now grows **+14.5 MB per additional
100 000 files** (32.5 → 47.0 MB, ~1.45x) where it grew +55.6 MB per 100 000 (~1.87x) before, a 3.8x flatter curve.
The leader map was the dominant per-file live structure, exactly as the code owner's reading of
`PackAliasTable.cs:97` predicted. The same-session `b0378c4` pair reproduces this independently of anything that
changed between sessions: 67.8 → 32.5/32.7 MB at 100k and 117.8 → 47.0/47.5 MB at 200k, measured within the same
hour on the same machine. Fitting the two points to `live ≈ a + b·files` gives an intercept of ~18 MB —
which is precisely the post-run steady state this process settles at (18.6–21.0 MB, and unchanged by any of this) —
leaving ~29 MB of per-file live data at 200 000 files, or **~150 bytes per file at peak**. The next suspect for that
residue, with the evidence available: the work database's write channel, `Channel.CreateUnbounded<WriteOp>` at
`RunWorkDb.cs:254`. Every row the scan and the diff produce is handed to it as a closure and drained by a single
writer task at its own pace, with no capacity limit, so a backlog *is* per-file live data at the moment of peak, and
~150 bytes is the right order for a queued closure carrying a `ScanRow`/`DraftRow`'s fields. The alternatives are
weaker: `_aliasesByLeader` is provably empty in this dataset (unique content — pinned by
`PackAliasTableTests.Two_Hundred_Thousand_Distinct_Claims_Leave_Nothing_In_Memory`), `dirRemaining` holds one entry
per *directory* (1 000 and 2 000, not per file), `StageTracker` and the uploader substitute were ruled out in round 1,
and the leader map is now on disk. The cheap experiment that would settle it is to bound that channel (or sample
`_channel.Reader.Count` alongside the forced-heap sampler): if the backlog is the residue, back-pressure on the
producer flattens the remaining slope; if the slope survives, the suspect is wrong and the search continues
elsewhere. That experiment is not part of Task 25 and has not been run.

**Round 3, durations: there is no slowdown, and the four extra runs are what says so.** Round 3's first pair came
in at 46.6 s / 73.9 s where round 2 recorded 36.7 s / 63.1 s — about +10 s at *both* file counts, which is already
the wrong shape for a per-file cost (a per-claim SQLite cost would roughly double from 100k to 200k, not stay
constant). A second round-3 pair reproduced it (47.2 s / 73.1 s), so it was not a one-off. The measurement that
settles it is the same-session run of **`b0378c4`, the commit immediately before Task 25**, in the same hour on the
same machine: **47.0 s at 100k and 73.2 s at 200k** — statistically identical to the Task 25 runs (46.6–47.2 s and
73.1–73.9 s). The ~10 s is a between-session property of this machine, not of this change. The same run confirms
the rest of the comparison is sound: `b0378c4` reproduced round 2's *memory* figures closely (live heap 67.8 MB vs
64.1 MB at 100k, 117.8 MB vs 119.7 MB at 200k) while not reproducing its durations at all, which is the signature
of a machine-state difference (clock/thermal state, page cache, background load) rather than of a code change.
Comparisons of *duration* across sessions in this document should be treated as unreliable for that reason; the
memory readings are reproducible to within a few MB.

**Round 3, cache size: measured, and left at 16 MiB.** `PackLeaderStore`'s `cache_size` is the one knob that would
change a per-claim SQLite cost, and there was a plausible argument for raising it: with production-length keys (the
~124-char `ContentKey` plus a real path) 200 000 rows is 40+ MB of b-tree, so 16 MiB cannot hold it, and the unit
test that claims 200 000 times in well under a second uses much shorter keys than production does. It was swept
over three values, a full 100k+200k pair at each:

| Store `cache_size` | Duration 200k | Post-run working set 200k | Live heap 200k |
|---|---|---|---|
| 4 MiB (`-4096`) | 73.7 s | 377.5 MB | 39.7 MB |
| 16 MiB (`-16384`, shipped) | 73.9 s / 73.1 s | 378.9 MB / 389.4 MB | 47.0 MB / 47.5 MB |
| 64 MiB (`-65536`) | 73.7 s | 415.0 MB | 44.6 MB |

Duration does not move at all across a 16x range of cache size — consistent with the access pattern being one probe
per file over keys with no locality, where a cache four times too small and one sixteen times too small miss at
nearly the same rate. Post-run working set, in contrast, tracks the cache upwards, and at 64 MiB it **breaks the
400 MB acceptance target** (415.0 MB). Below 16 MiB the gain is inside the noise (377.5 vs 378.9 MB). The value
stays at the 16 MiB the task specified. (The live-heap column varies by ~8 MB across rows that should not differ in
managed memory at all — the forced-collection sampler fires every 10 s, so it catches the peak only approximately;
that spread is the resolution of this instrument, and it is worth remembering when reading small differences
anywhere in this document.)

**Round 3, the cost side: post-run working set went up, and that is real.** The same-session `b0378c4` pair makes
one regression visible that cross-session comparison had blurred: post-run working set at 200 000 files is
**323.6 MB before Task 25 and 378.9 / 389.4 MB after it**, a consistent **+55 to +66 MB**, while the post-run
*managed* heap is unchanged (20.8 vs 20.9–21.0 MB). The extra is native, and the obvious candidate — the store's
SQLite page cache — is not enough of it: dropping the cache from 16 MiB to 4 MiB recovered only ~1.4 MB, so most of
it is allocator residue from 200 000 inserts into a 40+ MB b-tree rather than the cache itself. This is the trade
Task 25 makes: ~70 MB less *live managed* data at peak in exchange for ~60 MB more *native* residue after the run.
The acceptance target is still met (378.9–389.4 MB against 400 MB) but the margin is now 11–21 MB rather than the
77 MB `b0378c4` had in this session, and that is the number to watch. What would settle where the residue lives:
sample RSS immediately before and after `PackLeaderStore.DisposeAsync` inside the run, and try one `malloc_trim`
(or equivalent) after the run's aggressive collect to see how much of it the allocator is merely holding rather
than using. Not run; out of Task 25's scope.

The post-run managed heap stays flat regardless of checkout, generator, or file count (15.5–22.0 MB across all
runs in this document — the eight of rounds 1–2 and all of round 3's, no correlation with file count) — whatever
holds the per-file-proportional amount at peak is entirely transient and nothing proportional to file count survives
a full collection once the run ends, in either checkout.
The `PostRunForcedHeapPreCleanupBytes` reading (**round 2**: 18.7 MB at 100k, 20.7 MB at 200k; **round 3**: 18.6 MB
and 21.0 MB) confirms this independently:
it is indistinguishable from `PostRunManagedHeapBytes`, meaning even a single ordinary forced collection right at
run's end — before the production-mirroring aggressive/compacting cleanup runs — already reclaims essentially all
of the per-file-proportional growth.

The AFTER post-run working set is 308.1 / 306.1 MB at 100k and 378.9 / 389.4 MB at 200k files under unique content
(round 3's two runs) — under the 400 MB acceptance target, but by less than before, and the same-session `b0378c4`
pair shows this is a genuine cost of Task 25 rather than the run-to-run noise an earlier draft of this paragraph
took it for: 323.6 MB before, 378.9–389.4 MB after, at 200 000 files, in the same session, with the post-run
*managed* heap unmoved (20.8 → 20.9–21.0 MB). See "round 3, the cost side" above for what is known about where the
extra native residue lives and what would settle it.

## Acceptance check (task brief / controller ruling), evaluated against the primary (unique-content) table

- **AFTER post-run working set at 200k files < 400 MB: met, with a caveat that is now measured rather than
  guessed** — 378.9 MB and 389.4 MB across round 3's two runs, an 11–21 MB margin. Task 25 *raised* this figure:
  the same-session run of `b0378c4` (the commit before it) reads 323.6 MB, so the change costs +55 to +66 MB of
  native, post-run residue while removing ~70 MB of live managed data at peak. The store's page cache accounts for
  only ~1.4 MB of it (measured by dropping `cache_size` from 16 MiB to 4 MiB), and raising the cache to 64 MiB
  pushes the figure to 415.0 MB, which would fail this criterion — which is why the cache stays at 16 MiB. The
  criterion as stated is satisfied; the margin is thin enough that any future change touching native allocation on
  this path should re-measure it rather than assume it.
- **AFTER peak managed heap does not scale with file count between 100k and 200k, within noise: still not met
  after Task 25, but the slope is 3.8x flatter and the reading itself is 2.0–2.6x smaller.** Against the
  forced-collection (live-data) reading, which is the column this criterion has to be judged on:
  `PeakForcedHeapBytes` now grows ~1.45x (32.5 MB → 47.0 MB), **+14.5 MB per additional 100,000 files**, where
  before Task 25 it grew ~1.87x (64.1 MB → 119.7 MB), +55.6 MB per 100,000 — and the same-session `b0378c4` run
  puts the "before" figures at 67.8 MB and 117.8 MB, i.e. the cross-session comparison was not misleading here. The ordinary
  (`GC.GetTotalMemory(false)`) reading grows ~1.98x (47.5 MB → 94.3 MB) — it is now the *looser* of the two, i.e.
  a larger share of what it counts is allocation-rate garbage rather than live data, which is what one expects once
  the live structure it was tracking has gone. The committed-heap cross-check (49.7 MB → 58.5 MB, ~1.18x) still
  tracks live data rather than diverging from it. **Live data still scales with file count, at about 150 bytes per
  file at peak once the ~18 MB fixed intercept is taken out.** The structure that was responsible for the bulk of
  it — `PackAliasTable._leaderByContent`, one entry per packed member — is measured, fixed and gone; the earlier
  attribution to `diff.Changes` was wrong (that collection no longer exists in AFTER; see the correction in the
  Reading section). The next suspect, with its evidence and the experiment that would settle it, is the work
  database's unbounded write channel (`RunWorkDb.cs:254`) — untested, and out of scope for Task 25. Reported as
  measured; not massaged.
