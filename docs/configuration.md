# Backup configuration

What a backup is configured with, which fields can change afterwards, and the two fields that need a
guarded channel of their own.

## Creating a backup

A two-step form.

**Step 1 — basics**, immutable after creation except name and description:

- account and container (a container can be created here)
- the local root path (exactly one)
- name, description
- password (optional — a password means encryption, one switch for both the info file and 7z)
- index tier and data tier

**Step 2 — per-backup settings**, each either set explicitly or inherited from the global defaults.

## Inheritance: `null` means inherit

Sixteen fields have a `Default*` counterpart in global settings and follow one rule with no
exceptions: **NULL means inherit, non-NULL means override.**

| Field | "Explicitly none" is expressed as |
|---|---|
| `MaxVersions`, `MaxAgeDays`, `RetentionMode` | n/a |
| `SingleFileThresholdBytes`, `GroupCapBytes` | n/a |
| `IncludeSymlinks`, `VerboseLogging` | n/a |
| `VolumeBytes` | **`0`** = splitting off |
| `IgnoreRules`, `DontCompressRules`, `DontGroupRules`, `CrossDirGroupRules` | **`''`** = no rules |
| the four `*CaseInsensitive` counterparts of those | **`''`** = no rules |

**Each rule list is two independently inheritable fields**, not one. `IgnoreRules` and
`IgnoreRulesCaseInsensitive` are separate columns with separate `Default*` counterparts, and the same
for the other three lists — that is eight of the sixteen.

> **Rationale.** Overriding "the mp4 rules" for one backup must not silently drag the global path
> rules along with it, nor the other way round. Whether the two halves are then matched as one set or
> two is a different question, settled in [backup-engine.md](backup-engine.md) § *The rule engine*:
> they are concatenated into one, because "the last matching rule decides" has to hold across the pair.

> **Rationale — why `VolumeBytes` moved "off" from `null` to `0`.** That field used `null` to mean
> "splitting off", which collides with inheritance. One rule across all sixteen fields is worth more
> than preserving a per-field convention, and the settings page already said `0 = off`, so the UI
> semantics were already there. The eight rule fields are the mirror image: `null` inherits and `''`
> is explicitly no rules, and `string?` passes through from DTO to entity untouched, so the two stay
> distinguishable.

**`IndexTier` and `DataTier` are not inheritable.** Inheritance means "follows the global setting",
i.e. a change after creation — and the update service refuses to change these after creation. They
are pre-filled from the global defaults on the new form, editable there, and fixed on save. Their
rows show `locked after creation` rather than a checkbox.

> **Rationale.** One form should not contain two checkboxes that behave differently and are
> distinguished only by their caption.

### Resolution happens at use

`ResolvedBackupSettings` takes `(BackupConfig, GlobalSettings)` and produces the effective value of
all sixteen. Four paths go through it: backup, check, cleanup and restore.

> **Rationale — resolution must not fill values in when the configuration is read.** Fill them in at
> read time and the edit screen can no longer tell "an inherited 100" from "a 100 I typed", so saving
> quietly converts inheritance into an override — and the feature cancels itself.

The API response carries **both**: the raw nullable fields, which decide each checkbox's state, and
an `effective` object with everything non-null, shown read-only where a box is ticked.

In the UI, re-ticking a box discards the typed value and sends `null` on save. The form keeps no
hidden draft, because a hidden draft makes what is displayed differ from what will be saved.

**Existing configurations were migrated as overrides**, keeping their concrete values.

> **Rationale.** Silently converting them to follow the global settings would change the behaviour of
> already-running backups without the user knowing — a backup deliberately set to `MaxVersions=10`
> would suddenly become 100. A change like that has to be made by a person, explicitly.

> **Known consequence: inheriting retention is destructive.** `MaxVersions`, `MaxAgeDays` and
> `RetentionMode` being inheritable means lowering the global `MaxVersions` from 100 to 10 causes
> every backup inheriting it to **actually delete the surplus versions** at the next cleanup. That is
> the correct meaning of "follows the default" — the user could produce the same outcome backup by
> backup — but it turns one settings edit into a destructive operation across backups, with no
> indication of the blast radius on screen.

## The container picker

Once an account is chosen, the containers endpoint fills a dropdown, with a fixed final entry
`+ New container…` revealing a live-validated name field.

Two flags come back with each container:

- `has backup` — the cloud info file exists. A hint, not a block: that case usually wants Import.
- `in use by "<name>"` — a local backup configuration already holds it. This option **is** disabled.

