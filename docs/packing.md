# Grouping, packing and dead-weight compaction

Small files are merged into shared 7z archives so the container does not accumulate one blob per
file. This document covers how groups are formed, how duplicate members are avoided, and how a pack
is reclaimed once most of it is dead.

## Grouping

By default, small files in one directory (excluding its subdirectories) are merged into one pack.

| Knob | Default | Effect |
|---|---|---|
| `SingleFileThresholdBytes` | 5 MB | larger files get their own blob |
| Don't-group rules | empty | gitignore syntax; matches get their own blob |
| `GroupCapBytes` | 100 MB | per-group cap, measured before compression |
| Cross-directory rules | empty | matching paths are packed by full-path order, ignoring directory boundaries |

The size limit and the don't-group list apply to **newly added files only**: a file already grouped
or already standalone does not change state because a threshold moved.

Precedence: **don't-group > cross-directory > by-directory**.

### Cross-directory packing

> **Rationale — this came from measurement, not from taste.** Under hash-sharded directory trees
> (Emby/Jellyfin metadata, Git objects, assorted caches — very many directories holding one or two
> files each), splitting by directory drives the pack count towards the file count. Measured: 46,624
> files produced over ten thousand packs, each costing a 7z process and a billable upload request,
> which defeats grouping entirely. Packing by full-path order keeps same-directory files adjacent, so
> locality is not lost. It is empty by default, i.e. byte-for-byte the historical behaviour.

Cross-directory packs are sealed on the diff side, so the consumer side has no ordering constraint
between items and the single-threaded compressor may take work in strict queue order.

### How full is full

Three separate places decide "this group is full" — the by-directory lane, the cross-directory lane,
and the re-queue path for changed members — and all three share one predicate.

Two bounds apply at once: **member count and byte total**.

> **Rationale — both bounds are load-bearing, and both came from measurement.** `argv` has a hard
> ceiling of about 1.73 MB before `execve` fails with `E2BIG`, which a pack of many long paths can
> reach on its own. And 7z's per-member metadata runs about 1.3 KB, so a pack with tens of thousands
> of members costs real memory in the 7z process regardless of how small those members are. A byte
> bound alone misses the first; a count bound alone misses the second.

## Member deduplication

Three layers, each covering what the one before it cannot:

| Layer | Scope | Mechanism |
|---|---|---|
| Solid archive | within one pack | 7z's dictionary matches across members — duplicates cost almost nothing |
| Cross-version | packs from retained versions | `LocalDedupResolver.TryFindPackMember` |
| Cross-pack, within one run | packs sealed by this run | `PackAliasTable` |

The criterion for both lookup layers is **four-way strict equality** on `fullHash`, `length`,
`headHash` and `tailHash`; see [content-identity.md](content-identity.md) for why it matches the
single-file path exactly and why "missing" counts as unequal.

### Why the third layer exists

The member table is built only from historical version indexes, so packs sealed during this run never
enter it. On a first backup, or one adding many duplicate small files at once, identical content
landing in different packs really was stored once per pack — compression dictionaries are not shared
between packs.

### Where the decision happens

```
existing-pack hit (cross-version)  → write the StorageRef directly, file = null
    ↓ miss
this-run alias hit                 → recorded as an alias, file = null, not packed
    ↓ miss
register self as leader            → packed as usual
```

The alias branch ends **exactly like** the existing-pack hit, taking the established "this entry did
not change" path: directory counters decrement as usual, sealing timing is unaffected, no upload slot
is taken and nothing needs settling. The consumer side therefore needs no changes at all.

Ordering cannot conflict with cross-version dedup: if the leader hits an existing pack, later files
with the same content use the same table and the same criterion and also hit the first tier, never
reaching the alias table. So any leader in the alias table is one newly packed in this run.

### Backfill happens at the end

After all consumers join and before entries are built, each leader is checked:

```
storageByPath[leader] is { Kind: "pack" }
  and leader ∉ overrides
  and leader ∉ postDiffUnreadable
      → copy that StorageRef to every alias of this leader
otherwise
      → all its aliases are orphaned
```

The three veto conditions map to the three real ways a leader goes astray:

| Condition | Meaning |
|---|---|
| an override was written for it | its content changed inside the compression window, so **the alias's content no longer equals the leader's** — the correctness red line of the whole feature |
| it is in the post-diff unreadable set | it was unreadable on the second attempt too and was downgraded in place, producing no blob |
| its storage is not a pack, or absent | it grew past the threshold and became a single-file blob, or the whole group was unreadable |

> **Rationale — why the backfill is deferred rather than done as aliases attach.** The decision looks
> only at the **final state** and never tracks intermediate steps, so there is no race where the diff
> thread attaches an alias just as a consumer condemns the leader. That is the entire point of
> deferring it, and it is why no new concurrency primitive is needed anywhere in this feature.

**Orphaned aliases are re-run** as ordinary files, the first one naturally becoming a new leader. They
are split into two pools by compression mode first — one pack has one mode, and that cut must happen
before packing — and store-only is evaluated against **the alias's own path**, since rules match by
path and an alias may live in a different directory from its leader. Orphaned aliases are **not**
deduplicated against each other: reaching this path requires the leader to be rewritten or become
unreadable inside the compression window, which is rare, and storing a few extra copies on a rare
path buys a linear, readable, testable finish.

