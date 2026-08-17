# Compression/upload pipeline split — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the compression stage independent of the upload stage, so that `StagedLimitBytes` — not the size of the worker pool — is what limits how far compression runs ahead.

**Architecture:** The single consumer loop in `BackupOrchestrator.RunAsync` becomes two: exactly one compressor (compression is already globally serial behind `StagingArea._compressLock`) and `UploadConcurrency + 1` uploaders, joined by an unbounded `Channel<PendingUpload>`. The compressor stops at "the archive is in the staged pool" and packages the rest of the item's work as a closure; an uploader runs that closure. Queue depth is bounded in bytes by the staging quota, which is the bound the operator configured.

**Tech Stack:** C# / .NET, xUnit, `System.Threading.Channels`. Integration tests run against Azurite.

## Global Constraints

- **Everything written into the repository is English** — code, comments, commit messages, docs. (Conversation with the user stays Chinese.)
- **Commit messages end with:** `Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>`
- **Integration tests need Azurite**: `npx azurite --skipApiVersionCheck` — without it 189 tests skip silently and a green run means nothing.
- **Do not change** `VolumeUploadGate`, `VolumeUploadScope`, `StagingArea`'s quota semantics, or any index/journal format.
- **The progress identity must keep holding:** `processed + preparing + queued + waitingOnArchive + uploading ≡ total` (`StageProgress.cs:916-924`).
- **`BeginWork`/`EndWork` must stay exactly paired.** `BeginWork` is called once when the compressor claims an item; `EndWork` is called once when that item leaves the pipeline — by the uploader after it settles, or by the compressor if the item never entered the queue. An unpaired call permanently skews the `uploading` column.
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

### Task 4: Two loops joined by a staged queue

This is the task that changes behaviour. Write the acceptance test first — it fails on today's code for the reason the spec documents.

**Files:**
- Create: `backend/tests/AzureStorageBackup.Api.Tests/CompressionContinuityTests.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs:721-759` (the consumer loop and its startup), `:1004` / `:1013` / `:1052-1055` (settle points)

**Interfaces:**
- Consumes: `StagedHandoff` (Task 1), `StageBlobAsync` / `UploadStagedBlobItemAsync` (Task 2), `CompressGroupAsync` / `UploadGroupAsync` (Task 3).
- Produces:
  - `private sealed record PendingUpload(StagedHandoff Handoff, Func<CancellationToken, Task> RunAsync)`
  - A `Channel<PendingUpload>` local to `RunAsync`, written by the compressor and read by the uploaders.

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

- [ ] **Step 3: Introduce the queue and the two loops**

In `RunAsync`, replace the single `ConsumeAsync` (`BackupOrchestrator.cs:721-744`) with:

```csharp
// Compression is globally serial (StagingArea holds one compression lock across every run), so the compression
// side is one worker by definition — a pool would just be a queue in front of a lock that admits one.
// Its only blocking point is the staging quota, which is what makes StagedLimitBytes the setting it claims to be.
var stagedQueue = Channel.CreateUnbounded<PendingUpload>(
    new UnboundedChannelOptions { SingleWriter = true, SingleReader = false });

async Task CompressAsync()
{
    try
    {
        while (await work.DequeueAsync(working.Token) is { } item)
        {
            if (control is { Stop: not StopKind.None })
                break;
            uploadTracker.BeginWork();
            var handed = false;
            try
            {
                // Returns null when the item settled here (a dedup or resume hit uploads nothing at all).
                if (await StageItemAsync(item, working.Token) is { } pending)
                {
                    await stagedQueue.Writer.WriteAsync(pending, working.Token);
                    handed = true;
                }
            }
            finally
            {
                // EndWork belongs to whoever finishes the item: the uploader once it settles, or this loop when
                // the item never made it into the queue. Exactly one of the two runs it.
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
            pending.Handoff.Dispose();
            uploadTracker.EndWork();
        }
    }
}
```

`StageItemAsync` dispatches on the work item exactly as `RunItemAsync` did, calling the compress half from Task 2 or Task 3 and packaging the upload half in the closure:

```csharp
async Task<PendingUpload?> StageItemAsync(WorkItem item, CancellationToken token)
{
    if (item.Single is { } single)
    {
        // The probe and the two dedup tiers settle the item without compressing anything; that path finishes
        // here and hands nothing on.
        ...
        var stagedBlob = await StageBlobAsync(request, single, ..., token);
        return new PendingUpload(stagedBlob.Handoff, t => WithPauseAsync(control, () =>
            FinishBlobAsync(request, single, stagedBlob, ..., t), t));
    }

    // A pack pool splits into several groups, each with its own pack id and its own retry unit. The compressor
    // takes one group at a time so that the queue drains group by group instead of pool by pool.
    ...
}
```

