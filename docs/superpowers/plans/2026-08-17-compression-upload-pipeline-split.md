# Compression/upload pipeline split — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the compression stage independent of the upload stage, so that `StagedLimitBytes` — not the size of the worker pool — is what limits how far compression runs ahead.

**Architecture:** The single consumer loop in `BackupOrchestrator.RunAsync` becomes three, joined by two unbounded channels: one prober (the dedup probe is disk-bound, and concurrent reads do not make a disk faster — the stage exists so its reads overlap compression, not so they run in parallel), one compressor (compression is already globally serial behind `StagingArea._compressLock`), and `UploadConcurrency + 1` uploaders. Each stage stops at a well-defined handover and packages the rest of the item's work as a closure for the next one. Queue depth is bounded in bytes by the staging quota, which is the bound the operator configured.

**Tech Stack:** C# / .NET, xUnit, `System.Threading.Channels`. Integration tests run against Azurite.

## Global Constraints

- **Everything written into the repository is English** — code, comments, commit messages, docs. (Conversation with the user stays Chinese.)
- **Commit messages end with:** `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`
- **Integration tests need Azurite**: `npx azurite --skipApiVersionCheck` — without it 189 tests skip silently and a green run means nothing.
- **Do not change** `VolumeUploadGate`, `VolumeUploadScope`, `StagingArea`'s quota semantics, or any index/journal format.
- **The progress identity must keep holding:** `processed + preparing + queued + waitingOnArchive + uploading ≡ total` (`StageProgress.cs:916-924`).
- **`BeginWork`/`EndWork` must stay exactly paired.** `BeginWork` is called once when the **prober** claims an item — the first stage, so an item counts as in-hand for its whole journey across both queues. `EndWork` is called once when that item leaves the pipeline: by the uploader after it settles, or by whichever earlier stage the item stops at (settled as unreadable, or discarded from a drained queue). An unpaired call permanently skews the `uploading` column.
- Spec: `docs/compression-upload-pipeline-design.md`.

## File Structure

| File | Responsibility |
|---|---|
| `backend/src/AzureStorageBackup.Api/Services/StagedHandoff.cs` | **New.** Owns what a queued item must hand back exactly once: the staged archive's pool quota and, for single files, the dedup reservation. |
| `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs` | Split the consumer loop; split `PlaceBlobAsync` and `ProcessPackAsync` each into a compress half and an upload half. |
| `backend/tests/AzureStorageBackup.Api.Tests/StagedHandoffTests.cs` | **New.** Unit tests for the ownership guard. |
| `backend/tests/AzureStorageBackup.Api.Tests/CompressionContinuityTests.cs` | **New.** Integration acceptance: compression keeps running while every uploader is blocked; the staging limit binds; stop and downgrade release the queue. |

---

### Task 1: `StagedHandoff` ownership guard

A queue entry owns two things that must be handed back exactly once, or the loss is permanent: the `StagedItem` (pool quota booked on a process-wide singleton, plus volume files on disk) and — single files only — the dedup reservation that later same-content arrivals are blocked on. `StagingArea.cs:270-286` explains why a leak here is unrecoverable without a process restart.

The distinction between "settled" and "discarded" matters: on success the reservation was already answered by `Resolution.Complete`, and calling `Fail` afterwards would still run `release()` (`LocalDedupResolver.cs:302-306`) and withdraw the claim, making the next same-content file upload the same bytes again.

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/StagedHandoff.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/StagedHandoffTests.cs`

**Interfaces:**
- Consumes: `StagingArea`, `StagedItem` (`StagingArea.cs:4`), `StagingArea.StagingLease`.
- Produces:
  - `sealed class StagedHandoff : IDisposable`
  - `StagedHandoff(StagingArea area, StagedItem? staged, Action<Exception>? abandon = null)`
  - `StagedItem? Staged { get; }`
  - `void MarkSettled()`
  - `void Dispose()`

- [ ] **Step 1: Write the failing tests**

Create `backend/tests/AzureStorageBackup.Api.Tests/StagedHandoffTests.cs`:

```csharp
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The ownership guard for one entry of the staged queue. Everything here is about the release happening
/// **exactly once**: the pool quota lives on a process-wide singleton, so leaking it keeps that space booked
/// until the process restarts, and since the quota gates output for every run, enough leaks stall compression
/// process-wide (see StagingArea's remarks on Hold).
/// </summary>
public sealed class StagedHandoffTests : IDisposable
{
    private readonly string _root;
    private readonly StagingArea _area;