**Occupancy is judged locally, and duplicates are refused outright.**

> **Rationale — this came from a real incident.** While a first backup was halfway through, that same
> container showed up in the dropdown as empty: the presence flag only looked at whether the cloud
> info file existed, and that file is the very last thing a run writes. So the container already held
> this run's uploaded data while the cloud carried no marker at all. Following that list, the user
> assigned it to a second backup as well. The two then wrote competing version histories over each
> other, and each one's data blobs were deleted as orphans by the other's retention cleanup.

The `BackupConfig` row exists from the moment it is created, without waiting for any cloud artefact,
so `inUseBy` covers all three cases at once: a backup in progress, a failed backup leaving a partial
result, and the cloud being momentarily unreadable. Create and import both look up
`(accountId, container)` before writing and return 409 if taken, and a unique database index closes
both writes that bypass the endpoint and races inside the check-then-write window.

The migration that added that index **deleted nothing**: existing duplicates were moved aside — the
lowest id in each group kept, the rest renamed to a dotted name Azure can never accept, marked in
error, and given a reason. A duplicate configuration points at real cloud data, and deleting the
local record would only make that data invisible.

**Listing failures are survivable**: on failure the dropdown degrades to a plain text field showing
the reason. Being unable to list must not prevent creating a backup. The backend still does **not**
verify cloud-side existence, because the orchestrator calls `CreateIfNotExistsAsync` on the first
backup — naming a container that does not exist yet is a supported flow.

## Scope: backing up a subset of the root

`BackupConfig.ScopeRules`. Null or empty means everything under the root, which is the default.

> **Rationale — why this is not folded into the inheritance system.** For every other rule field
> `null` means "inherit the global value"; here it means "include everything". Scope is this backup's
> own business and a global default for it is meaningless, so it stays out of
> `ResolvedBackupSettings` and is read straight from the configuration. Do not fold it in out of
> habit.

### Four settled semantics

1. **Subtree semantics.** Ticking a directory is a standing rule, so files added there later are
   included automatically. What is stored is a **boundary**, not a file list.
2. **Orthogonal to ignore rules.** Entries matched by ignore rules still appear in the tree, are
   still tickable, and their tick state is still stored; the backup removes them independently.
3. **Paging for large directories**, with ticking at directory level not requiring expansion.
4. **Moving out of scope counts as deletion** — exactly like changing the ignore rules. New versions
   no longer contain them; old versions still do. The UI warns explicitly on save.

### The rule format

One rule per line: a sign, a space, and a path relative to the root.

```
-
+ photos
+ docs/2026
- docs/2026/tmp
```

A lone `-` is the root rule. The root has no ancestor and its implicit default is "include", so a
`+` root rule is redundant and is never persisted.

`IsInScope(path)` walks up for the **longest matching prefix** rule and takes it; with no match at
all, the answer is "included".

**Two write invariants** keep the set permanently minimal:

1. Each rule's decision must be the **opposite** of its nearest ancestor rule — an identical one is
   redundant and is not persisted.
2. Writing a rule deletes every deeper rule that has it as a strict prefix.

Ticking a node is therefore "write one rule, clear what it covers": one-shot, local, no cycles.

### Two corollaries that carry the whole design

- **A directory is `indeterminate` ⟺ the rule set contains a rule with it as a strict prefix.** By
  invariant 1 a deeper rule necessarily disagrees with its nearest ancestor, so a deeper rule
  existing means the subtree is divided. **No child needs to be loaded** to compute tri-state —
  without this, lazy loading and tri-state would be mutually exclusive.

  It is one-directional, and there is a real corner: with `- docs`, `+ docs/a`, `+ docs/b`, where
  `docs` happens to contain only `a` and `b`, the effect is "everything selected" while the UI shows
  indeterminate. Without loading children there is no way to know whether `a` and `b` exhaust `docs`.
  Indeterminate is the conservative and honest side — it never misreports partial selection as full
  selection — and the backup result is unaffected.

- **The scanner cannot prune on "out of scope"**, because an excluded directory may contain `+` rules
  re-including things. It needs `MayContainIncluded(dir)` = in scope **or** a `+` rule exists beneath
  it.

> **A trap in the scanner.** Whether a directory is recorded in `EmptyDirs` depends on whether
> anything under it was kept. A directory merely being *passed through* — excluded itself, descended
> into only to reach a re-included directory below — must not count. Otherwise `- docs` plus
> `+ docs/2026` records `docs` as an empty directory and restore recreates it out of nothing.