Start them where the old consumers started (`BackupOrchestrator.cs:755-759`, `:1024-1025`):

```csharp
var workers = Math.Max(2, Math.Max(1, opts.UploadConcurrency) + 1);
List<Task> consumers = [];
void StartConsumers() =>
    consumers = [Task.Run(CompressAsync, working.Token),
                 .. Enumerable.Range(0, workers).Select(_ => Task.Run(UploadAsync, working.Token))];
```

The uploader count stays `UploadConcurrency + 1`: the extra consumer is what keeps the volume gate's hand-off from stalling at item boundaries (`VolumeBlobIO.cs:143-167`), and that reasoning is untouched here.

The existing `SettleAsync(consumers)` / `Task.WhenAll(consumers)` call sites (`:1010`, `:1013`, `:1019`, `:1052`, `:1055`) need no change — `consumers` now simply holds one more task.

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

### Task 5: Drain the queue on stop and on downgrade

The queue can now hold up to the whole staging limit. Two exits must empty it, or that much quota stays booked on the singleton: a user stop, and the pause gate's downgrade — `PauseGate.cs:23-27` warns that a suspended run holding the staging seat "blocks every parallel backup completely", and this change makes the amount it can hold much larger.

Per the decision recorded in the spec: entries an uploader has already claimed run to completion; entries still in the queue are discarded.

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs` (`SettleStopAsync` at `:501-515`, and the catch blocks at `:1007-1021`)
- Test: `backend/tests/AzureStorageBackup.Api.Tests/CompressionContinuityTests.cs`

**Interfaces:**
- Consumes: the `stagedQueue` and `PendingUpload` from Task 4.
- Produces: `void DrainStagedQueue()` — local function in `RunAsync`.

- [ ] **Step 1: Write the failing test**

```csharp
/// <summary>
/// Stop with a full queue: everything compressed but unclaimed is discarded, and its quota goes back. The quota
/// lives on a process-wide singleton, so anything left booked here stays booked until the process restarts and
/// throttles every other backup on the machine.
/// </summary>
[Fact]
public async Task Stop_Releases_Everything_Still_Queued()
{
    if (!AzuriteReachable() || !SevenZip())
        return;

    for (var i = 0; i < 12; i++)
        WriteFile($"f{i}.bin", 2 * 1024 * 1024);

    var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var factory = new BlobClientFactory();
    var (orchestrator, staging, request) = Build(
        new BlockingUploader(block.Task, new BlobUploader(factory)),
        stagingLimit: 200_000_000, uploadConcurrency: 2);

    var journals = new BackupJournalStore(Path.Combine(_temp, "journal"));
    await using var control = new BackupRunControl(journals, configId: 1, runId: "stop-drain");

    var run = orchestrator.RunAsync(request, progress: null, ct: default, control: control);
    await WaitUntil(() => staging.StagedBytes > 4L * FileSize, TimeSpan.FromSeconds(60));

    // FinishCurrentFiles is the ordinary stop: the item in hand finishes, nothing new starts. Everything the
    // compressor produced that no uploader claimed has to be handed back.
    control.RequestStop(StopKind.FinishCurrentFiles);
    block.SetResult();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

    Assert.Equal(0, staging.StagedBytes);
    Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_temp, "staged")));
}
```

`BackupRunControl`'s constructor is `(BackupJournalStore store, int configId, string runId, PauseGate? gate = null)` and it is `IAsyncDisposable`; `BackupCancelModesTests` builds one the same way.

- [ ] **Step 2: Run it to verify it fails**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj \
  --filter "FullyQualifiedName~CompressionContinuityTests.Stop_Releases"
```

Expected: FAIL — `StagedBytes` is non-zero, and volume files are left in staged-temp.

- [ ] **Step 3: Implement the drain**

```csharp
// Whatever the compressor produced that no uploader claimed. Every entry here is compressed but has not sent a
// byte, so discarding it costs local CPU only and leaves nothing behind in the container. Its quota, though, is
// booked on a singleton shared by every run — leave it booked and this machine's other backups pay for it until
// the process restarts.
void DrainStagedQueue()
{
    while (stagedQueue.Reader.TryRead(out var pending))
    {
        pending.Handoff.Dispose();
        uploadTracker.EndWork();
    }
}
```

