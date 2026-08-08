# Backup scope selection (a subset beneath the root)

## The problem

A backup configuration could only be "everything under the root, minus whatever the ignore rules match". To back up just a few subdirectories, the only route was writing the unwanted parts into the ignore rules **in gitignore syntax** — the user had to know what was under the root, type each one out, and retype the lot to change the scope.

What was needed: after choosing a root when creating a backup, uncheck "back up everything", expand a tree and tick through it — with the scope revisitable later.

## Four settled semantics

1. **Subtree semantics**: ticking a directory is a standing rule, so files added there later are included automatically. What is stored is a **boundary**, not a file list.
2. **Orthogonal to ignore rules**: entries matched by ignore rules still appear in the tree, are still tickable, and their tick state is still stored; the backup still removes them independently. If the ignore rules change later, the old ticks take effect on their own.
3. **Paging for large directories**: load a batch at a time with `Load more` at the bottom. Ticking at directory level does not require expanding it.
4. **Moving out of scope counts as deletion**: exactly like changing the ignore rules. New versions no longer contain them; old versions still do (and can be restored from until retention removes them). The UI warns explicitly on save.

## Starting point

- The scanner had one filter while walking the root: the ignore rule set. A directory matching it was skipped with `continue` and **never descended into**.
- `/api/system/browse` already lazily lists local directories, returns hidden files, respects the path boundary, and flags symlinks pointing outside the root. It truncated hard at 2,000 entries per directory.
- `BackupConfig.LocalRoot` remains locked on the ordinary edit path, but there is now a dedicated, validated migration channel for it (see [change-local-root-design.md](change-local-root-design.md), for when a mount point moves). So the relative basis for scope rules is **not absolutely stable**: after a root change the rule text is preserved verbatim, and it keeps matching correctly when the new root holds the same data. If the user forces a migration onto a differently structured tree, matching may go empty or partially fail — the same consequence as narrowing the scope by hand (semantic 4), with no data corruption.
- The restore dialog already had a lazily loaded tree with tri-state ticks, but its data source is the **cloud version index** (a finite known set, with tri-state computed from loaded descendants).

## Why the ignore rule set is not reused

Translating scope into gitignore rules stored in another field would require almost no backend change — and does not work. The scanner does not descend into a directory that matches an ignore rule, which kills "exclude `docs/` but re-include `docs/2026/`" — the single most common operation in this feature. Using it would mean changing the scanner's descent logic anyway, giving back the saving, and adding the cost of debugging two rule systems interfering with each other.

Storing a full allow-list of files is likewise out: it directly contradicts subtree semantics (new files would not be included automatically), and a root with half a million files would inflate the configuration table to tens of megabytes.

## Design

### 1. Data model: `BackupConfig.ScopeRules`

```csharp
/// Backup scope. null/empty = everything under the root (the default).
/// **Not inheritable** — scope is this backup's own business and a global default is
/// meaningless, so it stays out of ResolvedBackupSettings and is read straight from the config.
public string? ScopeRules { get; set; }
```

For the other rule fields `null` means "inherit the global value"; for this one it means "include everything". That difference is deliberate — do not fold it into the inheritance system out of habit.

**Text format**, one rule per line: a sign, a space, and a path relative to `LocalRoot` (`/`-separated, no leading or trailing slash):

```
-
+ photos
+ docs/2026
- docs/2026/tmp
```

A lone `-` is the root rule. The root has no ancestor and its implicit default is "include", so by invariant 1 the root rule can only ever be `-`; a `+` root rule is redundant and is not persisted.

**The decision** `IsInScope(path)` walks up the path for the **longest matching prefix** rule and takes it; with no match at all, the answer is "included".

**Two write invariants**, which keep the rule set permanently minimal and stop it growing without bound:

1. Each rule's decision must be the **opposite** of its nearest ancestor rule — an identical one is redundant and is not persisted.
2. Writing a rule deletes every deeper rule that has it as a strict prefix — they are now covered.

Ticking a node is therefore just "write one rule, clear what it covers": one-shot, local, no cycles.

### 2. Two corollaries that come for free

These are why lazy loading and tri-state can coexist, and they carry the whole design:

