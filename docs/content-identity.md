# Content identity and deduplication

How the system decides two things: **did this file change?** and **does this content already exist?**
Both are answered from local state alone — a backup issues no cloud read to decide either.

## The three segments

Every file has a content identity made of four fields:

| Field | Covers | Computed by |
|---|---|---|
| `headHash` | the leading 4 KB | `IFileHasher.HeadHashAsync` |
| `tailHash` | the trailing 4 KB | `IFileHasher.TailHashAsync` |
| `fullHash` | the whole file | `IFileHasher.FullHashAsync` |
| `length` | the file size | `FileInfo.Length` |

All three hashes are **XxHash128**, rendered with an `xxh128:` prefix. The segment size is
`DiffOptions.HeadHashBytes`, 4096 bytes by default, and the same value is used for both ends.

The four fields together are the **content identity**. `LocalDedupResolver.ContentKey` composes it,
and it is public precisely so that every path composing one uses the same function.

> **Rationale — why XxHash128 rather than SHA-256.** It is non-cryptographic, several times faster,
> and half the length, which halves the hash bytes in every index. At 128 bits the collision
> probability for content-addressed dedup at personal-backup scale is negligible. CRC was rejected
> outright: its collision rate is far too high to key dedup on without losing data.

> **Rationale — why a tail segment exists at all.** It hardens dedup against a residual collision.
> A false dedup needs `fullHash` **and** `length` **and** `headHash` **and** `tailHash` to all match
> on differing content, which is not achievable in practice. Without the tail, content differing only
> near the end of a large file has one fewer barrier.

### Computing all three in one pass

`IFileHasher.ContentIdentityAsync` reads the file once and feeds every byte to all three segment
hashers, picking up head and tail along the way (`StreamingHasher`).

> **Rationale.** The full-file pass already goes past the head and the tail, so calling the three
> methods separately pays for two extra I/O passes and two extra `open` + `seek` pairs. On a first
> backup of a few hundred thousand small files that is a few hundred thousand redundant seeks — on a
> NAS with spinning disks, not a small number.

The compression path gets the same three values for free: `StreamingHasher` sits in the stream that
feeds 7z, so the bytes hashed and the bytes stored are the same set by construction.

### Opening a source file

`FileHasher.OpenRead` is the only way this project opens a source file, and on Unix it opens with
`O_NONBLOCK`.

> **Rationale.** A FIFO makes an ordinary `File.OpenRead` block forever inside `open()`, waiting for
> a writer — inside a syscall no `CancellationToken` can reach. The whole run wedges, the busy lock
> is held forever, and the UI shows a percentage that never moves. .NET on Unix cannot recognise a
> FIFO by inspection: measured, its `FileAttributes` is `Normal` and its `Length` is 0, exactly like
> an empty regular file. A non-blocking open returns immediately instead, and `CanSeek` then
> separates regular files (always true, empty ones included) from FIFOs and pipes (false). Anything
> judged not a regular file throws `IOException` and takes the existing unreadable-input route, so it
> never enters the upload plan and 7z — a separate process without the flag — never opens it either.
> `O_NONBLOCK` has no effect on read semantics for regular files, so the normal path is untouched.

## The diff chain: proving that content changed

`BackupDiffer.CompareAsync` compares a scanned entry against the previous version's index entry,
asking from cheap to expensive and stopping as soon as the answer is settled:

```
length differs                          → Modified          (no hash read)
length, mtime and permissions all match → Unchanged         (no read at all)
otherwise:
    headHash differs                    → Modified          (4 KB read)
    tailHash differs                    → Modified          (4 KB read, conditional — see below)
    both match → full read:
        fullHash differs                → Modified
        fullHash matches                → MetadataOnly      (index updated, nothing re-uploaded)
```

Each segment here is used to **prove change**. A mismatch is a certain answer, so the expensive pass
below it is never paid for.

**The tail tier is conditional**: it runs only when the full hash can be deferred *and* the previous
entry carries a tail (`deferFull && prev.TailHash is not null`).

> **Rationale.** Files on the deferrable path are by definition over the single-file threshold — a
> few MB up to hundreds of GB — so 4 KB may buy out an entire full-file read. Pack members are the
> other way round: they are small, and once classified `Modified` their `fullHash` still has to be
> computed and written into the index, so exiting early saves nothing and costs one extra
> `open` + `seek`. A null `prev.TailHash` is an old index's transitional state and simply skips the
> probe.

**The full read must not be skipped when head and tail both match.** Only a whole-file pass can tell
"the content really changed" from "the file was merely touched"; skipping it would treat every touch
as a change and re-upload the file.