Call it in `SettleStopAsync` before `control.FlushAsync` (`BackupOrchestrator.cs:505`), and in the `catch` blocks at `:1007-1021` after `SettleAsync(consumers)`. The downgrade path reaches `SettleStopAsync` through `BackupSuspendedException`, so both exits are covered by the same two call sites — confirm this while implementing by tracing where `BackupSuspendedException` from `WithPauseAsync` (`:1881`) is handled.

- [ ] **Step 4: Run the test, then the full suite**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj \
  --filter "FullyQualifiedName~CompressionContinuityTests"
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: PASS. `BackupCancelModesTests` and `GracefulSuspendTests` are the ones to watch — they pin the stop and suspend semantics this touches.

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs \
        backend/tests/AzureStorageBackup.Api.Tests/CompressionContinuityTests.cs
git commit -m "$(cat <<'EOF'
fix: release the staged queue on stop and on downgrade

The queue can hold up to the whole staging limit, and its quota is booked
on a process-wide singleton. Leaving it booked on the way out throttles
every other backup on the machine until the process restarts — the failure
PauseGate already warns about, now with much more to leak.

Claimed entries still run to completion; queued ones are discarded, which
costs local CPU only and leaves nothing in the container.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: Prove the limit binds and the ledger balances

Two claims from the spec are still unasserted: that `StagedLimitBytes` is now the binding constraint, and that the item ledger still balances with entries sitting in the queue.

**Files:**
- Modify: `backend/tests/AzureStorageBackup.Api.Tests/CompressionContinuityTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 4 and 5.
- Produces: no production code.

- [ ] **Step 1: Write the tests**

```csharp
/// <summary>
/// The point of the whole rework: the staging limit, not the worker pool, is what stops compression. Before it,
/// the pool saturated first and this setting had no effect at any value.
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
    // Four items' worth of room. HasRoom admits a caller whose current usage is below the limit, so a single
    // archive may overshoot it — hence the slack in the assertion below.
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
        // The compressor now queues on the quota instead of on a free worker, and says so.
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

/// <summary>
/// The identity the operator uses to judge "did work vanish": processed + preparing + queued + waitingOnArchive
/// + uploading == total. Entries parked in the staged queue fall under `uploading` (inWork - inStaging), so the
/// sum must not drift while the queue is full.
/// </summary>
[Fact]
public async Task The_Item_Ledger_Balances_With_Entries_Parked_In_The_Queue()
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

Confirm `BackupProgress.Detail`'s type and the `StageProgress` property names against `StageProgress.cs:120-135` while writing these; `StageByteBreakdownTests` and `UploadWaitVisibilityTests` assert the same columns and are the reference for how.

- [ ] **Step 2: Run them**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj \
  --filter "FullyQualifiedName~CompressionContinuityTests"
```

Expected: PASS. If the ledger test fails, a `BeginWork`/`EndWork` pair has been broken — check the `handed` flag in `CompressAsync` and the `finally` in `UploadAsync`.

- [ ] **Step 3: Run the whole suite one final time**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: PASS, no skips beyond the usual environment-gated ones. Confirm Azurite was up — a silent skip of 189 integration tests would make this green and meaningless.

- [ ] **Step 4: Commit**

```bash
git add backend/tests/AzureStorageBackup.Api.Tests/CompressionContinuityTests.cs
git commit -m "$(cat <<'EOF'
test: pin the staging limit as the binding constraint

Two claims from the design were still unasserted: that compression now
stops at StagedLimitBytes rather than at the size of the worker pool, and
that the item ledger balances while entries sit in the staged queue.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
EOF
)"
```

---

## Self-review notes

**Spec coverage.** Every section of `docs/compression-upload-pipeline-design.md` maps to a task: §1 two stages → Task 4; §2 `StagedUnit` and the four release paths → Task 1 (the guard), Tasks 2–3 (normal and exception paths), Task 5 (stop and downgrade); §3 recompress in place → Task 3 Step 3; §4 changed members stay on the compression side → Task 3 Step 1, where they are returned by `CompressGroupAsync`; §5 progress accounting → Task 4 Step 3 and Task 6.

**Naming.** The spec calls the queue entry `StagedUnit`; this plan implements it as `StagedHandoff` (the guard) plus `PendingUpload` (the queue entry that pairs it with the closure). Same thing, split because the guard is unit-testable on its own and the closure is not.

**Known thin spot.** Task 4 Step 3 gives the shape of `StageItemAsync` rather than a complete body: it dispatches on `WorkItem` exactly as today's `RunItemAsync` does, and the two halves it calls are fully specified by Tasks 2 and 3. Everything else in the plan is complete code. Where a step depends on a signature that must be read from the current source (`BackupEngineOptions`' property names, `BackupInfoStore`'s constructor, `StageProgress`' column names), the step names the file to check.