    public StagedHandoffTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "asb-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _area = new StagingArea(
            Path.Combine(_root, "compress"), Path.Combine(_root, "staged"), () => 1_000_000);
    }

    public void Dispose()
    {
        _area.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private Task<StagedItem> Stage(string name, int size) => _area.StageAsync(async (dir, ct) =>
    {
        var path = Path.Combine(dir, name);
        await File.WriteAllBytesAsync(path, new byte[size], ct);
        return (IReadOnlyList<string>)new[] { path };
    });

    [Fact]
    public async Task Dispose_Hands_The_Pool_Quota_Back()
    {
        var staged = await Stage("a", 500);
        Assert.Equal(500, _area.StagedBytes);

        using (new StagedHandoff(_area, staged)) { }

        Assert.Equal(0, _area.StagedBytes);
    }

    /// <summary>Double release drives the watermark negative, and after that backpressure never blocks compression again.</summary>
    [Fact]
    public async Task Dispose_Is_Idempotent()
    {
        var staged = await Stage("b", 500);
        var handoff = new StagedHandoff(_area, staged);

        handoff.Dispose();
        handoff.Dispose();

        Assert.Equal(0, _area.StagedBytes);
    }

    /// <summary>Discarded before it reached the cloud: the latecomers blocked on this content must be woken, or they hang for the rest of the run.</summary>
    [Fact]
    public async Task Dispose_Fails_The_Reservation_When_It_Was_Never_Settled()
    {
        var staged = await Stage("c", 100);
        Exception? abandoned = null;

        using (new StagedHandoff(_area, staged, ex => abandoned = ex)) { }

        Assert.NotNull(abandoned);
    }

    /// <summary>
    /// After a successful upload the reservation was already answered by Resolution.Complete. Failing it now would
    /// still run the reservation's release(), withdrawing the claim, and the next file with the same content would
    /// upload the very same bytes a second time.
    /// </summary>
    [Fact]
    public async Task Dispose_Leaves_A_Settled_Reservation_Alone()
    {
        var staged = await Stage("d", 100);
        var failed = false;

        var handoff = new StagedHandoff(_area, staged, _ => failed = true);
        handoff.MarkSettled();
        handoff.Dispose();

        Assert.False(failed);
        Assert.Equal(0, _area.StagedBytes);
    }

    /// <summary>7z can drop every member of a group, leaving no archive at all — the guard still has to be constructible.</summary>
    [Fact]
    public void A_Null_Archive_Releases_Nothing_And_Does_Not_Throw()
    {
        using var handoff = new StagedHandoff(_area, staged: null);
        Assert.Null(handoff.Staged);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj \
  --filter "FullyQualifiedName~StagedHandoffTests"
```

Expected: compile error — `StagedHandoff` does not exist.

- [ ] **Step 3: Write the implementation**

Create `backend/src/AzureStorageBackup.Api/Services/StagedHandoff.cs`:

```csharp
namespace AzureStorageBackup.Api.Services;

/// <summary>
/// One entry's ownership of everything that must be handed back exactly once when a compressed archive travels
/// from the compressor to an uploader.
/// <para>
/// Two things ride along. The <see cref="StagedItem"/> holds pool quota — an in-memory counter on a singleton
/// shared by every run — plus volume files on disk; leaking it books that space until the process restarts, and
/// since the quota gates output for all runs, enough leaks stall compression process-wide (see
/// <see cref="StagingArea.Hold"/>). The optional abandon callback is the dedup reservation of a single-file item:
/// latecomers in this run with identical content are blocked on it, and an entry discarded without answering them
/// leaves them waiting for the rest of the run.
/// </para>
/// <para>
/// <see cref="MarkSettled"/> is the difference between "the upload answered the waiters" and "this archive died on
/// the way". It is not cosmetic: <c>Resolution.Fail</c> also withdraws the claim from the reservation table, so
/// calling it after a successful upload would make the next file with the same content upload those bytes again.
/// </para>
/// </summary>
public sealed class StagedHandoff(StagingArea area, StagedItem? staged, Action<Exception>? abandon = null)
    : IDisposable
{
    private int _settled;
    private int _disposed;

    /// <summary>The archive on disk. Null when 7z dropped every member of the group and left no archive at all.</summary>
    public StagedItem? Staged => staged;

    /// <summary>The upload finished (or deduplicated onto an existing blob): the waiters have their answer already.</summary>
    public void MarkSettled() => Interlocked.Exchange(ref _settled, 1);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (staged is not null)
            area.Release(staged);
        if (Volatile.Read(ref _settled) == 0)
            abandon?.Invoke(new OperationCanceledException(
                "Staged work was discarded before it reached the cloud."));
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj \
  --filter "FullyQualifiedName~StagedHandoffTests"
```

Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/StagedHandoff.cs \
        backend/tests/AzureStorageBackup.Api.Tests/StagedHandoffTests.cs
git commit -m "$(cat <<'EOF'
feat: add the staged-queue ownership guard

A queue entry between the compressor and an uploader owns pool quota and,
for single files, a dedup reservation. Both have to be handed back exactly
once: the quota lives on a process-wide singleton, so a leak books that
space until restart and stalls compression for every run.

MarkSettled separates "the upload answered the waiters" from "this archive
died on the way" — failing an already-completed reservation would withdraw
its claim and make the next same-content file re-upload the same bytes.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Split the single-file path into a compress half and an upload half

Behaviour must not change in this task. The two halves are still called back to back by the same worker; only the seam is introduced. Every existing test stays green — that is the acceptance criterion.

The cut goes **after** `StreamAndStageAsync` and **before** `ResolveAsync`. `ResolveAsync` (`BackupOrchestrator.cs:1559`) waits on `Reservation.Completion` when another item in this batch is uploading the same content, which is a wait on someone else's upload — running it on the compressor would reintroduce exactly the stall this rework removes.

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs:1527-1586` (`PlaceBlobAsync`)

**Interfaces:**
- Consumes: `StagedHandoff` (Task 1).
- Produces (these are the signatures as built — Task 4 wires the second and third of them into two different loops):
  - `private async Task<BlobPlacement?> ProbeAndResumeAsync(BackupRequest request, PlannedFile file, string localPath, LocalDedupResolver localResolver, StageTracker uploadTracker, BackupRunControl? control, CancellationToken ct)` — null means "nothing matched, go compress"; non-null means a dedup or resume hit settled the item without compressing anything.
  - `private sealed record StagedBlob(BlobContent Content, StagedHandoff Handoff)`
  - `private async Task<StagedBlob> StageBlobAsync(BackupRequest request, PlannedFile file, string localPath, bool storeOnly, StageTracker uploadTracker, RunState state, CancellationToken ct)` — note what it does **not** take: no `LocalDedupResolver`, no `VolumeUploadScope`, no `BackupRunControl`. That is the seam. It never returns null: by the time it is called the probe has already run.
  - `private async Task<BlobPlacement> UploadStagedBlobItemAsync(BackupRequest request, PlannedFile file, StagedBlob stagedBlob, BlobAddressScheme addressing, LocalDedupResolver localResolver, VolumeUploadScope uploadScope, StageTracker uploadTracker, RunState state, BackupRunControl? control, CancellationToken ct)`

- [ ] **Step 1: Extract the two halves**

Replace the body of `PlaceBlobAsync` (`BackupOrchestrator.cs:1527-1586`) with a call to the two new methods, so it keeps its current signature and behaviour:

```csharp
private async Task<BlobPlacement> PlaceBlobAsync(
    BackupRequest request, PlannedFile file, string localPath, bool storeOnly,
    BlobAddressScheme addressing, LocalDedupResolver localResolver,
    VolumeUploadScope uploadScope, StageTracker uploadTracker, RunState state, BackupRunControl? control,
    CancellationToken ct)
{
    // Kept as the single-worker composition of the two halves: the pipelined path calls them separately, and
    // the retry inside a pack demotion (see ProcessPackAsync) still wants "do the whole thing here and now".
    var hit = await ProbeAndResumeAsync(request, file, localPath, localResolver, uploadTracker, control, ct);
    if (hit is not null)
        return hit;

    var stagedBlob = await StageBlobAsync(
        request, file, localPath, storeOnly, uploadTracker, state, ct);
    return await UploadStagedBlobItemAsync(
        request, file, stagedBlob, addressing, localResolver, uploadScope, uploadTracker, state, control, ct);
}
```

Move the code verbatim:

- `BackupOrchestrator.cs:1535-1549` (the probe plus the two dedup tiers) becomes `ProbeAndResumeAsync`, returning `BlobPlacement?` — null meaning "nothing matched, go compress".
- `BackupOrchestrator.cs:1551-1553` (`StreamAndStageAsync`) becomes the body of `StageBlobAsync`, which wraps the returned `StagedItem` in a `StagedHandoff` and returns `new StagedBlob(content, handoff)`. It takes **no** `localResolver` and **no** `uploadScope` — that is the point of the cut.
- `BackupOrchestrator.cs:1556-1585` (resolve, upload, `Complete`/`Fail`, the `finally` release) becomes `UploadStagedBlobItemAsync`.

In `UploadStagedBlobItemAsync`, the reservation and the pool release both go through the handoff instead of the old `try`/`finally`:

```csharp
try
{
    var res = await localResolver.ResolveAsync(
        content.FullHash, content.Length, content.HeadHash, content.TailHash, uploadTracker);
    if (res.Exists)
    {
        var existing = res.Existing!;
        // Compressed for nothing, but the pool quota must go back immediately all the same.
        stagedBlob.Handoff.MarkSettled();
        return new BlobPlacement(res.Ref, res.Collision, existing.Volumes, existing.VolumeSizes,
            content with { Raw = existing.Raw });
    }
    try
    {
        var (volumes, sizes) = await UploadStagedBlobAsync(
            request, res.Ref, stagedBlob.Handoff.Staged!, content, addressing, uploadScope, uploadTracker,
            state, file.Path, control, ct);
        res.Complete(content.Raw, volumes, sizes);
        stagedBlob.Handoff.MarkSettled();
        return new BlobPlacement(res.Ref, res.Collision, volumes, sizes, content);
    }
    catch (Exception ex)
    {
        res.Fail(ex);                       // never dedup onto a blob that was not uploaded successfully
        stagedBlob.Handoff.MarkSettled();   // the waiters have been answered; Dispose must not answer them twice
        throw;
    }
}
finally
{
    stagedBlob.Handoff.Dispose();
}
```

- [ ] **Step 2: Run the full backend suite**

```bash
npx azurite --skipApiVersionCheck &   # if not already running
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: PASS, same count as before the change. This task adds no test — it is a pure refactor, and the existing suite is the assertion.

- [ ] **Step 3: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs
git commit -m "$(cat <<'EOF'
refactor: split the single-file path at the staging seam

PlaceBlobAsync now composes three pieces — probe, compress, upload — with
the cut placed after StreamAndStageAsync and before ResolveAsync. That
boundary is not arbitrary: ResolveAsync waits on another item's upload when
two files in one batch share content, so leaving it on the compression side
would stall the compressor on someone else's network.

No behaviour change; the halves are still called back to back.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Split the pack path into a compress half and an upload half

Same rule: no behaviour change, existing tests are the acceptance criterion.

`AttemptAsync` (`BackupOrchestrator.cs:1978-2084`) is the retry unit — "compress this group + upload it" — and it must stay re-entrant: the pack id is taken outside it and never changes, so a recompression overwrites the same volume family (`BackupOrchestrator.cs:1936-1940`). The split keeps that property by making the retry recompress rather than reuse.

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs:1903-2180` (`ProcessPackAsync`)

**Interfaces:**
- Consumes: `StagedHandoff` (Task 1).
- Produces:
  - `private sealed record GroupAttempt(StagedHandoff Handoff, List<PackEntry> Changed, IReadOnlyList<PackEntry> Stable)`
  - `private async Task<GroupAttempt> CompressGroupAsync(BackupRequest request, string packId, IReadOnlyList<PackEntry> members, bool storeOnly, StageTracker uploadTracker, RunState state, CancellationToken ct)`
  - `private async Task<IReadOnlyList<long>> UploadGroupAsync(BackupRequest request, string packId, GroupAttempt attempt, VolumeUploadScope uploadScope, StageTracker uploadTracker, RunState state, BackupRunControl? control, CancellationToken ct)`

- [ ] **Step 1: Extract `CompressGroupAsync`**

Everything from the pre-compression stat snapshot through the post-compression re-verification moves in verbatim: `BackupOrchestrator.cs:1989-1998` (stat snapshot), `1999-2000` (`CompressPackTolerantAsync`), `2010-2015` (members 7z dropped), `2017-2060` (re-verification). Then, where the current code branches:

```csharp
if (changed.Count == 0)
    return new GroupAttempt(new StagedHandoff(staging, staged), changed, members);

// Discard this archive; the stable members still become a pack, the changed ones are handled under their
// new hash. staged can only be null when 7z dropped every member, in which case there is nothing to release.
if (staged is not null)
    staging.Release(staged);
var stable = members.Where(m => !changed.Contains(m)).ToList();
if (stable.Count == 0)
    return new GroupAttempt(new StagedHandoff(staging, staged: null), changed, []);

var staged2 = await CompressPackAsync(request, packId, stable, storeOnly, uploadTracker, state, ct);
return new GroupAttempt(new StagedHandoff(staging, staged2), changed, stable);
```

The `using var held = staging.Hold(staged)` at `BackupOrchestrator.cs:2008` is **removed**: the `StagedHandoff` now carries that responsibility, and the scope it guarded no longer ends inside this method. Between the compression and the return there is still code that can throw (the rehash inside the re-verification, and cancellation), so wrap the body from `CompressPackTolerantAsync` onward in a `try`/`catch` that releases `staged` and rethrows.

- [ ] **Step 2: Extract `UploadGroupAsync`**

```csharp
private async Task<IReadOnlyList<long>> UploadGroupAsync(
    BackupRequest request, string packId, GroupAttempt attempt, VolumeUploadScope uploadScope,
    StageTracker uploadTracker, RunState state, BackupRunControl? control, CancellationToken ct)
{
    if (attempt.Stable.Count == 0)
        return [];
    return await UploadStagedPackAsync(
        request, packId, attempt.Handoff.Staged!, uploadScope, uploadTracker, state,
        attempt.Stable.Count, control, ct);
}
```

- [ ] **Step 3: Rebuild `AttemptAsync` on top of the two halves**

```csharp
// The first pass consumes what the compressor already produced; every retry compresses a fresh archive, which
// is what the retry unit has always done — the pack id does not change, so the new output overwrites the same
// volume family and UploadStagedPackAsync clears the previous attempt's leftovers before sending.
GroupAttempt? attempt = precompressed;
async Task<(List<PackEntry>, IReadOnlyList<PackEntry>, IReadOnlyList<long>)> AttemptAsync()
{
    attempt ??= await CompressGroupAsync(request, packId, members, storeOnly, uploadTracker, state, ct);
    try
    {
        var vols = await UploadGroupAsync(
            request, packId, attempt, uploadScope, uploadTracker, state, control, ct);
        attempt.Handoff.MarkSettled();
        return (attempt.Changed, attempt.Stable, vols);
    }
    catch
    {
        // This archive is dead. Hand its quota back now rather than at the end of the run, and make the next
        // attempt compress a fresh one — reusing it would upload an archive whose members were never re-verified.
        attempt.Handoff.Dispose();
        attempt = null;
        throw;
    }
    finally
    {
        attempt?.Handoff.Dispose();
    }
}
```

For this task `precompressed` is always null (the caller has not been split yet); Task 4 passes a real one.

- [ ] **Step 4: Run the full backend suite**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: PASS, same count as before. `BackupPackRetryUnitTests` is the one that matters most here — it pins the "one hiccup redoes this group, not the earlier ones" behaviour.

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs
git commit -m "$(cat <<'EOF'
refactor: split the pack path at the staging seam

CompressGroupAsync now ends where the archive lands in the pool, and
UploadGroupAsync takes it from there. AttemptAsync composes them and keeps
the retry unit intact: a failed upload disposes the archive and the next
attempt compresses a fresh one under the same pack id, which is what the
unit has always done.

The explicit staging.Hold goes away — StagedHandoff carries that
responsibility now, and the scope it guarded no longer ends in this method.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Three stages, two queues

This is the task that changes behaviour. Write the acceptance test first — it fails on today's code for the reason the spec documents.

The stage count changed after the plan was first written, so read `docs/compression-upload-pipeline-design.md` §1 before starting. In short: the dedup probe gets its own stage, with **one** worker. It is disk-bound (a candidate hit reads the whole file to derive its content identity), and concurrency does not make a disk faster — on spinning media or a NAS share it makes it slower by turning one sequential read into several competing seeks. The reason to give it a stage of its own is overlap, not parallelism: while it reads the next item, the compressor works on the previous one, so the CPU stops waiting on a hash and the disk stops waiting on 7z.

**Files:**
- Create: `backend/tests/AzureStorageBackup.Api.Tests/CompressionContinuityTests.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs:721-759` (the consumer loop and its startup), `:1431-1522` (`HandleBlobAsync`, split in Step 3), `:1004` / `:1013` / `:1052-1055` (settle points)

**Interfaces:**
- Consumes, as actually built by Tasks 1-3:
  - `StagedHandoff(StagingArea area, StagedItem? staged, Action<Exception>? abandon = null)` with `Staged`, `MarkSettled()`, `Dispose()` (Task 1).
  - Single-file path (Task 2): `ProbeAndResumeAsync(request, file, localPath, localResolver, uploadTracker, control, ct)` → `BlobPlacement?`; `StageBlobAsync(request, file, localPath, storeOnly, uploadTracker, state, ct)` → `StagedBlob(BlobContent Content, StagedHandoff Handoff)`; `UploadStagedBlobItemAsync(request, file, stagedBlob, addressing, localResolver, uploadScope, uploadTracker, state, control, ct)` → `BlobPlacement`.
  - Pack path (Task 3): `CompressGroupAsync(request, packId, members, storeOnly, uploadTracker, state, ct)` → `GroupAttempt(StagedHandoff Handoff, List<PackEntry> Changed, IReadOnlyList<PackEntry> Stable)`, and `RunGroupAsync(request, packId, members, storeOnly, GroupAttempt? precompressed, uploadScope, uploadTracker, state, control, ct)` → `(Changed, Recorded, Volumes)`.
  - **`RunGroupAsync` is the pack side's wiring point, and `precompressed` is why it exists.** The compressor calls `CompressGroupAsync` for one group and puts the `GroupAttempt` in the queue; the uploader calls `RunGroupAsync` with that attempt as `precompressed`. The first pass consumes it, and any retry inside `RunGroupAsync` compresses afresh — which is what keeps the retry unit honest. Note the granularity: it is per **group**, not per pool. A pool splits into several groups and each has its own pack id, so the compressor produces one queue entry per group.
- Produces:
  - `private sealed record ProbedItem(WorkItem Item, BlobPlacement? Hit)` — `Hit` non-null means the probe already settled where this content lives, so nothing needs compressing.
  - `private sealed record PendingUpload(StagedHandoff? Handoff, Func<CancellationToken, Task> RunAsync)` — `Handoff` is null for a probe hit, which owns no archive.
  - `private async Task FinishBlobAsync(BackupRequest request, PlannedFile file, BlobPlacement placement, ConcurrentDictionary<string, StorageRef> storageByPath, ConcurrentDictionary<string, string> tailByPath, ConcurrentDictionary<string, EntryOverride> overrides, Action<long> onItem, BackupRunControl? control, CancellationToken ct)` — the single-file item's settle half, extracted in Step 3.
  - `private async Task<bool> TrySettleUnreadableAsync(BackupRequest request, PlannedFile file, string localPath, Exception ex, ConcurrentDictionary<string, string> postDiffUnreadable, Action<long> onItem, CancellationToken ct)` — returns true when it recognised and settled the exception as "this file cannot be read", false when the caller must rethrow.
  - Two `Channel<>`s local to `RunAsync`: `probedQueue` (written by the prober, read by the compressor) and `stagedQueue` (written by the compressor, read by the uploaders).

- [ ] **Step 1: Write the failing acceptance test**

Create `backend/tests/AzureStorageBackup.Api.Tests/CompressionContinuityTests.cs`. It follows the shape of `PipelinedBackupTests` (Azurite + an injected `IBlobUploader`); the helpers are copied rather than shared, as every test file in this suite carries its own.

```csharp
using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The compression stage must not be throttled by the upload stage.
/// <para>
/// Before this rework one worker owned an item from compression through the last volume of its upload, and there
/// were only UploadConcurrency + 1 workers. Once that many items were uploading, no worker could reach StageAsync
/// and compression stopped outright — measured in production with 23 items queued, 4.5 GB in the pool, and both
/// preparing and waitingOnArchive at zero. The staging limit was never the binding constraint, so the setting had
/// no effect at any value.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class CompressionContinuityTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private const int FileSize = 2 * 1024 * 1024;

    private readonly string _base;
    private readonly string _root;
    private readonly string _temp;

    public CompressionContinuityTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "asb-cont-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "src");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount() => new()
    {
        Name = "azurite",
        BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
        AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
        Region = AzureRegion.Global,
    };

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;
    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    private void WriteFile(string rel, int size)
    {
        var full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(full, bytes);
    }

    /// <summary>
    /// Every upload hangs on <paramref name="gate"/> before being let through to the real uploader, so the run
    /// still completes normally once the gate opens. Only the 8-argument overload needs implementing: the
    /// progress-reporting one has a default implementation that forwards to it (see IBlobUploader).
    /// </summary>
    private sealed class BlockingUploader(Task gate, IBlobUploader inner) : IBlobUploader
    {
        public async Task<bool> UploadIfMissingAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            await gate.WaitAsync(ct);
            return await inner.UploadIfMissingAsync(
                account, container, blobName, filePath, tier, retry, ct, metadata);
        }

        public async Task UploadOverwriteAsync(
            Account account, string container, string blobName, string filePath,
            AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            await gate.WaitAsync(ct);
            await inner.UploadOverwriteAsync(
                account, container, blobName, filePath, tier, retry, ct, metadata);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan patience)
    {
        var deadline = DateTime.UtcNow + patience;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Condition was not met in time.");
    }

    private (BackupOrchestrator Orchestrator, StagingArea Staging, BackupRequest Request) Build(
        IBlobUploader? uploader, long stagingLimit, int uploadConcurrency)
    {
        var factory = new BlobClientFactory();
        var store = new BackupInfoStore(factory);
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => stagingLimit);
        var authority = new TestLocalAuthority(store);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            new SevenZipCompressor(), uploader ?? new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(),
                indexCache: authority.IndexCache, trackedInfo: authority.Tracked),
            new FileHasher(), authority.IndexCache, authority.Tracked);
        var request = new BackupRequest
        {
            Account = AzuriteAccount(),
            Container = RandomName("cont"),
            LocalRoot = _root,
            Name = "continuity",
            // Single-file blobs only: one item per file, so "how many items are in flight" is exactly the number
            // of files, with no packing to reason about.
            Options = new BackupEngineOptions
            {
                UploadConcurrency = uploadConcurrency,
                Plan = new PlanOptions { SingleFileThresholdBytes = 1 },
            },
        };
        return (orchestrator, staging, request);
    }

    [Fact]
    public async Task Compression_Keeps_Running_While_Every_Uploader_Is_Blocked()
    {
        if (!AzuriteReachable() || !SevenZip())
            return;

        // Twelve items against three workers (concurrency 2 + 1): on the old code the pool plateaus at what
        // three in-flight items hold, because no worker is left to reach StageAsync.
        for (var i = 0; i < 12; i++)
            WriteFile($"f{i}.bin", FileSize);

        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new BlobClientFactory();
        var (orchestrator, staging, request) = Build(
            new BlockingUploader(block.Task, new BlobUploader(factory)),
            stagingLimit: 200_000_000, uploadConcurrency: 2);

        var run = orchestrator.RunAsync(request);
        try
        {
            // Every uploader is stuck on the gate. Compression must keep going regardless — more than the three
            // items' worth the old worker pool allowed. The files are random bytes, so each archive is about
            // FileSize; four of them is comfortably past the old ceiling and well under the staging limit.
            await WaitUntil(() => staging.StagedBytes > 4L * FileSize, TimeSpan.FromSeconds(60));
        }
        finally
        {
            block.SetResult();
        }

        await run;
    }
}
```

Verify `BackupEngineOptions`' property names (`UploadConcurrency`, `Plan`) and `BackupInfoStore`'s constructor against the current code while writing this — `PipelinedBackupTests.Build` is the reference for the orchestrator's constructor argument order.

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj \
  --filter "FullyQualifiedName~CompressionContinuityTests.Compression_Keeps_Running"
```

Expected: FAIL — the pool plateaus at three items' worth and `WaitUntil` times out. If it passes, the test is not reproducing the condition; check that `UploadConcurrency` really is 2 and that the staging limit is not the thing stopping it.

- [ ] **Step 3: Extract the single-file item's settle half (no behaviour change)**

`HandleBlobAsync` (`BackupOrchestrator.cs:1431-1522`) wraps probe + compress + upload in one `try`, and everything after that `try` is the item's **settle**: the journal append, the collision warning, the index override, `storageByPath` / `tailByPath`, `LogFileAsync`, `onItem`. Once the three stages exist, two different stages need to reach that settle (a probe hit settles without ever compressing), and all three need the unreadable-source handling. Extract both, leaving `HandleBlobAsync` behaving exactly as it does now.

The settle half, moved verbatim from `BackupOrchestrator.cs:1472-1521`:

```csharp
/// <summary>
/// Everything that happens once this file's content has a home in the cloud, whether it got there by being
/// uploaded or by being recognised as already present. Split out because those two answers now arrive in
/// different stages of the pipeline, and both have to record the same things.
/// </summary>
private async Task FinishBlobAsync(
    BackupRequest request, PlannedFile file, BlobPlacement placement,
    ConcurrentDictionary<string, StorageRef> storageByPath, ConcurrentDictionary<string, string> tailByPath,
    ConcurrentDictionary<string, EntryOverride> overrides, Action<long> onItem,
    BackupRunControl? control, CancellationToken ct)
{
    // ... the body from BackupOrchestrator.cs:1478-1521, comments included, unchanged ...
}
```

The unreadable-source handling, from the `catch` filter at `BackupOrchestrator.cs:1459-1470`. It becomes a predicate the callers invoke from inside a `catch` block rather than an exception filter, because **an exception filter cannot `await`** and `SourceUnreadable` plus `MarkPostDiffUnreadableAsync` both need to:

```csharp
/// <summary>
/// Recognise "this run failed to store this file" and settle it as such: no blob is produced, the index
/// carries the old entry forward or omits it, one warning is logged, and the run continues.
/// <para>
/// The probe is not enough on its own — BlobUploader classifies IOException as a retryable network error
/// and rethrows it verbatim once the retry budget runs out, so accepting on type alone would turn one NAS
/// outage into a pile of "file unreadable" while the run still reports success. Hence the second look at the
/// source file. ArchiveMembersMissingException needs no such look: it is only thrown when 7z did not put
/// this file in the archive intact, which is already proof, and it is thrown before the upload.
/// </para>
/// </summary>
/// <returns>true when this exception was recognised and the item settled; false when the caller must rethrow.</returns>
private async Task<bool> TrySettleUnreadableAsync(
    BackupRequest request, PlannedFile file, string localPath, Exception ex,
    ConcurrentDictionary<string, string> postDiffUnreadable, Action<long> onItem, CancellationToken ct)
{
    if (ex is not ArchiveMembersMissingException
        && !((ex is IOException or UnauthorizedAccessException) && SourceUnreadable(localPath)))
        return false;
    await MarkPostDiffUnreadableAsync(request, file.Path, ex.Message, postDiffUnreadable, ct);
    onItem(file.Length);
    return true;
}
```

`HandleBlobAsync` then becomes probe-or-place, catch-and-settle, finish — same behaviour, three named pieces. Keep it: `ProcessPackAsync` still calls it when a member grows past the threshold and is demoted to a single file (`BackupOrchestrator.cs:2169-2172` before this task's edits).

Run the full suite (`1231 passed, 0 failed, 0 skipped`) and commit this extraction on its own:

```bash
git commit -m "$(cat <<'EOF'
refactor: extract the single-file settle and unreadable paths

Once probe, compression and upload are three stages, two of them need to
reach the settle half — a probe hit records the same journal entry, index
override and storageByPath row as a finished upload — and all three need
the unreadable-source handling.

The unreadable check becomes a predicate called from a catch block rather
than an exception filter, because a filter cannot await and both halves of
the check do.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 4: Introduce the three stages**

In `RunAsync`, replace the single `ConsumeAsync` (`BackupOrchestrator.cs:721-744`) with three loops joined by two channels:

```csharp
// Stage 1 → 2. The probe is disk-bound: on a candidate hit it reads the whole file end to end to derive
// its content identity. One worker, deliberately — concurrency does not make a disk faster, and on
// spinning media or a NAS share it makes it slower by turning one sequential read into several competing
// seeks. What this stage buys is overlap: while it reads the next item, the compressor works on the
// previous one, so the CPU stops waiting on a hash and the disk stops waiting on 7z.
var probedQueue = Channel.CreateUnbounded<ProbedItem>(
    new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });
// Stage 2 → 3. Compression is globally serial (StagingArea holds one compression lock across every run),
// so this side is one worker by definition — a pool would be a queue in front of a lock that admits one.
// Its only blocking point is the staging quota, which is what finally makes StagedLimitBytes bind.
var stagedQueue = Channel.CreateUnbounded<PendingUpload>(
    new UnboundedChannelOptions { SingleWriter = true, SingleReader = false });

async Task ProbeAsync()
{
    try
    {
        while (await work.DequeueAsync(working.Token) is { } item)
        {
            if (control is { Stop: not StopKind.None })
                break;
            // BeginWork belongs to the first stage, so an item counts as in-hand for its whole journey
            // across both queues. Exactly one of the three stages calls the matching EndWork.
            uploadTracker.BeginWork();
            var handed = false;
            try
            {
                ProbedItem probed;
                if (item.Single is { } single)
                {
                    var hit = await ProbeAndResumeAsync(
                        request, single, Local(request, single.Path), localResolver, uploadTracker,
                        control, working.Token);
                    probed = new ProbedItem(item, hit);
                }
                else
                {
                    probed = new ProbedItem(item, Hit: null);   // a pack has no pre-compression probe
                }
                await probedQueue.Writer.WriteAsync(probed, working.Token);
                handed = true;
            }
            catch (Exception ex)
            {
                // The source went unreadable between the diff and this read. Settle it and carry on; one
                // file must never take the run down. Not an exception filter: it has to await.
                if (item.Single is not { } f || !await TrySettleUnreadableAsync(
                        request, f, Local(request, f.Path), ex, postDiffUnreadable, ReportItem, working.Token))
                    throw;
            }
            finally
            {
                if (!handed)
                    uploadTracker.EndWork();
            }
        }
    }
    finally
    {
        // However this loop ends, the compressor must learn there is no more work, or it waits forever.
        probedQueue.Writer.Complete();
    }
}

async Task CompressAsync()
{
    try
    {
        await foreach (var probed in probedQueue.Reader.ReadAllAsync(working.Token))
        {
            var handed = false;
            try
            {
                handed = await StageProbedAsync(probed, working.Token);
            }
            catch (Exception ex)
            {
                if (probed.Item.Single is not { } f || !await TrySettleUnreadableAsync(
                        request, f, Local(request, f.Path), ex, postDiffUnreadable, ReportItem, working.Token))
                    throw;
            }
            finally
            {
                if (!handed)
                    uploadTracker.EndWork();
            }
        }
    }
    finally
    {
        stagedQueue.Writer.Complete();
    }
}

async Task UploadAsync()
{
    await foreach (var pending in stagedQueue.Reader.ReadAllAsync(working.Token))
    {
        try
        {
            await pending.RunAsync(working.Token);
        }
        finally
        {
            pending.Handoff?.Dispose();
            uploadTracker.EndWork();
        }
    }
}
```

`StageProbedAsync` is where the two paths part. It returns true when it handed the item to `stagedQueue` (so the uploader owns the `EndWork`), false when the item is finished here:

```csharp
async Task<bool> StageProbedAsync(ProbedItem probed, CancellationToken token)
{
    // A dedup or resume hit: nothing to compress, and no archive to own. It still travels through the
    // queue rather than settling here, so that every single-file item's settle happens in one place —
    // and so the uploader's EndWork stays the single accounting exit.
    if (probed.Hit is { } hit)
    {
        var file = probed.Item.Single!;
        await stagedQueue.Writer.WriteAsync(
            new PendingUpload(null, t => FinishBlobAsync(
                request, file, hit, storageByPath, tailByPath, overrides, ReportItem, control, t)),
            token);
        return true;
    }

    if (probed.Item.Single is { } single)
    {
        var localPath = Local(request, single.Path);
        var storeOnly = request.Options.DontCompress?.MatchesFileOrAncestorDir(single.Path) ?? false;
        var staged = await StageBlobAsync(request, single, localPath, storeOnly, uploadTracker, state, token);
        // The retry unit is still "compress + upload", exactly as it is for a pack: the first pass uses the
        // archive this stage just produced, and any retry compresses a fresh one, because the failed pass
        // disposed its own. Reusing a disposed archive would upload volume files that are no longer on disk.
        await stagedQueue.Writer.WriteAsync(new PendingUpload(staged.Handoff, async t =>
        {
            StagedBlob? pending = staged;
            await WithPauseAsync(control, async () =>
            {
                var blob = pending ?? await StageBlobAsync(
                    request, single, localPath, storeOnly, uploadTracker, state, t);
                pending = null;
                var placement = await UploadStagedBlobItemAsync(
                    request, single, blob, addressing, localResolver, uploadScope, uploadTracker, state,
                    control, t);
                await FinishBlobAsync(
                    request, single, placement, storageByPath, tailByPath, overrides, ReportItem, control, t);
            }, t);
        }), token);
        return true;
    }

    // A pack pool splits into groups, each with its own pack id and its own retry unit, so the compressor
    // produces one queue entry per group. The group loop, the member re-queueing and the demotion to a
    // single file all stay here: they are decided before the upload and mutate state this loop owns.
    // ... adapt ProcessPackAsync's while loop so that, per group, it calls CompressGroupAsync and enqueues
    // a PendingUpload whose RunAsync calls RunGroupAsync(..., precompressed: attempt, ...) followed by the
    // RecordPackAsync / LogFileAsync / onItem tail that currently sits at BackupOrchestrator.cs:2049-2080 ...
    return true;
}
```

For the pack branch, work from `ProcessPackAsync` as Task 3 left it: the `while (queue.Count > 0)` loop keeps its structure, but where it now calls `RunGroupAsync` inline and then records, it instead calls `CompressGroupAsync` and enqueues the record-and-settle tail as the `PendingUpload`'s `RunAsync`. The resume short-circuit (`control?.Resume.FindPack`) settles in place exactly as it does today.

Start all three where the old consumers started (`BackupOrchestrator.cs:755-759`, `:1024-1025`):

```csharp
var uploaders = Math.Max(2, Math.Max(1, opts.UploadConcurrency) + 1);
List<Task> consumers = [];
void StartConsumers() =>
    consumers = [Task.Run(ProbeAsync, working.Token),
                 Task.Run(CompressAsync, working.Token),
                 .. Enumerable.Range(0, uploaders).Select(_ => Task.Run(UploadAsync, working.Token))];
```

The uploader count stays `UploadConcurrency + 1`: the extra consumer is what keeps the volume gate's hand-off from stalling at item boundaries (`VolumeBlobIO.cs:143-167`), and that reasoning is untouched here.

The existing `SettleAsync(consumers)` / `Task.WhenAll(consumers)` call sites (`:1010`, `:1013`, `:1019`, `:1052`, `:1055`) need no change — `consumers` now holds two more tasks.

Neither channel needs a depth limit. `stagedQueue`'s depth is bounded in bytes by the staging pool, which is the bound the operator configured; `probedQueue` holds items that own nothing yet.
- [ ] **Step 4: Run the acceptance test, then the full suite**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj \
  --filter "FullyQualifiedName~CompressionContinuityTests"
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: the new test passes; the full suite passes with the same count as before plus the new tests.

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs \
        backend/tests/AzureStorageBackup.Api.Tests/CompressionContinuityTests.cs
git commit -m "$(cat <<'EOF'
feat: run compression and upload as two stages

One worker used to own an item from compression through the last volume of
its upload, and there were only UploadConcurrency + 1 of them. Once that
many items were uploading, none was left to reach StageAsync and
compression stopped — measured with 23 items queued, 4.5 GB staged, and
both preparing and waitingOnArchive at zero.

Now a single compressor (compression is already globally serial) feeds a
channel that UploadConcurrency + 1 uploaders drain. The compressor's only
blocking point is the staging quota, so StagedLimitBytes finally binds.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: The three acceptance tests the design still owes

**Task 4 already landed this task's implementation work.** It had to: four existing resume/stop tests went red without stop rechecks in the compressor and uploader plus a `DrainQueues()`, because a prober that races ahead makes the old single `break` meaningless — the stop had nothing left to prevent. The original plan called `DrainQueues` from `SettleStopAsync`, which runs *after* the consumers settle and would have been a no-op.

So what remains is evidence. Of the six tests `docs/compression-upload-pipeline-design.md` lists, three have landed:

- compression proceeds while every uploader is blocked — `CompressionContinuityTests` (Task 4)
- downgrade releases everything queued — the F1 regression test asserts `StagedBytes == 0` after an auto-suspend that kills every uploader (`BackupPauseGateIntegrationTests`)
- upload failure recompresses under the same pack id — existing `BackupPackRetryUnitTests` plus F2's new test

Three are still owed, and two of them cover the paths where Task 4's Critical hang lived — which is the argument for writing them rather than declaring the feature done.

**Files:**
- Modify: `backend/tests/AzureStorageBackup.Api.Tests/CompressionContinuityTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-4. No production code should need to change; if a test cannot be written without changing production code, stop and report that — it is a finding about the design, not a licence to edit.

- [ ] **Step 1: The staging limit binds**

This is the assertion the whole rework exists for. Before it, the worker pool saturated first and `StagedLimitBytes` had no effect at any value.

```csharp
/// <summary>
/// The point of the whole rework: the staging limit, not the worker pool, is what stops compression.
/// Before it the pool saturated first, so this setting had no effect at any value — 10 GB, 2 GB and
/// 40 GB all produced identical behaviour.
/// </summary>
[Fact]
public async Task The_Staging_Limit_Is_What_Stops_Compression()
{
    if (!AzuriteReachable() || !SevenZip())
        return;

    for (var i = 0; i < 12; i++)
        WriteFile($"f{i}.bin", FileSize);

    var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var factory = new BlobClientFactory();
    // Four items' worth of room. HasRoom admits a caller whose current usage is below the limit, so a
    // single archive may overshoot it — hence the slack in the assertion below.
    var limit = 4L * FileSize;
    var (orchestrator, staging, request) = Build(
        new BlockingUploader(block.Task, new BlobUploader(factory)),
        stagingLimit: limit, uploadConcurrency: 2);

    var seen = new List<StageProgress>();
    var progress = new Progress<BackupProgress>(p =>
    {
        if (p.Detail is { Stage: "Uploading" } d)
            lock (seen) seen.Add(d);
    });

    var run = orchestrator.RunAsync(request, progress);
    try
    {
        // The compressor now queues on the quota instead of on a free worker, and says so on screen.
        // That column reading non-zero is the visible evidence the operator never sees today.
        await WaitUntil(
            () => { lock (seen) return seen.Any(s => s.WaitingOnArchive > 0); },
            TimeSpan.FromSeconds(60));
        Assert.True(staging.StagedBytes <= limit + FileSize,
            $"pool grew past the limit plus one archive: {staging.StagedBytes}");
    }
    finally
    {
        block.SetResult();
    }

    await run;
}
```

- [ ] **Step 2: Stop releases everything still queued**

Both queues, and the assertion is on the pool because that is what a leak costs: the quota is booked on a process-wide singleton, so anything left behind throttles every other backup on the machine until the process restarts.

```csharp
[Fact]
public async Task Stop_Releases_Everything_Still_Queued()
{
    if (!AzuriteReachable() || !SevenZip())
        return;

    for (var i = 0; i < 12; i++)
        WriteFile($"f{i}.bin", FileSize);

    var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var factory = new BlobClientFactory();
    var (orchestrator, staging, request) = Build(
        new BlockingUploader(block.Task, new BlobUploader(factory)),
        stagingLimit: 200_000_000, uploadConcurrency: 2);

    var journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
    await using var control = new BackupRunControl(journals, configId: 1, runId: "stop-drain");

    var run = orchestrator.RunAsync(request, progress: null, ct: default, control: control);
    await WaitUntil(() => staging.StagedBytes > 4L * FileSize, TimeSpan.FromSeconds(60));

    // FinishCurrentFiles is the ordinary stop: the item in hand finishes, nothing new starts.
    // Everything the compressor produced that no uploader claimed has to be handed back.
    control.RequestStop(StopKind.FinishCurrentFiles);
    block.SetResult();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

    Assert.Equal(0, staging.StagedBytes);
    Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_temp, "staged")));
}
```

- [ ] **Step 3: The ledger balances with entries parked in the queues**

```csharp
/// <summary>
/// The identity the operator uses to judge "did work vanish": processed + preparing + queued +
/// waitingOnArchive + uploading == total. Entries parked in either queue fall under `uploading`
/// (inWork - inStaging), so the sum must not drift while both queues are full.
/// </summary>
[Fact]
public async Task The_Item_Ledger_Balances_With_Entries_Parked_In_The_Queues()
{
    if (!AzuriteReachable() || !SevenZip())
        return;

    for (var i = 0; i < 12; i++)
        WriteFile($"f{i}.bin", FileSize);

    var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var factory = new BlobClientFactory();
    var (orchestrator, staging, request) = Build(
        new BlockingUploader(block.Task, new BlobUploader(factory)),
        stagingLimit: 200_000_000, uploadConcurrency: 2);

    var seen = new List<StageProgress>();
    var progress = new Progress<BackupProgress>(p =>
    {
        if (p.Detail is { Stage: "Uploading" } d)
            lock (seen) seen.Add(d);
    });

    var run = orchestrator.RunAsync(request, progress);
    try
    {
        await WaitUntil(() => staging.StagedBytes > 4L * FileSize, TimeSpan.FromSeconds(60));

        // The total only settles once the diff finishes, so only snapshots that have one can be checked.
        List<StageProgress> settled;
        lock (seen) settled = [.. seen.Where(s => s.Total > 0)];
        Assert.NotEmpty(settled);
        foreach (var s in settled)
            Assert.Equal(
                s.Total,
                s.Processed + s.Preparing + s.Queued + s.WaitingOnArchive + s.Uploading);
    }
    finally
    {
        block.SetResult();
    }

    await run;
}
```

- [ ] **Step 4: Run the new tests, then the whole suite**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj \
  --filter "FullyQualifiedName~CompressionContinuityTests"
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: the three new tests pass; the full suite reports **1237 passed, 0 failed, 0 skipped** (1234 + 3). Skipped tests mean Azurite died and the integration coverage silently vanished — report that rather than treating it as green.

If the ledger test fails, a `BeginWork`/`EndWork` pair has been broken — check the `WorkShare` release points in the prober's `if (!handed)`, the compressor's `finally`, and the uploader's `finally`.

- [ ] **Step 5: Commit**

```bash
git add backend/tests/AzureStorageBackup.Api.Tests/CompressionContinuityTests.cs
git commit -m "$(cat <<'EOF'
test: pin the staging limit as the binding constraint