- **A directory shows as `indeterminate` ⟸ the rule set contains a rule with it as a strict prefix.** By invariant 1, a deeper rule necessarily disagrees with its nearest ancestor, so "a deeper rule exists" means the subtree is divided. **No child needs to be loaded** to compute tri-state — without this, lazy loading and tri-state would be mutually exclusive.

  This is **one-directional**, and there is a real corner: with `- docs`, `+ docs/a` and `+ docs/b`, where `docs` happens to contain only `a` and `b`, the effect is "everything selected" while the UI shows indeterminate. Without loading children there is no way to know whether `a` and `b` exhaust `docs` — an inherent cost of lazy loading, and the restore dialog's tree has the same limitation. Indeterminate is the conservative and honest side: it faithfully reports "explicit rules are at work here" and never misreports partial selection as full selection. The backup result is unaffected; only the display is.

- **The scanner can no longer use "if the directory is out of scope, do not descend"**: an excluded directory may contain `+` rules re-including things. So a second method is needed, `MayContainIncluded(dir)` = the directory is in scope **or** a `+` rule exists with it as a prefix.

### 3. `ScopeRuleSet` (pure logic, no I/O)

A peer of the ignore rule set but deliberately **not** shared with it: that one is glob matching with last-rule-wins, this one is exact paths with longest-prefix-wins, and merging them would complicate both.

```csharp
public static ScopeRuleSet All { get; }           // empty set = include everything
public static ScopeRuleSet Parse(string? text);   // null/empty → All; invalid lines skipped, never thrown

public bool IsInScope(string relativePath);       // longest prefix match
public bool MayContainIncluded(string dirPath);   // any + rule left in the subtree?
public bool IsPartial(string dirPath);            // tri-state: a deeper rule exists

public ScopeRuleSet With(string path, bool included);  // maintains both invariants, returns a new instance
public override string ToString();                     // back to text
```

Internally a `SortedDictionary<string, bool>` (Ordinal). `IsInScope` splits the path level by level and looks up, O(depth). `MayContainIncluded` and `IsPartial` scan linearly for prefixes — the rules are boundaries the user clicked out one at a time, capped at a few dozen, and a linear scan is both simpler and faster than binary search. No interval index here, deliberately.

Under Ordinal ordering an ancestor always sorts before its descendants (a strict prefix compares less than any extension of it), so normalisation can clear redundant rules in a single forward pass.

### 4. Scanner integration

The only change on the backup's main path. `ScanOptions` gains a `Scope`, applied **after** the existing ignore check:

```csharp
if (isDirectory && !isSymlink)
{
    // Excluded, and no re-inclusion anywhere beneath → prune the whole subtree, do not descend.
    // IsInScope alone is not enough: an excluded directory may still contain + rules.
    if (!scope.MayContainIncluded(relative))
        continue;
    ScanDirectory(...);   // see below: keptChildren must not increment unconditionally
    continue;
}
if (!scope.IsInScope(relative))
    continue;             // file out of scope
```

**A trap that must be called out**: `keptChildren` decides whether a directory is recorded in `EmptyDirs`. A directory merely being *passed through* — excluded itself, descended into only to reach a re-included directory below — must not count as a kept child. `ScanDirectory` therefore has to report whether anything was actually kept in the subtree, and only then increment. Otherwise `- docs` plus `+ docs/2026` records `docs` as an empty directory, and restore recreates it out of nothing.

### 5. The empty-scope backstop

If the scope removes every file, the backup scans zero entries, the diff judges everything deleted, and an empty version is written. That is not data loss (old versions survive), but it is certainly a mistake. When the scan result is empty and `ScopeRules` is non-empty, the orchestrator **fails outright** with a clear message rather than quietly writing an empty version.

### 6. Paging for browse

The original implementation collected everything, then sorted, so truncation happened during collection — which made the truncated result's ordering wrong and paging impossible. It now enumerates directories and files separately (which yields `isDir` for free, with no stat), sorts each, concatenates, slices by `offset` / `limit`, and stats **only the current page** for length and mtime. The response carries `Total` and `Offset`.

The old truncation semantics are kept for callers that pass no paging parameters, so the path browser is unaffected.

### 7. The scope tree component

Not a reuse of the restore dialog's tree: that one's source is the cloud version index, this one's is a live filesystem (unbounded, changing, with tri-state computed from rules). They only look alike; their cores are opposites, and merging them would make both fragile. They share row styling visually.

Three pieces of state, one source of truth:

