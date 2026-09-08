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

- **AFTER** (`backend/tests/AzureStorageBackup.Api.Tests/MemoryBenchmarkTests.cs`, commit `dd201d3`): the orchestrator
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
| `dd201d3` | after  | 100,000 | 398.5 MB | 101.1 MB | **64.1 MB** | 285.8 MB | 18.7 MB | 36.7 s |
| `dd201d3` | after  | 200,000 | 534.2 MB | 171.4 MB | **119.7 MB** | 340.3 MB | 20.7 MB | 63.1 s |

The AFTER rows above are from the round-2 re-run (same generator, same file counts, with the forced-collection
sampler added); they supersede round 1's AFTER figures (412.5/94.6/289.1/18.6 MB at 100k, 569.8/164.7/379.1/20.8 MB
at 200k), which differ by ordinary run-to-run variance plus the expected slight perturbation from the added forced
collections. BEFORE rows are unchanged from round 1 — the controller's round-2 instruction did not ask for a
BEFORE re-run, and the round-1 finding that BEFORE scales by the same mechanism as AFTER does not depend on the
forced-collection reading.

`PeakForcedHeapCommittedBytes` (the single `GC.GetGCMemoryInfo().HeapSizeBytes` cross-check, taken at the same
sample as the peak forced-heap reading): 69.4 MB at 100k, 133.3 MB at 200k.
`PostRunForcedHeapPreCleanupBytes` (one more forced collection immediately after the run, before the aggressive
compacting cleanup): 18.7 MB at 100k, 20.7 MB at 200k — indistinguishable from `PostRunManagedHeapBytes` at both
file counts, confirming the aggressive/compacting LOH collect reclaims almost nothing beyond a single ordinary
forced collection once the run has ended.

Raw `benchmark-unique.json` (AFTER, round 2, from
`backend/tests/AzureStorageBackup.Api.Tests/bin/Debug/net10.0/benchmark-unique.json`):

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
fragmentation. Of the candidates named in the paragraph above, the structure with the strongest evidence for this
scaling is the run's own diff/plan set (`diff.Changes`, one record per scanned file): `PackAliasTable.cs` itself
documents that "diff.Changes already holds one FileChange per scanned entry" as the pre-existing baseline against
which the alias table's own memory cost is judged — i.e., production code already treats this set as an
acknowledged, O(file-count) live structure held for the whole run, in both checkouts, independent of whichever
class holds it (`RunWorkDbFactory`'s work database in AFTER, `ILocalIndexCache` in BEFORE). That a live-data
reading — not just an uncollected-garbage artifact — scales at close to the same rate in a benchmark where every
container starts empty (so no historical-index carryover exists to blame) points at this per-run, per-file record
set as the most likely site, rather than at any structure specific to one checkout's design.

The post-run managed heap stays flat regardless of checkout, generator, or file count (15.5–20.8 MB across all
eight runs, no correlation with file count) — whatever holds the per-file-proportional amount at peak is entirely
transient and nothing proportional to file count survives a full collection once the run ends, in either checkout.
The new `PostRunForcedHeapPreCleanupBytes` reading (18.7 MB at 100k, 20.7 MB at 200k) confirms this independently:
it is indistinguishable from `PostRunManagedHeapBytes`, meaning even a single ordinary forced collection right at
run's end — before the production-mirroring aggressive/compacting cleanup runs — already reclaims essentially all
of the per-file-proportional growth.

The AFTER post-run working set is 285.8 MB at 100k and 340.3 MB at 200k files under unique content (round-2
re-measurement) — both comfortably under the 400 MB acceptance target.

## Acceptance check (task brief / controller ruling), evaluated against the primary (unique-content) table

- **AFTER post-run working set at 200k files < 400 MB: met** — 340.3 MB (round-2 re-measurement), a 59.7 MB margin
  under target. The criterion as stated is satisfied.
- **AFTER peak managed heap does not scale with file count between 100k and 200k, within noise: not met, and this
  now holds even against the forced-collection (live-data) reading, which rules out uncollected garbage or
  allocation-rate artifacts as the explanation.** `PeakForcedHeapBytes` grows ~1.87x (64.1 MB → 119.7 MB), a real
  increase of ~55.6 MB per additional 100,000 files; the ordinary (`GC.GetTotalMemory(false)`) reading grows ~1.7x
  (101.1 MB → 171.4 MB, +70.3 MB/100k) over the same interval — close enough to the forced-collection ratio that
  the earlier reading was already substantially a live-data signal, not mostly garbage. **Live data does scale with
  file count in the AFTER build.** This is smaller in absolute terms than BEFORE's growth (~227.6 MB per 100,000
  files, ~1.71x, round-1 ordinary-heap reading) by roughly the same 3.2–3.4x factor seen in the peak-heap totals,
  but it is not flat, and round 2 confirms the non-flatness is a live-data property, not a GC-accounting artifact.
  The structure with the strongest evidence is the run's own diff/plan set (`diff.Changes`, one record per scanned
  file — a baseline production code already treats as O(file-count) per `PackAliasTable.cs`'s own comment), shared
  by both checkouts at very different per-file cost, rather than `PackAliasTable` specifically: switching to unique
  content did not flatten the curve, and the committed-heap cross-check tracks the live-data reading rather than
  diverging from it. Reported as measured; not massaged.
