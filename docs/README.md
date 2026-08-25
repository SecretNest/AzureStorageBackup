# Design documentation

These documents describe **how the system works today**, organised by what they cover rather than by
when they were built. The code is authoritative; where a document disagrees with the code, the
document is a bug.

## Start here

| | |
|---|---|
| [architecture.md](architecture.md) | What the system is, the four principles everything else follows from, and how the pieces fit together. **Read this first.** |
| [product-requirements.md](product-requirements.md) | The original requirements as given. The source the rest of the design answers to. |

## The backup path

| | |
|---|---|
| [backup-engine.md](backup-engine.md) | One run end to end: scan, diff, plan, index, finalize, retention. Unreadable-input handling and the recheck rules. |
| [content-identity.md](content-identity.md) | The three-segment hash. How "did this change?" and "does this already exist?" are decided, and why the same 4 KB hash means opposite things in the two chains. |
| [pipeline.md](pipeline.md) | The three concurrent stages, the staging area and its backpressure, the raw in-place upload route, and the volume gate. |
| [packing.md](packing.md) | Grouping small files, three layers of member dedup, and dead-weight compaction. |
| [run-lifecycle.md](run-lifecycle.md) | Pause, suspend, stop and resume. The journal, graceful shutdown and automatic resume. |

## Data and recovery

| | |
|---|---|
| [storage-format.md](storage-format.md) | The container layout, the two index levels, addressing, serialisation and the local cache. |
| [check-restore-repair.md](check-restore-repair.md) | Verifying a backup along two axes, repairing from local files, restoring, and substituting unrecoverable files. |

## Configuration and operation

| | |
|---|---|
| [configuration.md](configuration.md) | What a backup is configured with: default inheritance, the container picker, scope rules, the sentinel path, and changing the local root. |
| [operations.md](operations.md) | The password gate, the local path boundary, key ring loss and recovery, 7-Zip settings, and environment variables. |

## Interface

| | |
|---|---|
| [progress-display.md](progress-display.md) | The item and byte ledgers, the speed clock, the ETA, and the two run lines. What every figure means and what it must never imply. |
| [web-ui.md](web-ui.md) | Design tokens, the two responsive axes, tables and dialogs, the inline edit panel, and how visual claims are verified. |

## History

| | |
|---|---|
| [history.md](history.md) | The milestone plan the project was originally built to, and the per-round delivery record that followed it. **A record of how it got here, not of how it works.** |

---

## How these documents are organised

Four rules, all of them learned the hard way:

**By topic, not by date.** A round that extends an existing topic is merged into that topic's
document rather than filed as a new dated one. A reader asking "how does dedup work?" should find one
document, not five rounds of change proposals to assemble in their head.

**Current state, not change history.** These describe what the system does now. They do not open with
"the problem" or "what this used to do", because a reader trying to understand the system has to skip
that to get to the answer — and after three rounds the accumulated "before" states outweigh the
present one. Where the history genuinely explains a non-obvious choice, it lives inside a
`> **Rationale.**` block, kept visually separate from the description.

**Rationale is preserved, and separated.** Every non-obvious decision carries its reasoning, because
the alternative is that a future change quietly undoes a deliberate trade-off. But reasoning is set
apart from description, so someone looking up a mechanism is not made to read an argument first.

**Neither implementation plans nor completion records are kept.** Planning still happens between
design and implementation, and rounds still get reviewed — but task lists and "what was delivered,
which commits, how many tests" write-ups are scaffolding. Once the work has shipped, the code and the
design document are the sources of truth, and a stale plan is worse than none: it reads like a
specification while describing a shape the code has moved past. Whatever is worth surviving a round
belongs in the design document; what the round cost belongs in the commit history.

## Conventions

- **Code is cited by symbol name**, not by line number — line numbers rot silently, and a wrong one
  sends the reader somewhere plausible and wrong.
- **Measurements are stated as measurements**, with the number and what was measured. "It is faster"
  is not a reason; "measured 46,624 files produced over ten thousand packs" is.
- **Known limitations are documented rather than omitted.** A limitation that is written down is a
  decision; one that is not is a bug waiting to be rediscovered.