**Progress counting pairs to zero.** An alias neither enqueues nor reports an item — both sides are
zero, which balances by construction, since an alias genuinely corresponds to no work. On screen:
with no orphans the behaviour is identical to before; with orphans, the bar has already reached 100%
and the finish runs silently for a while. Better a brief unreported stretch on an extremely rare path
than any chance of a wrong denominator on the normal path.

### What aliases do not change

1. The alias table is built only from **this run's** changes and never reads or writes the previous
   index.
2. The reference written is `{Kind="pack", Ref=packId, EntryName=leaderPath}` — byte-for-byte the
   shape cross-version dedup already wrote. No schema change, no new field.
3. Unchanged entries always carry their storage forward and never take this path, so **no existing
   reference is released** and existing packs' dead-byte counts do not move.
4. Two entries in one version index pointing at the same `(packId, EntryName)` was already producible
   when cross-version dedup shipped. This introduces no new shape; it only makes that one common.
5. **No retroactive merging.** Duplicates already in history stay until their versions retire.
   Merging them would mean rewriting old packs — a destructive operation on backed-up data.

**Aliases make a member harder to kill, not easier.** Live members are grouped by entry name, so a
member survives as long as **any** referencing path survives; an alias is one more pin. That is
deliberate — references collecting on older packs make those packs less likely to be rewritten by
compaction. The corollary is that **after the leader's own file is deleted, an alias must still
restore**: the entry name is then kept alive by the alias entry alone, the pack is not deleted, and
the member is still extractable.

> **Known trade-off: aliases degrade "repairable from local".** Local repair looks for exactly one
> local source, and that path is the entry name — the leader's path. Once the local file there is
> gone or changed, the member cannot be repaired, and **every** path referencing it is marked
> unrecoverable, even when a byte-identical file sits at one of the alias paths. This is not a new
> category — cross-version dedup has always had it — but aliases turn it from occasional into
> routine, because within-run dedup fires whenever a backup contains duplicate small files.

> **`PackInfo.Members` narrowed in meaning.** It lists each member's full hash. It used to double as
> "does this pack contain this content" and roughly as "how many references does this pack have".
> Now identical content is registered once per pack, so it equals neither the number of index entries
> referencing the pack nor a way to tell whether content appears twice. No consumer relies on that —
> they all go by entry name or ref — but anyone reaching for `Members.Count` to estimate how much
> dedup saved will get a number that is too low.

## Dead-weight compaction

When files inside a pack are deleted or changed, the old data stays in the archive. Once the dead
ratio by original size exceeds the threshold (30% by default), the pack is reclaimed.

**A file counts as dead only when no valid version references it any more**, so the dead ratio is a
function of the retention policy. Compaction is therefore triggered only when a version retires —
that is the only time dead weight grows — and it runs inside the cleanup pipeline.

### In-place recompression

The pack is recompressed from **only its still-live members** and written over the same pack id,
deleting the old volumes.

> **Rationale — why this rather than "reprocess through planning".** Packs are referenced by
> `packId + entryName`, and live members keep their entry name, so **no version index needs
> rewriting**. That is simpler than the original plan and avoids cross-version index edits entirely.

### Member content comes from local first

Before recompressing, each live member is checked for an identical local copy — hash-confirmed, not
merely matching on length, time and permissions. If it is there, the local file is used and the
download is skipped. Only members missing locally require fetching the old pack from the cloud.

Two consequences:

- **A pack in the Archive tier can still be compacted** when every live member is available locally,
  because that costs no cloud read at all.
- When members are missing locally, whether to download is decided **per data tier**
  (`RepackDownload{Hot,Cool,Cold,Archive}`, defaulting to true/true/true/**false**). Archive is off
  to avoid expensive retrieval and rehydration. If downloading is not permitted, the repack is
  **abandoned** for that pack — the dead weight stays and is recorded for observability.

Existence is checked before hashing, as a short-circuit.

### The safety rule

Compaction overwrites in place, which makes it the most dangerous operation in the system: a pack
holding a/b/c rewritten to hold only c, while b is still referenced by a valid version, is permanent
data loss. So when 7z reports that it dropped a member it could not read, compaction **abandons the
optimisation rather than ever overwriting an intact pack**. See
[backup-engine.md](backup-engine.md) § *7z drops members it cannot read*.

## Restore and check implications

- Restore extracts a pack once and copies `extractDir/{entryName}` to each referencing entry's own
  path. Because content-addressed dedup means several paths can reference one member, restore copies
  for **every** referencing entry.
- Check looks up each entry name in the archive and passes when both entries resolve to the same
  item.
- Retention collects live packs by ref and live members by entry name, so `liveBytes ≤ originalBytes`
  always holds and the dead-byte figure can neither go negative nor trigger compaction spuriously.

## See also

- [content-identity.md](content-identity.md) — the four-field criterion and where pack dedup is decided
- [backup-engine.md](backup-engine.md) — where planning sits in the run, and retention
- [storage-format.md](storage-format.md) — `PackInfo` and the `pack` storage reference
