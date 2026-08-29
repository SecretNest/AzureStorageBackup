# Volume identity: per-volume hashes, skip-aware replacement, and self-healing

Designed 2026-08-29 with the user, in the aftermath of a real incident (a retention-sweep defect
deleted volumes past the 999th; four large files needed repair over a home uplink). Every rule here
was argued from that incident. The single premise: **a volume's xxh128 is written next to it at
upload time, and equality of that hash is the only thing that ever justifies not uploading a
volume.**

## What Azure can and cannot do

Azure stores what we label but computes nothing: no API hashes an existing blob server-side. So the
label must be written **at upload, atomically** — custom metadata (`x-ms-meta-*`) rides the upload
request and commits with the blob (for chunked uploads, at the final Put Block List). Setting it
afterwards (Set Blob Metadata) is a second request, non-atomic, and touches the ETag: never that.
We use our own xxh128, not Content-MD5 — this project has no MD5 anywhere and is not adding one.

## Writing the label

A volume is compressed to disk first, so at upload time it is read **once into memory**: the same
bytes feed the hash and the upload (no second disk read, no page-cache gamble). Memory bound:
volume size × upload concurrency — ~600 MB at the defaults, acceptable because the ceiling is
explicit. `BlobUploader.LabelMemoryLimit` (256 MB, clearing the default 100 MB volume with headroom)
is the cut-off: a file past it streams exactly as before and goes **unlabelled** — never a wrong
label, never an unbounded buffer. The consequence is a known limitation, not a degraded mode: with
`VolumeBytes` set to GB-scale, the skip machinery is simply **off** for those volumes — the missing
label reads as "different" on every later comparison and the family re-uploads in full. The one
exception is a caller that already holds the bytes' hash — the raw route uploads the source file
whose FullHash the backup computed in the same format — whose label is used verbatim: no buffering,
no recompute, any size.

## The one comparison rule, and where the burden of proof sits

- **Upload side** (deciding whether to skip): *cannot prove identical → treat as different →
  upload.* A cloud volume without the label is different by definition. This converges: the first
  full replacement of a legacy family labels every volume, and skipping starts paying from then on.
- **Check side**: the label is **not consulted at all**. There is no local volume to compare
  against, and "index-recorded hash vs cloud label" compares two copies of the same write — a
  ledger reconciliation, not a byte verification. The check stays exactly as it is (existence +
  size), and the index gains no `VolumeHashes` field: the label's only consumer is the upload path.

## Skipping is positional-blind

The mixture hazard (volumes from two compressions spliced into one family) is real, but hash
equality dissolves it: a kept volume that hashes equal to the new family's same-numbered volume
**is** the new volume, byte for byte. So the rule is per-volume and order-free — equal: skip;
different or unlabelled: overwrite — with no "first mismatch condemns the rest" sequencing. Any mix
of kept and uploaded volumes assembles into exactly the new family.

### What actually matches, in practice

7z is deterministic *enough*, and the design never depends on more than what is measured:

- **Pack path**: byte-identical across runs (compresses by file name, mtime from disk —
  `SevenZipDeterminismTests`).
- **Streaming single-file path**: 7z reads stdin, cannot see the source mtime, and used to stamp
  pack-time into the trailing header — so the **last** volume (header content) and the **first**
  volume (its signature CRCs cover the trailing header) differed between runs, while every middle
  volume was byte-identical. `-mtm=off` removes the stamp, and it **is adopted**
  (`SevenZipCompressor.CompressStreamAsync` passes it on every streaming archive), pinned by two CI
  verifications: same input twice → all volumes byte-identical, and extraction plus restore
  metadata unaffected — restore takes times from the index, the in-archive stamp has no consumer.
  The whole streaming family is deterministic end to end. Era transition needs no analysis: legacy
  volumes carry no label and are therefore "different" without ever being compared.
- **Encrypted backups**: random salt/IV per run — nothing ever matches, everything uploads, which
  is today's behavior exactly.
- If any 7z upgrade breaks determinism, hashes stop matching and volumes upload: **the assumption
  failing costs bandwidth, never correctness.** That asymmetry is the whole reason comparison is
  by bytes rather than by theory.

## Two trust levels, one line apart

The backup path and the repair path skip under different rules, because they know different things:

