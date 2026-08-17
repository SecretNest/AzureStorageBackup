# Resume speed, raw upload, and the error badge — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Three independent improvements, shipped together: a resume stops re-reading files it is going to skip; a store-only unencrypted upload stops copying itself into staging first; and the persistent error badge stops being timeless.

**Architecture:** Three unrelated changes sharing one branch and one release. B adds an optional field to the journal record and asks a cheap question before the expensive one. C changes which path a raw upload reads from, guarded by a metadata check on both sides of the upload. E is a frontend rendering change over a field the backend already stores.

**Tech Stack:** C# / .NET, xUnit, React + TypeScript, vitest. Integration tests run against Azurite.

## Global Constraints

- **Everything written into the repository is English** — code, comments, commit messages, docs. (Conversation with the user stays Chinese.)
- **Commit messages end with:** `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`
- **Integration tests need Azurite**: `npx azurite --skipApiVersionCheck`. **If they fail or slow sharply, run `df -h /tmp` first** — `/tmp` is tmpfs on this machine and testing has filled it twice, presenting once as timeouts and once as `Disk quota exceeded`. Report it rather than working around it.
- **Typecheck the frontend with `npx tsc -b`, never `tsc --noEmit`** — the latter passes vacuously on this project.
- **Do not change the index or info-file formats.** B adds an optional field to the *journal* record, which is JSONL and tolerates absent fields; nothing else may change shape.
- **A blob's address is its content.** Anything that could put bytes at `data/{hash}` that do not hash to `{hash}` is a correctness failure, not a performance trade-off.
- Specs: `docs/journal-mtime-fast-resume-design.md` (B), `docs/raw-upload-without-staging-design.md` (C).

## File Structure

| File | Responsibility |
|---|---|
| `backend/src/AzureStorageBackup.Api/Services/BackupJournal.cs` | B: the optional mtime on `JournalRecord`. |
| `backend/src/AzureStorageBackup.Api/Services/BackupRunControl.cs` | B: carry the mtime into the record. |
| `backend/src/AzureStorageBackup.Api/Services/JournalResume.cs` | B: the metadata-only lookup. |
| `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs` | B: ask it first. C: the raw route and its guard. |
| `frontend/src/lib/errorBadge.ts` | E: **new.** When and how a persistent error renders. |
| `frontend/src/pages/BackupConfigsPage.tsx` | E: render it. |

---

## Part B — a resume that trusts metadata

### Task 1: The journal remembers when the file was last written

**Files:** `BackupJournal.cs`, `BackupRunControl.cs`, `BackupOrchestrator.cs` (the one call site), `backend/tests/AzureStorageBackup.Api.Tests/BackupJournalTests.cs`

**Interfaces produced:**
- `JournalRecord.MtimeUtcTicks` — `long?`, absent on records written by older versions.
- `BackupRunControl.RecordBlobAsync` gains a `DateTimeOffset mtimeUtc` parameter.

- [ ] **Step 1: Write the failing round-trip test**

In `BackupJournalTests.cs`, following its existing fixtures: append a blob record carrying an mtime, read the volume back, assert the ticks survive exactly. Then append a line **without** the field (write the JSON by hand, omitting it) and assert it deserialises with `MtimeUtcTicks == null` rather than throwing — that is the compatibility case, and it is the one that matters most.

- [ ] **Step 2: Run it, confirm it fails to compile**

- [ ] **Step 3: Implement**

Add to `JournalRecord`:

```csharp
/// <summary>
/// The source file's last-write time when this blob was uploaded, as UTC ticks.
/// <para>
/// Nullable because journals written before this field existed must keep working: null means "this record
/// cannot answer the cheap question", and the resume falls back to reading the file exactly as it did
/// before. No format version and no migration — an absent field deserialises to null.
/// </para>
/// <para>
/// Ticks rather than a formatted timestamp so the comparison is exact. A round trip through a rendered
/// time is where "equal" quietly stops meaning equal.
/// </para>
/// </summary>
public long? MtimeUtcTicks { get; init; }
```