### Why the ignore rule engine is not reused

The scanner does not descend into a directory matching an ignore rule, which kills "exclude `docs/`
but re-include `docs/2026/`" — the single most common operation in this feature. Reusing it would
mean changing the descent logic anyway, giving back the saving, and adding the cost of debugging two
rule systems interfering with each other. The two are also structurally different: glob matching with
last-rule-wins versus exact paths with longest-prefix-wins.

Storing an allow-list of files is likewise out: it contradicts subtree semantics, and a root with
half a million files would inflate the configuration table to tens of megabytes.

### The empty-scope backstop

If the scope removes every file, the scan is empty, the diff judges everything deleted, and an empty
version gets written. That is not data loss — old versions survive — but it is certainly a mistake,
so the orchestrator **fails outright** with a clear message instead.

### Two implementations, one fixture

The decision and write logic is implemented again in TypeScript, about 60 lines.

> **Rationale.** Going through the API would mean a round trip per checkbox, and clicking through a
> tree means dozens of them. The cost is that the two implementations must agree, which is pinned by
> **one shared JSON fixture** read by both the C# and TypeScript tests, so a divergence turns both
> red at once.

### Error handling

| Situation | Handling |
|---|---|
| Expanding a directory with no permission | the row shows `Could not be read` and stays tickable — a scope rule does not require the directory to be readable right now |
| A ticked directory is deleted | the rule stays, matches nothing, and is harmless. **No automatic cleanup** — it may just be temporarily unmounted, and clearing it would erase the user's intent |
| Rule text hand-edited into something invalid | `Parse` skips unrecognised lines rather than throwing |
| The scope removes every file | the backup fails outright rather than writing an empty version |

Scope applies to the backup scan only — restore and check are unaffected — and it is **not** written
into the cloud info file. Like the local root and the ignore rules, it is local device configuration.

## Changing the local root

`LocalRoot` was originally locked with the other base fields. That lock's justification does not
apply to it:

- Local backup state and cached version indexes are keyed by `(AccountId, Container)`, **independent
  of the local path**.
- Index entries store paths relative to the root, and scope rules are relative coordinates too.
- Absolute paths appear in exactly two places: the scanning prefix, and `SourceRootHint` in the info
  file, which is advisory and is rewritten by the next backup.

**So as long as the new path holds the same data, changing the root desynchronises nothing and
triggers no re-upload.** The real danger is not "the content changed" but "an unrelated directory was
entered" — in which case the next backup records the entire backup as fully deleted and fully added,
producing an enormous new version and possibly pushing older versions out under retention. Every
guard below points at that one failure mode.

It also fixed a second gap: importing a backup whose info file has no `SourceRootHint` leaves
`LocalRoot` empty, and a locked field meant the user could not fill it in. Imported configurations
were born half-broken.

### Preview and apply are separate endpoints

```
POST /backup-configs/{id}/local-root/preview   { newRoot }   → validation only, never mutates
POST /backup-configs/{id}/local-root           { newRoot, force }
```

> **Rationale.** Preview is a pure query — idempotent, freely retryable, trying another path leaves
> no trace — and apply's confirmation semantics are independently identifiable in the log. Restore
> already splits estimate from execution the same way.
>
> Unlocking the field on `PUT` instead was rejected: it would route everyday edits like renaming and
> operational actions like migrating a root down one path, and would breach the base-field defence in
> the update service. **The new channel is another door, not a picked lock** — the locking check is
> untouched, and a `PUT` carrying a different `LocalRoot` is still refused.

### Validation, in order

1. **Busy check** — if the account/container is busy, 409 and nothing further.
2. **Path validation** — non-empty, absolute, inside the boundary, exists, is a directory, is
   listable.
3. **Baseline determination** — is there something to compare against? That question is
   **independent of what the current `LocalRoot` is, or whether it is empty.**
4. **Sampling** — up to 200 entries from the latest version's index, stratified.
5. **Graded verdict.**

> **Rationale for step 3's independence.** An early draft returned "no baseline" immediately when
> `LocalRoot` was empty. But a configuration imported without `SourceRootHint` has an empty root
> *and* a full set of version indexes in the local cache — and that is precisely the situation where
> the user is **guessing** at a mount point, which is the last case that should be waved through.

### Matching: existence plus size only