### What the diff computes for an entry already known to have changed

`BackupDiffer.IdentityAsync` decides which hashes an added or modified entry needs:

- **Single-file blobs** (`DeferrableFullHash`): **only the head**. The full and tail hashes fall out
  of the compression pass for free and overwrite whatever the diff recorded, so computing them here
  is a wasted read. The head is still computed because it doubles as "can this file be opened right
  now" — an unreadable file is classified as such here rather than falling over inside compression
  hours later.
- **Pack members, symlinks, empty files**: all three in one pass, because the index entry needs the
  full hash and the next diff compares against it.

Empty files are stopped in the orchestrator before compression: `Length == 0` is complete information
by itself and restore creates the file from that. Deferring their full hash would leave a permanently
null value in the index, and every subsequent run would reclassify them as changed.

### An unchanged entry reads nothing

`BackupDiffer.Unchanged` carries every field forward from the previous entry, including a null tail.

> **Rationale — why old entries are not backfilled.** A backfill here would be a random read conjured
> out of nothing: measured at 0.033 ms/file on SSD, against 5–10 ms for one random I/O on a NAS
> spinning disk — close to an hour for 500,000 small files. All it buys is moving pack-member dedup
> from a three-component criterion to a four-component one, while the real line of defence has always
> been the 128-bit whole-file hash. The trade does not pay. So pack members in old indexes stay
> without a tail forever and simply do not take part in dedup; any file that gets modified fills it
> in naturally.

## The upload chain: finding an existing copy

`BackupOrchestrator.ProbeAndResumeAsync` runs before anything is compressed. Three tiers, each
settling the item outright on a hit:

### Tier 0 — metadata only, no read

If this run adopted a journal, `JournalResume.FindUntouchedBlob` is asked first: **path + mtime +
length**. A hit means the previous run already uploaded this exact path and the file has not been
touched since, so the recorded storage reference is reused directly.

It is asked **before** the probe because everything below it opens the file. That is the read this
tier exists to avoid, and asking afterwards would avoid nothing. The empty-table check comes before
the `stat`, so an ordinary run with no journal does not pay one extra `stat` per file to be told
there is nothing to resume.

> **Rationale — why metadata is enough here.** The diff calls a file unchanged on
> `length + mtime + permissions`. A file that slips past this test would also have slipped past the
> diff and never entered the pipeline at all, so the resume accepts nothing the run as a whole did
> not already accept. Permissions are left out deliberately: they do not affect content, which is all
> the journal record claims, and the index entry's metadata is rewritten from the current scan
> regardless.

### Tier 1 — the prescreen, 4 KB read

`ProbeForDedupAsync` computes the head hash and asks `LocalDedupResolver.MayDeduplicate(length, head)`.
The key is `length` **and** `headHash` together, and the set it consults has three sources:

| Source | Populated by |
|---|---|
| Retained version indexes | `LocalDedupResolver.Build` |
| Content this run has already started on | `NoteInFlight`, called on every probe |
| Confirmed blocks in an adopted journal | `Build`'s `confirmed` parameter |

**A miss ends the probe.** The caller falls straight through to `StageBlobAsync` — no further reading,
no content identity computed.

> **Rationale.** Streaming compression only learns the full hash once compression is done, so
> "compute the full hash first, then decide on dedup" means reading every file one extra time. This
> prescreen lets a first backup — where not one candidate exists anywhere — take the single-read fast
> path, and pays for the extra pass only when candidates really exist. A false positive costs one
> extra read; a miss would compress content that could have been skipped entirely, so the test is
> deliberately biased towards false positives.

### Tier 2 — the full content identity

A prescreen hit escalates to `ReadContentIdentityAsync`, which reads **the whole file once** and
produces all four fields in that single pass. Two lookups then run, both requiring **all four fields
strictly equal**:

1. `JournalResume.FindBlob` — the copy the previous run already confirmed as uploaded. Path **and**
   content must both match: after an interruption the file may well have been modified, and reusing
   on path alone would write old content into the index as if it were new.
2. `LocalDedupResolver.TryFindExisting` — an existing blob from any retained version.

A miss on both means the item is genuinely new: it goes to `StageBlobAsync`, and `ResolveAsync`
claims an address for it during the upload.

> **Rationale — why there is no tail tier here.** The diff's ladder exists to avoid the full read.
> Here the full read is owed anyway the moment a candidate exists, because dedup needs the full hash
> and nothing cheaper can supply it. Inserting a 4 KB probe between the prescreen and the full pass
> would add an `open` + `seek` and could only ever save the cases the prescreen already let through —
> a set that is small by construction.

### The two head hashes point in opposite directions