```
rules      — the frontend mirror of ScopeRuleSet (the only truth; serialised to text on save)
children   — Record<path, Entry[]>, a lazy-load cache, purely presentational
expanded   — Set<path>, purely presentational
```

A node's tick state is **always computed from `rules`, never stored**:

```ts
isPartial(path) ? 'indeterminate' : isInScope(path) ? 'checked' : 'unchecked'
```

**Which is why there is no feedback loop**: a click calls `rules.with(path, !checked)` once and stops. A parent's state is recomputed on the next render, and so is a child's. There is no "child updates parent updates child" propagation, because there is no propagation at all. All four cascade requirements are corollaries of this model and need no separate implementation:

| Requirement | Follows from |
|---|---|
| Parent ticked → all children included | No deeper rules in the subtree → all children render as ticked |
| Parent unticked → all children excluded | The same, inverted |
| All children ticked → parent shows ticked | Those child rules were judged redundant by invariant 1 and cleared |
| Otherwise parent shows indeterminate | A deeper rule exists → `isPartial` is true |

### 8. Two implementations of the rule logic

The decision and write logic is implemented again in TypeScript (about 60 lines). This is **deliberate duplication**: going through the API would mean a round trip per checkbox, and clicking through a tree means dozens of them. The cost is that the two implementations must agree — pinned by **one shared JSON fixture** read by both the C# and TypeScript tests, so a behavioural divergence turns both red at once.

### 9. Rows and the entry point

A row is a checkbox, a name, an expand arrow for directories, and badges. There are two badges, both purely informational:

- `ignored` — matched by the current ignore rules. Still shown, still tickable, state still stored, and still removed independently at backup time. The badge carries a tooltip saying so, or users assume ticking it means it will be uploaded.
- `outside root` — a symlink pointing outside the root, reusing browse's existing flag, greyed out and not tickable.

Hidden files are not filtered at all — browse already returns them, and this is a "what we do not do".

Paging: 500 entries per directory initially, with `Load more (showing 500 of 12,431)` at the bottom; what is loaded is never discarded.

The entry point is a checkbox under the `LocalRoot` field reading `Back up everything in this folder`, ticked by default. Unticking reveals the tree, whose first level is the root node alone, **initially ticked** (equivalent to current behaviour), and the user removes from there — first-time configuration is most often "almost everything, except a few directories", and starting from nothing selected would mean ticking from scratch. Editing an existing configuration deserialises from `ScopeRules`. Saving goes with the configuration form; there is no separate endpoint.

**A warning on save**: if this edit moved paths that were previously in scope out of it, confirm before saving — by semantic 4, those files will be treated as deleted by the next backup. The check compares the old and new rule sets and does not touch the filesystem.

## Error handling

| Situation | Handling |
|---|---|
| Expanding a directory with no permission | Browse already returns 403; the row shows `Could not be read` and the node stays tickable — a scope rule does not require the directory to be readable right now |
| A directory is deleted after being ticked | The rule stays in the set, matches nothing during a scan, and is harmless. **No automatic cleanup**: the directory may just be temporarily unmounted, and clearing it would erase the user's intent |
| Rule text hand-edited into something invalid | `Parse` skips unrecognised lines rather than throwing, consistent with how the ignore rule set treats blank lines and comments. The save path generates rules from the UI and cannot produce invalid lines |
| The scope removes every file | The backup fails outright with a clear message rather than writing an empty version (§5) |
| A directory in scope cannot be read | Takes the existing unreadable-path route, unrelated to this feature and unchanged |

## Pinned behaviour

Longest-prefix matching; `MayContainIncluded` correctness across alternating `+`/`-` levels; both write invariants (a redundant rule is not persisted, and writing a rule clears deeper ones); `Parse` / `ToString` round-tripping. The decision and write cases are recorded once as JSON and read by both language implementations (§8).

Scanner tests build a real temporary tree: a whole subtree excluded; a subdirectory re-included beneath an excluded directory (it must genuinely descend); **a merely passed-through directory not entering `EmptyDirs`** (the §4 trap, pinned on its own); and scope and ignore rules both in effect, independently.

## Out of scope

- Restore and check are unaffected — scope applies to the backup scan only.
- Scope is not written into the cloud info file. Like `LocalRoot` and the ignore rules, it is local device configuration.