mtime takes **no part** in the verdict; it is counted separately and reported alongside.

> **Rationale.** When data moves across filesystems, mtime precision and preservation are frequently
> inconsistent (rsync without `-t`, differing granularity), so using it as a criterion produces false
> mismatches at scale. And a wrong mtime only means the next backup re-uploads those files, whereas a
> wrong size is what suggests the wrong directory was entered.

Symlink entries are checked only for "exists and is still a symlink" — an index entry's length is
always 0 for one. Entries carrying `UnreadableAt` are **excluded from the pool**: their size was
carried over from a previous version and was never guaranteed to match the disk, so including them
can only manufacture false mismatches.

### Stratified sampling

Entries are bucketed by length (0 / <1 MB / 1–100 MB / >100 MB), each bucket gets a quota
proportional to its share, and within a bucket samples are taken **at even intervals in index order
rather than from the head**.

> **Rationale.** Index order approximates directory order, so taking the head would concentrate every
> sample in the first subdirectory — and a half-right migration where only one subdirectory got
> mounted would go undetected.

When a bucket has fewer entries than its quota, the remainder is redistributed rather than wasted.
Sampling is a pure function: entries in, samples out.

### Graded verdicts

| Match rate | Verdict | Behaviour |
|---|---|---|
| `[95%, 100%]` | `Ok` | apply allowed directly |
| `[5%, 95%)` | `NeedsConfirm` | requires `force: true` |
| `[0, 5%)` including nothing found | `Rejected` | requires `force: true` |
| no versions yet | `NoBaseline` | apply allowed directly |
| index unreadable | `BaselineUnreadable` | requires `force: true` |

Intervals are closed on the left, so a boundary value falls into the more permissive grade.

> **Rationale — why `BaselineUnreadable` is separate from `NoBaseline`.** An earlier draft folded
> "the index could not be fetched" into `NoBaseline`, which is exactly the verdict that waves things
> through without confirmation. A backup **with cloud history whose index cannot be read** — the case
> that most deserves an extra question — would have sailed through as "this backup has never run".

**`Rejected` can still be overridden with `force`.** The user has no command line on the NAS and
cannot look around to investigate, so a hard block with a mistaken verdict would leave no way out at
all. The frontend makes the override a checkbox that must be ticked deliberately.

The report carries the counts, the match rate, the mtime-differs count, a reason, and **up to ten
mismatching paths** — with no command line, "which files actually do not match" has to be on screen,
or a 68% match rate gives the user nothing to judge a forced override by.

### Applying

Only `LocalRoot` changes. Scope rules keep their text, and the local index cache and backup state are
neither invalidated nor cleared — they are independent of the path. One operation log entry records
old root, new root, verdict, match rate and whether it was forced.

Apply **does not trust the preview result sent by the frontend** and reruns the full validation
itself — which is precisely why the inspection has to be a pure query and safely re-entrant. The new
root being unmounted after the preview, or the backup starting between the two calls, is caught by
apply's own pass.

> **Consequence for scope rules.** They are coordinates relative to the root, and after a root change
> **the text is preserved verbatim and never rewritten**. When the new root holds the same data the
> relative structure is identical and the rules keep matching. If the user forces a migration onto a
> differently structured tree, scope matching may go empty or partially fail — the same consequence
> as narrowing the scope by hand, with no data corruption and no cloud versions deleted. This is
> spelled out on the forced-migration confirmation screen.

## Sentinel path: refusing to run on an unmounted source

An unmounted root is not an absent root, and that is the whole problem. The mount point stays on
disk with nothing under it. The scan walks it and finds an empty tree. The diff compares that with
the previous version and concludes, correctly by its own rules, that every file was deleted. One
round records a version in which the entire backup has vanished — and **nothing about it looks like a
failure**: no error, no warning, a green run and a tidy summary saying a few hundred thousand files
were removed. Retention then starts counting down on the versions that still hold the data.

The sentinel turns that into a question asked before the run instead of a conclusion reached after
it. Point `SentinelPath` at something that only exists **once the mount is up** — a marker file
inside it, or a subdirectory that is always present when the data is — and the run checks for it
first.

**Existence only.** Not readability, not emptiness, not a listing. A genuinely empty directory is a
legitimate thing to back up and must not be mistaken for an unmounted one, and every deeper probe is
a new way to fail on a source that is perfectly healthy.