- **Backup resume** (a stopped run's own family): the blob was never condemned, the labels were
  written by this product's own uploader hours ago — label + length match is enough. The same trust
  if-missing uploads have always extended to mere existence, only far stronger.
- **Repair** replaces a blob a check *condemned*: nothing the cloud says about itself is trusted.
  Label and length only nominate a candidate cheaply; the actual cloud bytes are then **downloaded
  and hashed** before anything is kept. Same-length bit rot under a preserved label — the one damage
  no metadata can see, and exactly what the lifecycle test's corruption tool simulates — is caught
  by that read. The economics still favor the incident: a verification download stands in for a
  re-upload, and on the asymmetric links this product lives on, downlink is the cheap direction.

## One replacement primitive, everywhere

Backup upload of a multi-volume single-file blob and repair's replacement become the same
operation:

1. list the family prefix **with metadata** (per-volume HEADs would be a thousand round-trips
   priced at nothing). The backup path pays exactly one listing — its trim reuses it. The repair
   path (`VolumeBlobIO.ReplaceAsync`) lists twice: once for the labels, and once more for its trim,
   which re-enumerates so the deletion criterion cannot act on a listing the upload has since made
   stale;
2. per volume: label equals the in-memory hash → skip; else → overwrite-upload;
3. **trim**: delete volumes whose *number exceeds the new count* — the criterion is the name,
   never "was not uploaded this run" (skipped volumes are exactly the ones not uploaded this run;
   a bookkeeping-based trim would delete everything the mechanism just saved).

There is deliberately no cloud-side completeness assertion (no "skipped ∪ uploaded ≡ 1..N" re-check
before the index commits): the upload addresses exactly the names 1..N and throws on any failure,
so the set is complete by construction or the run has already failed. The 1..N backstop that does
exist runs at production time, in the **local** volume collector — numbers run 1..N with no gaps
and only the last volume short — which is the point where a gap could actually arise unnoticed.

This *replaces* the old wipe-before-upload (running the wipe first would leave nothing to match).
Trailing stale volumes are the reason trim must run on every family write: the retention sweep
protects `data/` volumes by their **base** name, so an over-count leftover is invisible to it
forever.

What this buys, concretely: a run stopped at volume 900 of 1000 used to mean wipe-and-resend all
1000; now the 900 are verified in place and 100 upload. The claim has an honest boundary: it holds
for **labelled-era families and for retries**. A family uploaded before labels existed carries none,
so its first repair re-uploads the survivors too — "cannot prove identical → upload" — and labels
everything on the way; the incident family that motivated this design (tail volumes deleted from a
~1000-volume family, uploaded pre-label) pays that full first replacement. What the mechanism saves
even there is the retry: an interrupted repair's own uploads are labelled, so resuming it
label-skips every volume the first attempt landed instead of starting the ~97 GB over. The local
read-and-recompress is still paid in full in every case; what is salvaged is uplink, which is the
scarce resource.

Packs meet this path during backup for **this-run retries**: a transient upload failure re-sends
the same pack id, and the listing salvages the volumes the first attempt landed (pack output is
byte-deterministic, compressed by file name). Across runs they do not — pack ids carry a per-run
random tag, so a stopped run's packs are never re-addressed. On repair the id is preserved, and the
primitive applies in full.

## Damage is a first-class fact, and dedup must respect it

`VersionIndex.UnrecoverablePaths` is the single source of truth for "this content's blob is
damaged", and it now drives four things:

1. **restore** — substitution from other versions (as always);
2. **deferred healing** — after every completed backup, marks are stat-filtered and handed to a
   scoped repair (`DeferredRepairs`);
3. **dedup exclusion** — a backup run resolves the marks into a damaged-ref set at start, and
   `TryFindExisting` never offers those refs as dedup targets; the same `IsDamagedRef` test guards
   every probe tier that could short-circuit an upload (the untouched-resume tier, the journal's
   content match, cross-version dedup), so a damaged ref always falls through to the healing
   upload. Without this, a *new* file whose
   content equals a damaged file's would be deduped into a reference to the broken blob — born
   dead with its bytes sitting right there on disk. Excluded, it compresses and uploads through
   the replacement primitive to the same content address — and thereby **heals the family in
   passing**, resurrecting every old version that references it;
4. **forced replacement** — an upload whose target ref is damage-marked must never take the
   "exists → skip" shortcut (`UploadIfMissingAsync`); single-volume blobs go through overwrite,
   multi-volume through the primitive above.

Concurrent same-name uploads cannot happen: backup and repair are mutually exclusive under the
busy lock, so the question never reaches Azure. Sequential double-compression (repair heals a blob
a suspended backup will also produce) costs one wasted compression; the second writer's volumes
all label-match and skip.

## Repair is a run, not a favor

The repair executes like a backup and is displayed like one (its own row under the backup's, same
detail layout):

- **Marks land first.** Before the first object is touched, every problem path the assessment
  found — selected or deferred alike — is marked in every referencing version and **persisted**
  (`RepairAsync`'s pre-mark pass). From that moment the marks state exactly which content is
  broken, whatever happens to the run — which is what dedup exclusion and restore read, so a
  backup running beside a suspended repair sees the truth. Repairing an object then clears its
  marks per ref ("heal one, unmark one"), and healed verdicts span the **whole assessed scope**:
  the pre-check genuinely re-examined the deferred half too, so a deferred path it found Ok sheds
  its marks, and the end-of-run deferred re-marking excludes paths this run proved healthy. One
  object's failure discards nobody else's work — the per-object backstop catches it, keeps its
  truthful start-of-run mark, moves on, and the end-of-run persistence records every success before
  the failure surfaces. The report's unrecoverable list holds **final verdicts** (deferred paths,
  failed objects), never the start-of-run safety marks — or a successfully repaired path would be
  reported unrecoverable for having been pre-marked.
- **Stages on screen**: **Assessing** — the scoped pre-check's cloud probing (volumes as the unit)
  under the repair's own name; the check's Local pass is dropped, everything else passes through
  unrenamed — and **Repairing** — damaged objects, with the count running one ahead to name the
  object *under* repair rather than the ones finished, and byte completion moving mid-object as
  each landed or verified-skipped volume books its per-volume share of the recorded source bytes.
  On the streaming route the preparing row carries a byte fraction (`PackingProgress`), and every
  in-flight line carries a `Wire` flag that decides its verb: the local verification read shows as
  *hashing*, never dressed up as a transfer. A 100 GB file's repair has an honest floor — one full
  read plus one full compression — and the progress display exists so that floor looks like work
  instead of a hang.
- **Stop / pause / suspend**: stop mirrors the backup's; pause is the repair's own
  (`/repair/pause`, `/repair/unpause`) and volume-granular — awaited before each object and each
  volume, so it answers in seconds mid-family, and a paused run stays stoppable and suspendable.
  Suspend persists the original selection **and** the deferred half (Paths and DeferPaths); resume
  re-runs the pre-check and intersects — already-healed files fall out (their blobs check clean),
  half-uploaded files are salvaged volume by volume. No journal: the labels in the cloud *are* the
  resume state.
- **Deference**: while a user's suspended repair exists, the post-backup deferred-repair trigger
  skips (with a log line). A suspension is explicit intent; automation does not step over it.

### Blob-granular, path-accounted

Repair operates on blobs; bookkeeping is per path. Two same-content paths in one version (or
across versions — one copy in v1, two in v2) reference one blob: all of them are flagged by the
check, all marked, and one repair of the blob clears every mark. Selecting one path and deferring
its twin still heals both — they were never separate objects.

### Retirement needs no coordination

If retention retires a version while a repair is suspended (running is impossible — busy lock),
the resume-time re-derivation answers everything:

- the retired version was the blob's **only** referencer → the sweep collects the leftovers, the
  pre-check no longer reports it, the path drops out of the intersection silently. Retention
  decided that content's fate; repair does not resurrect it.
- **newer versions still reference it** → it stays alive, stays flagged (their indexes carry their
  own marks), and the resumed repair heals it for the survivors.

Checks never go quiet about damage: marks do not suppress findings — a damaged blob keeps being
reported until it is actually healed, so no re-check can "lose" a repair opportunity, and a new
check simply refreshes the report the plan is a view over.

## See also

- [check-restore-repair.md](check-restore-repair.md) — the repair plan and deferral semantics
- [backup-engine.md](backup-engine.md) — the upload pipeline this hooks into
- [content-identity.md](content-identity.md) — file-level identity (head/tail/full); this document
  is its volume-level counterpart
