# Letting a resume trust metadata instead of re-reading every file

## The problem

Resuming an interrupted backup re-reads the source from end to end, even for the files it is about to
skip.

The reason is one line in `PlaceBlobAsync`:

```csharp
control?.Resume.FindBlob(file.Path, p.FullHash, p.Length, p.HeadHash, p.TailHash)
```

`FindBlob` (`JournalResume.cs:87-94`) requires all four content tests to match, and `p` — the content
identity — can only be obtained by reading the whole file (`ReadContentIdentityAsync`). So to answer
"did the previous run already upload this?", the resume reads the file it is going to skip.

Measured on the user's NAS during a real resume: 194.1 GB of source processed, 704.4 MB actually
uploaded. Better than 99% of that read was spent proving that nothing needed to be sent.

The cost lands where it is least welcome. A resume happens after something already went wrong, and the
operator is watching a progress bar that has to crawl through the whole dataset again before it reaches
the part that failed.

## Starting point

- **The journal records everything but the metadata.** `JournalRecord` (`BackupJournal.cs:34-57`) carries
  `Path`, `FullHash`, `HeadHash`, `TailHash`, `Length`, `Volumes`, `VolumeSizes` — and no timestamp.
- **`FindBlob` is keyed by path and gated on content.** `_blobs` is a path-keyed dictionary; the four
  content tests are the gate.
- **The diff already trusts metadata for exactly this judgement.** `BackupDiffer.cs:195-201`: a file whose
  length, mtime and permissions all match the previous version is `Unchanged` — not one byte is read.
  Only a mismatch escalates to head hash, tail hash, and finally a full read.
- **The strictness in `FindBlob` was aimed at a different mistake.** Its comment says reusing **on path
  alone** would be wrong, "after an interruption the file may well have been modified" — and that is true.
  Path alone is not enough. But path *and* metadata is a different proposition, and it is the one the diff
  makes a thousand times a second.
- **`BlobContent` carries the mtime already** (`Mtime`), captured before the read that produced the hashes,
  and `FinishBlobAsync` is where the journal record is written.

## Design

### 1. One optional field on the record

```csharp
/// <summary>
/// The source file's last-write time when this blob was uploaded, as UTC ticks.
/// <para>
/// Nullable because journals written before this field existed have to keep working: a null means "this
/// record cannot answer the cheap question", and the resume falls back to reading the file, exactly as it
/// did before. No format version, no migration — an absent field deserialises to null.
/// </para>
/// </summary>
public long? MtimeUtcTicks { get; init; }
```

Ticks rather than `DateTimeOffset` so the comparison is exact and the JSON is one number: a round-trip
through a formatted timestamp is where "equal" quietly stops meaning equal.

`RecordBlobAsync` gains the parameter and `FinishBlobAsync` passes `content.Mtime`.

### 2. A cheap question, asked before the expensive one

`JournalResume` gains a lookup that reads no file:

```csharp
/// <summary>
/// The previous run uploaded this exact path, and the file has not been touched since. Returns null when
/// the record predates the mtime field, when the path is absent, or when either metadata test fails — in
/// every one of those cases the caller must fall back to the content test.
/// </summary>
public JournalRecord? FindUntouchedBlob(string path, DateTimeOffset mtimeUtc, long length)
```

`ProbeAndResumeAsync` asks it **first**, before `ProbeForDedupAsync` — before any read at all, using the
`FileInfo` the path already gives:

```
stat the file  →  FindUntouchedBlob(path, mtime, length)  →  hit? done, nothing read
               →  miss? the existing route: head hash → pre-filter → full read → FindBlob → TryFindExisting
```

A hit skips the whole probe. A miss costs one `stat`, which the run pays anyway.

### 3. Why this is not a weakening

The new test is `path + mtime + length`. The diff calls a file unchanged on `length + mtime + permissions`
(`BackupDiffer.cs:200`). So a file that slips past this check would also have slipped past the diff and
never entered the pipeline in the first place — the resume is not accepting anything the run as a whole
did not already accept.

Permissions are deliberately left out: they do not affect the *content*, which is all the journal record
claims, and the index entry's metadata is rewritten from the current scan regardless.

What the old strictness bought — and still buys, on the fallback path — is protection against a file
rewritten between the interruption and the resume. A rewrite that also preserves both mtime and length
defeats this check, and it defeats the diff identically. That is a pre-existing, documented boundary of
the whole design, not a new hole.

## What this does not do

**It does not make the first backup faster, or an ordinary incremental one.** The fast path only fires on
records in a journal, which exists only for a run that was interrupted.

**It does not help the pack path.** `FindPack` matches on the member set, and its members carry content
hashes computed by the diff — for small files the diff computes the full hash anyway, so there is no
second read to save.

**It does not detect a file rewritten with its mtime and length preserved.** See §3; the diff has the same
boundary, and the fallback path is unchanged.

## Tests

- **A resume with an untouched file reads nothing.** With a journal recording a blob and the source file
  untouched, the run reuses it without opening the file. Assert on a hasher that counts reads, so
  "reads nothing" is measured rather than inferred from timing.
- **A touched file falls back.** Rewrite the file after the journal was written; the resume must not accept
  the record on metadata, and must go on to the content test.
- **A length change falls back**, even when the mtime somehow matches.
- **A record without the field falls back.** Deserialise a journal line lacking `MtimeUtcTicks` and confirm
  `FindUntouchedBlob` returns null rather than matching on path alone — this is the compatibility case, and
  it is the one that would silently accept the wrong file if it were got wrong.
- **The round trip preserves the value exactly.** Write a record, read it back, compare ticks.