**With no sentinel configured, the local root stands in as one.** A root that is not there cannot be
backed up under any circumstances, so it answers the same question. This is why the backup path has
no separate root-existence test — this is that test, with a better answer when a sentinel is
configured. It also means every config that predates the feature gets the protection without being
edited.

**A configured sentinel must live inside that backup's local root**, which is stricter than the
`Backup__Root` admission filter every other path goes through. The sentinel's job is to vouch for
*this* backup's source; one living elsewhere answers a different question and would go on saying
"yes" while the source it speaks for is gone. Refused with 400 at create and update alike — the
setting stays editable after creation, so the create-time check cannot speak for a value that arrives
later.

**Its existence is deliberately not validated when saving.** The moment an operator configures a
sentinel is very likely a moment when the mount is not there; that is the situation it is for.
Refusing to save would make the setting impossible to enter exactly when it is needed. The form
probes it live instead (`GET /api/system/path-exists`) and reports what it finds — found, not there
right now, or outside the root — without blocking the save. The reason that probe exists at all is
that a typo and an unmounted disk are indistinguishable in a text box, and the difference otherwise
surfaces days later as a backup that has been quietly skipping every night.

### What a missing sentinel does to each operation

| Operation | Effect |
|---|---|
| Backup | Does not start. `RunStatus.Skipped`, one `Warning` in the operation log, persisted status untouched. |
| Check | The **local axis only** is demoted to `LocalCheckLevel.None`. The cloud axis runs in full. |
| Repair | Unaffected. |
| Restore | Unaffected. |

**Backups skip rather than fail.** See
[run-lifecycle.md](run-lifecycle.md#the-status-model) for why `Skipped` had to be its own status. The
short version: an unmounted NAS is not a fault, a red badge every morning is an alarm nobody reads
after a week, and writing `Normal` instead would wipe a genuine earlier failure off a backup that has
not run since. The gate sits in `BackupRunner.RunCoreAsync`, ahead of every piece of network I/O and
before the run control opens a journal, so a round that will not happen costs nothing and cannot fail
for some second, unrelated reason on its way to being skipped. All three entry points — the UI
button, the scheduler and the automatic resume after a restart — share that body, so one check covers
them and no future entry point can forget it.

**Checks lose half, not all of themselves.** The cloud copy is still there and still worth verifying
when the source is not mounted; the local comparison is not, because every entry would come back
`Missing` — the same false alarm, rendered as a failed check instead of a version with everything
deleted. This matters most on the scheduled path, which runs unattended: without the demotion, a
perfectly healthy backup sends a failure notification every night the disk is not mounted. The
demotion is applied in `BackupChecker.CheckAsync`, the one point all three callers (the UI runner,
the scheduler, repair's internal pre-check) pass through.

The report carries `LocalSkippedSentinel` and the log line says so at both ends of the run. That is
not decoration: a column of `NotChecked` cannot tell "nobody asked for a local check" from "asked,
and the source was not mounted", and a reader who assumes the first concludes the backup verified
fine. The check dialog is closed long before anyone reads the result, so the report is the only thing
left that can say it. It does **not** affect `CheckReport.Ok` — the cloud half really did run and
really did pass, and failing the whole check because the other half was inapplicable would just move
the false alarm to a different screen.

**Repair needs no gate.** It rebuilds bad cloud blobs *from* local content, one direction only, so an
unmounted source means "nothing is repairable from local" — the run does less, never something wrong.

## Other configuration

**Groups** hold at least one backup and exist to be targeted by schedules. A schedule on a group runs
its backups **in sequence** — the next starts after the previous finishes, successfully or not.

**Schedules** are cron expressions with a graphical editor for non-technical users, configurable per
backup or per group, for backup, check and cleanup actions.

**Run-record retention** is independent of version retention: that one retains data versions, this
one retains run records and logs. Logs are two-tier — durable audit entries kept until the backup is
deleted or manually cleared by time, and ephemeral diagnostics expiring after 14 days by default.
Verbose per-file logging is off by default and, when on, writes to date-partitioned text files rather
than SQLite.

> **Rationale.** One DB write per file would become the bottleneck of a very large backup, and it
> keeps high-frequency diagnostics out of the queryable audit log.

## See also

- [backup-engine.md](backup-engine.md) — how the scan applies scope and ignore rules
- [operations.md](operations.md) — the `Backup__Root` boundary every path is validated against
- [web-ui.md](web-ui.md) — the form, the scope tree and the inline edit panel
- [product-requirements.md](product-requirements.md) — the original requirements these implement