This is the part most easily confused, because the same 4 KB hash appears in both chains with
inverted meaning:

| | The diff's head/tail | The upload prescreen's head |
|---|---|---|
| Question asked | did this content change? | might this content already exist? |
| A **mismatch** means | certainly changed — stop, skip the full read | certainly new — stop, skip the full read |
| A **match** means | inconclusive, go on to the next segment | candidates exist, pay for the full read |
| Cost of being wrong | none; a mismatch is a certain answer | a false positive costs one extra read; a miss compresses for nothing |

The diff uses the segment to **exit early and cheaply**. The upload uses it to **decide whether an
expensive pass is worth starting**. Both are a 4 KB read of the same bytes.

## Pack members are decided during the diff

Small files never reach the upload chain's tiers. Their dedup is settled on the diff side, in
`BackupOrchestrator`'s change handler, in two tiers:

```
existing-pack hit (cross-version)  → write the StorageRef directly, no work item
    ↓ miss
this-run alias hit                 → recorded as an alias, not packed
    ↓ miss
register as leader                 → packed as usual
```

**Cross-version**: `LocalDedupResolver.TryFindPackMember` keys on `fullHash + length + headHash` and
then compares the tail, which comes to the same thing as the four-field test — missing counts as
unequal on both sides.

> **Rationale — why the criterion is identical to the single-file path.** Both are answering "is this
> the same content", and getting either wrong points an index entry at somebody else's content and
> produces wrong data at restore time. The criterion was once relaxed to "compare the tail only when
> both sides have one", so that old tail-less pack members could take part; that relaxation is gone.
> The criterion is either all four fields or it is not one, and a compatibility loophole in the one
> place that must least of all be ambiguous is not worth the storage it saves.

**Within one run**: `PackAliasTable` covers content duplicated across packs sealed by this same run,
which the cross-version table cannot see. Duplicates *inside* one pack are already eliminated by 7z's
solid archive — the dictionary matches across members — so what this saves is the cross-pack part,
where no compression dictionary is shared.

An alias is backfilled at the very end, after every consumer has joined, and only if its leader
ended up as a pack member that was neither rewritten mid-compression nor found unreadable. The
decision looks only at the final state, which is why no concurrency primitive is needed. A leader
that went astray orphans its aliases, and those are re-run as ordinary files.

## Coordination within one run

`LocalDedupResolver.ResolveAsync` hands out addresses and keeps concurrent workers from colliding:

- **Same content, same run** — the second arrival finds the first one's reservation, waits on it, and
  receives the same `(ref, raw, volume count)`. The wait is marked as `UploadWait.Peer` so the UI can
  say who is being waited on; it covers the first uploader's **whole item**, which can be minutes.
- **Different content, same address** — the newcomer steps aside to `…~1`, `…~2`, and so on, and the
  actual name is recorded in `StorageRef.Ref` for restore, check and cleanup.
- **A failed upload fails the waiters too**, then withdraws the claim so a retry can take the address
  afresh and really upload a second time.

> **Rationale — why a failed claim is withdrawn.** Without it, a retry of the same content comes back
> to `ResolveAsync`, matches the dead claim on content key, and waits on a completion that never
> succeeds — replaying the same exception forever and never reaching a real second attempt.

## Where the address comes from

`BlobAddressScheme` maps a full hash to a blob name. For an unencrypted backup that is
`data/{fullHash}`. For an encrypted one it is `data/{HMAC(key, fullHash)[:16]}` with
`key = HKDF(password, BackupMeta.KdfSalt)`, and the collision metadata becomes an opaque
`v = HMAC(key, fullHash|length|head|tail)` leaking neither length nor header.

> **Rationale.** Someone who can list the container must not be able to use a publicly known file's
> hash to decide whether it was backed up. Dedup is unaffected — same content, same address. The
> residual leak is blob count and sizes.

## Boundaries

- **A rewrite preserving both mtime and length is not detected.** The diff has lived with this since
  the beginning, and Tier 0 of the upload chain inherits exactly the same boundary — it is not a new
  hole.
- **Old index entries without a tail never dedup.** They match no new file that can compute one. The
  price is that their content is stored once more.
- **Dedup trusts the local index as the cloud's truth.** A blob deleted behind the system's back is
  not re-uploaded by a backup; finding that drift is [check's](check-restore-repair.md) job.

## See also

- [backup-engine.md](backup-engine.md) — where these chains sit in the run
- [packing.md](packing.md) — how pack members are grouped and compacted
- [run-lifecycle.md](run-lifecycle.md) — the journal that Tier 0 and Tier 2 read
- [storage-format.md](storage-format.md) — what an index entry holds
