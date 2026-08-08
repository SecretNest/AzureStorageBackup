# 7-Zip CPU priority (global setting)

## Background

Compression and extraction are the only things this program does that will saturate a CPU. It runs on a NAS, and the NAS is running other things too — a media library, a photo indexer, somebody else's containers. A backup is background work: nobody notices it being slower, everybody notices the machine stalling.

The 7z thread count was already adjustable through `Backup__SevenZipMethodArgs` (`-mmt=N`), but that is an environment variable requiring a container restart, and capping threads only reduces **parallelism**, not **scheduling weight under contention** — one saturated thread can still make the UI stutter. Priority is the other knob: let 7z have only the CPU nobody else wants.

## The setting

```csharp
public enum SevenZipCpuPriority { Lowest = 0, BelowNormal = 1, Normal = 2 }

public SevenZipCpuPriority SevenZipPriority { get; set; } = SevenZipCpuPriority.Lowest;
```

Mapped onto `ProcessPriorityClass`:

| Value | ProcessPriorityClass | Linux nice |
|---|---|---|
| `Lowest` (default) | `Idle` | 19 |
| `BelowNormal` | `BelowNormal` | 10 |
| `Normal` | `Normal` | 0 |

There is no "above normal". Raising priority on Linux requires privileges, and letting compression outrank the web UI has no upside for a background backup program.

**`Lowest` must be 0 in the enum.** The EF migration fills existing rows with 0, so an upgraded database lands on "lowest" naturally — matching the default. The counter-example is `StagedLimitBytes` and `ProcessingMaxAttempts`: their valid defaults are not 0, which is why `GlobalSettingsService.GetAsync` still carries a "if it reads 0, substitute the default" patch. Defining `Lowest` as 0 avoids incurring that debt again.

## How it takes effect

`SevenZipCli.RunAsync` / `RunStreamingAsync` take an optional `Func<ProcessPriorityClass>?`, read immediately after `Process.Start` returns and applied to the process. `SevenZipCompressor` and `SevenZipArchiveCodec` accept the delegate at construction and pass it to every call.

**A delegate rather than a value**, so that saving a change in Settings applies to the next 7z process without restarting the container. Each 7z invocation costs one extra read of the singleton settings row; `StagingArea.Limit()` is called more often and that cost is already proven acceptable.

The reach is therefore every 7z process: backup compression (including the streaming path), restore extraction, deep check, repair, dead-weight compaction, and index encoding/decoding.

### Two traps that must stay in the comments

**Failing to set priority is swallowed unconditionally.** The process may already have exited in those few microseconds (`InvalidOperationException`), and the platform may refuse (`Win32Exception`). Not being able to lower priority is not a compression failure and must never take a backup down with it.

**On Linux, nice is a per-thread attribute.** `setpriority(PRIO_PROCESS, pid)` lands on the main thread only, and 7z's LZMA worker threads inherit the nice value of **the thread that created them** at creation time. Setting it the instant `Process.Start` returns means 7z is still dynamically linking and parsing arguments, with no worker threads created yet, so in practice they all inherit it. The worst case — losing that race — is that some threads stay at the old priority: no effect on correctness, only on effectiveness.

## UI

Settings → Global, one dropdown:

- Label: `7-Zip CPU priority`
- Options: `Lowest (default)` / `Below normal` / `Normal`
- Help text: *Compression and extraction are the most CPU-hungry things this app does. Lowest keeps them out of the way of everything else on the machine — they only get the CPU nobody else wants. Raise it if backups are the reason you bought the machine.*

## Pinned behaviour

The setting round-trips through storage; a legacy row holding 0 reads back as `Lowest` and is not rewritten by the normalisation logic in `GetAsync`; each of the three values maps to the expected `ProcessPriorityClass`; and a real 7z compression run with `Lowest` still succeeds.

That last one deliberately does not read `PriorityClass` back and assert on it — the process may have exited by then, which would make the assertion a race.