Thread it through `RecordBlobAsync` and its call site, which has the value already — the `BlobContent` that produced the hashes carries `Mtime`, captured before the read.

- [ ] **Step 4: Run the test, then the full suite. Commit.**

```
feat: record a blob's mtime in the journal

The resume's reuse test needs a content identity, and a content identity
costs a full read. Recording the source's last-write time is what will let
the next task answer the same question from a stat.

Optional by construction: an older journal deserialises it as null and the
resume falls back to reading, so no migration and no format version.
```

### Task 2: Ask the cheap question first

**Files:** `JournalResume.cs`, `BackupOrchestrator.cs` (`ProbeAndResumeAsync`), `backend/tests/AzureStorageBackup.Api.Tests/BackupResumeTests.cs`

**Interfaces produced:**
- `JournalResume.FindUntouchedBlob(string path, DateTimeOffset mtimeUtc, long length)` → `JournalRecord?`

- [ ] **Step 1: Write the failing tests**

Four cases, and the last two are the ones that would let a wrong file through:

1. An untouched file is reused **without being read**. Assert on a counting hasher, not on timing — "read nothing" must be measured.
2. A file whose mtime moved falls back to the content test.
3. A file whose length changed falls back, even if the mtime somehow matches.
4. A record with no `MtimeUtcTicks` falls back rather than matching on path alone.

Check `BackupResumeTests.cs` for how it builds a journal and a run; follow it rather than inventing a fixture.

- [ ] **Step 2: Run them, confirm they fail**

- [ ] **Step 3: Implement**

```csharp
/// <summary>
/// The previous run uploaded this exact path and the file has not been touched since.
/// <para>
/// Returns null when the record predates <see cref="JournalRecord.MtimeUtcTicks"/>, when the path is
/// absent, or when either metadata test fails — in every one of those the caller must fall back to the
/// content test.
/// </para>
/// <para>
/// This is weaker than <see cref="FindBlob"/> on purpose, and exactly as weak as the diff: a file whose
/// length and mtime both match its previous version is called unchanged there without a byte being read
/// (BackupDiffer's Unchanged branch). So a file that slips past this check would also have slipped past
/// the diff and never entered the pipeline. What the strictness bought — catching a file rewritten
/// between the interruption and the resume — it still buys on the fallback path, and a rewrite that
/// preserves both length and mtime defeats the diff identically.
/// </para>
/// </summary>
public JournalRecord? FindUntouchedBlob(string path, DateTimeOffset mtimeUtc, long length)
```

In `ProbeAndResumeAsync`, ask it **before** `ProbeForDedupAsync` — before any read — using the `FileInfo` the path already gives. A hit returns the same `BlobPlacement` shape the journal-hit branch returns today, including `Resumed: true`; a miss falls through to the existing route unchanged.

- [ ] **Step 4: Run the tests, then the full suite. Commit.**

```
feat: let a resume trust metadata instead of re-reading

Answering "did the previous run already upload this?" cost a full read,
because the journal's reuse test needs a content identity. Measured on a
real resume: 194 GB read to establish that 704 MB needed sending.

The new test is path + mtime + length, which is what the diff already
trusts to call a file unchanged — so a file that slips past it would have
slipped past the diff and never reached the pipeline. Anything that does
not match falls back to the content test, unchanged.
```

---

## Part C — a raw upload that reads from where the file already is

### Task 3: Upload the source, and prove it did not move

**Files:** `BackupOrchestrator.cs` (`StreamAndStageAsync` / the raw branch, and `UploadStagedBlobItemAsync`), `backend/tests/AzureStorageBackup.Api.Tests/` — pick the file where raw-route behaviour already lives; check `BlobUploaderTests.cs` and the orchestrator integration tests before adding a new one.

**Read `docs/raw-upload-without-staging-design.md` before starting.** The design's §2 is the part to get right.

- [ ] **Step 1: Write the failing tests**