Three claims from the design were still unasserted: that compression now
stops at StagedLimitBytes rather than at the size of the worker pool, that
a stop hands back everything still queued, and that the item ledger
balances while entries sit in the two queues.

The first and last cover the paths where the compressor-hang bug lived,
which is why they are worth writing rather than declaring the feature done.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-review notes

**Spec coverage.** Every section of `docs/compression-upload-pipeline-design.md` maps to a task: §1 three stages → Task 4; §2 the ownership guard and the release paths → Task 1 (the guard), Tasks 2-3 (normal and exception paths), Task 4 (stop, downgrade and the drain); §3 recompress in place → Task 3; §4 changed members stay on the compression side → Task 3; §5 progress accounting → Task 4's `WorkShare` and Task 5's ledger test. The design's six tests → three landed during Tasks 3-4, three in Task 5.

**Naming.** The design calls the queue entry `StagedUnit`; the implementation is `StagedHandoff` (the guard) plus `PendingUpload` and `ProbedItem` (the queue entries). Same thing, split because the guard is unit-testable on its own and the closures are not.

**Where the plan was wrong, and what corrected it.** Task 2's interface block contradicted its own code (corrected in `57b931c`). Task 3's sketch left a disposed archive reachable and put `precompressed` at pool granularity where a second group would re-consume it — the implementer caught both. Task 4's `UploadAsync` would have fired one `EndWork` per queue entry against one `BeginWork` per item, which `WorkShare` fixed, and its stage count went from two to three mid-flight (`1209c07`). The plan was a starting point in each case, not the authority.