- A store-only unencrypted backup never lets the staged pool rise above zero. Assert on the **peak** sampled during the run, not the end state — the end state is zero either way, so an end-state assertion would pass without the change.
- A raw blob round-trips byte-identically.
- A file rewritten mid-upload does not leave a `data/{hash}` whose content hashes differently. Force the window with a blocking uploader: begin the upload, rewrite the source, release.
- An encrypted or compressed blob still takes the copying route.

- [ ] **Step 2: Run them, confirm the first fails**

- [ ] **Step 3: Implement**

The raw branch stops copying: it reads once to compute the identity, records `(length, mtime)`, and the upload names the **source path**. After the upload returns, stat again; if either moved, delete the object just written — it is named for a hash its content may no longer match — and retry the item through the copying route, which is immune because it uploads a snapshot.

Watch two things the design calls out: the item now travels with `Handoff` null (the same shape a probe hit already uses, so no new plumbing), and nothing is charged to the staging quota on this route.

- [ ] **Step 4: Run the tests, then the full suite. Commit.**

```
fix: upload a raw blob from where it already is

A store-only unencrypted file that fits one volume was copied into staging
in full and uploaded from the copy — two reads and a write, for content
byte-identical to the source still sitting there, and that much of the
process-wide staging quota held for the duration.

The copy was what fixed the content between hashing and uploading. That is
now done by stat'ing on both sides of the upload, the same test the diff
trusts to call a file unchanged, over a window of seconds rather than
minutes. If it moved, the object is deleted and the item retries through
the copying route, which is immune.
```

---

## Part E — an error badge with a tense

### Task 4: Say when the error was

**Files:** `frontend/src/lib/errorBadge.ts` (new), `frontend/src/lib/errorBadge.test.ts` (new), `frontend/src/pages/BackupConfigsPage.tsx`

The backend already stores `LastErrorAt` beside `LastError` (`BackupConfigService.SetErrorAsync`), and the status badge already renders a persistent `Error` with a tooltip and a Reset button. What it does not say is **when** — so an error from three days ago, survived by several successful-looking pauses and suspends, looks exactly like one from a minute ago. The badge is only cleared by a successful run or a manual Reset (`BackupConfigStatusExtensions`), so a stale one can stand for a long time.

- [ ] **Step 1: Write the failing tests**

The repository does not unit-test components; logic worth testing goes to `frontend/src/lib/` as a pure function. Read `interruptedNotice.ts` and its test first — including their comment density, which explains *why* a rule exists.

Cases: no error → nothing; an error minutes old; an error days old; an error with no timestamp at all (an older backend, or a row written before the column existed) → still renders, just without the tense, because dropping the badge would be worse than dropping the time.

- [ ] **Step 2: Run them, confirm they fail**

- [ ] **Step 3: Implement and wire**

Check `frontend/src/constants/format.ts` for an existing relative-time or duration formatter before writing one — this repository already formats durations in several places and a second convention would be worse than a slightly awkward reuse.

- [ ] **Step 4: `npx tsc -b`, `npx vitest run`, commit.**

```
fix: give the error badge a tense

The badge is cleared only by a successful run or a manual Reset, so an
error from days ago outlives every pause, suspend and resume in between and
reads exactly like one from a minute ago. It now says when.

An error with no timestamp still renders — an older row, or an older
backend; losing the badge would be worse than losing the time.
```

---

## Self-review notes

**Spec coverage.** B's spec §1 → Task 1, §2 → Task 2, §3's equivalence argument → the doc comment on `FindUntouchedBlob` and Task 2's tests 2-4. C's spec §1 → Task 3's implementation, §2 → its third test, §3 → its fourth test. E has no spec; it is a rendering change over stored data, and the task carries its own reasoning.

**The three parts are independent** and can be reviewed and reverted separately. They share a branch only because they ship in one release.

**Where this plan is most likely to be wrong.** Task 3 is the one to distrust: it says "delete the object just written" without naming the API that does it, because the deletion path depends on how the uploader and the blob client are wired for this route, and guessing at it in a plan is how the earlier branch's sketches went wrong twice. Read it, then write it.
