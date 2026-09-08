# SQLite Index Catalog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move every structure whose size grows with the file count out of process memory into two SQLite files, so a backup of several million files peaks under 1 GB and a run suspended by the current release still resumes.

**Architecture:** A per-container `catalog.db` holds every retained version's entries with the indexes dedup and browsing need; a per-run `work.db` holds the scan, the draft of the new version, dedup reservations and the journal's records. The cloud index bytes, the info file, the journal and `app.db` do not change. Consumers swap their data source and keep their decision logic; anything that rewrites an index goes patch table → cloud → catalog.

**Tech Stack:** .NET 10, `Microsoft.Data.Sqlite` (already referenced through `Microsoft.EntityFrameworkCore.Sqlite` 10.0.10 and `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12), xunit + `Xunit.SkippableFact`, NSubstitute, Azurite and the official `7zz` for integration tests.

**Spec:** `docs/superpowers/specs/2026-09-07-sqlite-index-catalog-design.md`

## Global Constraints

- Cloud index format, addressing, encryption, volumes and tier are unchanged. `IndexSerializer` schema-version 1 / index format 4 bytes are the contract; new writers must be byte-identical.
- Journal files, suspend marks, the info file, `LocalBackupState` and `app.db` are unchanged. No EF migration is added.
- `catalog.db` lives at `{dbDir}/index-cache/{accountId}/{Safe(container)}/catalog.db`; `work.db` at `{tempPath}/work/{runId}.db`.
- Raw `Microsoft.Data.Sqlite` only, connection strings carry `Pooling=false`. Never touch `app.db` from the catalog code.
- Catalog is written only after the corresponding cloud write succeeded. Never before.
- `cache_size` per connection is `-65536` (64 MB). A run opens at most five connections.
- Migrated `.idx` files and legacy `CachedVersionIndexes` rows are deleted after a verified import. Downgrade is unsupported.
- Entry emission order is preserved through a `seq` column; serialization orders by `seq`, browsing by `path`.
- Tests: `cd backend && dotnet test`. Azurite must run (`npx azurite --location /tmp/azurite --skipApiVersionCheck --silent`, start it before and stop/wipe it after) and `7zz` must be on `PATH`, or a quarter of the suite silently skips. Integration tests use `[SkippableFact]` with `Skip.IfNot(AzuriteReachable(), "Azurite not running")` / `Skip.IfNot(SevenZip(), "7z not found")` copied from `BackupOrchestratorTests.cs:40-47`.
- Commit after every task. Commit messages follow the repo style: `feat(scope): what changed`, `test(scope): …`, `refactor(scope): …`, `docs: …`, body explains why, ends with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- Do not release. A release stops at the registry and is a separate, user-initiated step.

## File Structure

New files (all under `backend/src/AzureStorageBackup.Api/Services/` unless noted):

| File | Responsibility |
|---|---|
| `CatalogSql.cs` | Connection-string builder, pragmas, schema DDL for `catalog.db`, the `WITHOUT ROWID` tables and indexes. |
| `VersionCatalog.cs` | One open `catalog.db`: import a version from an `IndexStreamReader`, serialize a version to a `Stream`, all point/range/aggregate queries, patch application, version removal. `IAsyncDisposable`. |
| `VersionCatalogStore.cs` | Root directory → `VersionCatalog` handles; per-container async write lock; corrupt-file recovery; `RemoveContainer`. Singleton. |
| `IVersionCatalogs.cs` | `IVersionCatalogs` (scoped): `OpenAsync`, `EnsureVersionAsync` (lazy migration chain), `RemoveVersionAsync`, `RemoveContainerAsync`. Replaces `ILocalIndexCache`. |
| `VersionCatalogs.cs` | Implementation of `IVersionCatalogs` over `VersionCatalogStore`, `VersionIndexFileStore` (read-only, for `.idx` migration), `AppDbContext` (legacy rows) and `IBackupInfoStore` (cloud). |
| `IndexStreamWriter.cs` | Writes index format 4 to a `Stream` from counts + sequences. Byte-identical with `IndexSerializer.SerializeIndex`. |
| `IndexStreamReader.cs` | Reads index format ≤ 4 from a `Stream`, yielding entries one at a time. |
| `RunWorkDb.cs` | One `work.db`: schema, single writer channel with batched commits, scan/draft/reservation/resume tables, ordered cursors. `IAsyncDisposable`, deletes its file on dispose. |
| `RunWorkDbFactory.cs` | `{tempPath}/work` root, `Create(runId)`, `ClearStale()`. Singleton. |
| `ScanSink.cs` | `IScanSink` the scanner writes into; `WorkDbScanSink` (production) and `ListScanSink` (tests). |
| `RunLedger.cs` | The per-run ledger over `work.draft`: replaces `storageByPath`, `tailByPath`, `overrides`, `postDiffUnreadable`. |
| `ResumeLedger.cs` | `JournalResume`'s four lookups over `work.resume_*`. |

Modified: `LocalFileScanner.cs`, `BackupDiffer.cs`, `LocalDedupResolver.cs`, `BackupRunControl.cs`, `BackupOrchestrator.cs`, `IArchiveCodec.cs`, `SevenZipArchiveCodec.cs`, `IBackupInfoStore.cs`, `BackupInfoStore.cs`, `VersionTreeService.cs`, `RestoreEstimator.cs`, `LocalRootMigration.cs`, `RestoreOrchestrator.cs`, `RetentionCleaner.cs`, `BackupChecker.cs`, `BackupRepairer.cs`, `DeferredRepairs.cs`, `Endpoints/BackupConfigEndpoints.cs`, `Program.cs`, `AzureStorageBackup.Api.csproj`, `docs/storage-format.md`, `docs/architecture.md`, `README.md`.

Deleted at the end: `LocalIndexCache.cs`, `VersionIndexMemoryCache.cs`, `JournalResume.cs`; `VersionIndexFileStore.cs` shrinks to its reader. `IndexSerializer.SerializeIndex/DeserializeIndex` move to the test project as `LegacyIndexSerializer`.

Test files: one per new class plus `Legacy*` copies of the old differ, dedup resolver and journal resume as oracles, under `backend/tests/AzureStorageBackup.Api.Tests/`.

---

## Phase 0: capture the cross-release fixtures before anything changes

### Task 0: Record a suspended run made by the current release

This task runs on the unmodified code. Its output is committed test data used by Task 22. Do it first; once Task 1 lands the current release's behavior is no longer reproducible from the tree.

**Files:**
- Create: `backend/tests/AzureStorageBackup.Api.Tests/Fixtures/resume-2026.9.7/` (directory of files, see step 3)
- Create: `backend/tests/AzureStorageBackup.Api.Tests/FixtureRecorderTests.cs`

**Interfaces:**
- Produces: a directory the Task 22 test reads: `journal/{runId}.jsonl`, `journal/{runId}.jsonl.suspended` (the mark), `index-cache/1.idx`, `info.bin` (the local authoritative info bytes), `blobs/` (every blob of the container, name-encoded with `/` → `__`), `expected/v2.idx` (the index the current release produces when the run is resumed to completion), `expected/info.bin`, `source/` (the local tree).

- [ ] **Step 1: Write the recorder test**

The recorder is an ordinary `[SkippableFact]` with `Skip.IfNot(Environment.GetEnvironmentVariable("ASB_RECORD_FIXTURES") == "1", "recording only")`, so it never runs in CI. It builds a 40-file tree (`source/`) with two files above `SingleFileThresholdBytes` and the rest small so packs form, runs one full backup to Azurite (version 1), modifies 10 files and adds 5, starts a second run with a `BackupRunControl` and an `IBlobUploader` wrapper that calls `control.RequestStop(StopKind.Suspend)` after the third successful upload, awaits the `BackupSuspendedException`, then copies the journal directory, the `.idx` files, the local state's info bytes and every blob in the container into the fixture directory. Then it resumes the run with a fresh orchestrator to completion and copies the resulting `indexes/v2.json` bytes (decoded through the codec, so the fixture is the plain serialized index) and info file into `expected/`.

Use `BackupResumeTests.cs:172-213` as the template for `Build(uploader)` and `Request(...)`; the suspend-after-N-uploads uploader wrapper already exists there — copy it.

```csharp
[SkippableFact]
public async Task Record_resume_fixture()
{
    Skip.IfNot(Environment.GetEnvironmentVariable("ASB_RECORD_FIXTURES") == "1", "recording only");
    Skip.IfNot(AzuriteReachable(), "Azurite not running");
    Skip.IfNot(SevenZip(), "7z not found");

    var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../Fixtures/resume-2026.9.7"));
    if (Directory.Exists(fixture)) Directory.Delete(fixture, recursive: true);
    Directory.CreateDirectory(fixture);

    // 1. source tree (deterministic content so the fixture is reproducible)
    var source = Path.Combine(fixture, "source");
    WriteTree(source, seed: 7, smallFiles: 38, largeFiles: 2);

    // 2. version 1
    var account = AzuriteAccount();
    var container = RandomName("fixture");
    var journals = new BackupJournalStore(Path.Combine(fixture, "journal-root"));
    var (orchestrator, store, factory) = Build(uploader: null, journals: journals, indexRoot: Path.Combine(fixture, "index-cache-root"));
    await orchestrator.RunAsync(Request(account, container, source));

    // 3. mutate, then suspend the second run after three uploads
    MutateTree(source, seed: 8, modify: 10, add: 5);
    var stopAfter = new SuspendAfterUploads(realUploader, count: 3);
    var (o2, _, _) = Build(uploader: stopAfter, journals: journals, indexRoot: ...);
    await using var control = new BackupRunControl(journals, configId: 1, runId: "fixture-run");
    stopAfter.Control = control;
    await Assert.ThrowsAsync<BackupSuspendedException>(() => o2.RunAsync(Request(account, container, source), control: control));

    // 4. snapshot everything the next release has to read
    CopyDirectory(Path.Combine(fixture, "journal-root"), Path.Combine(fixture, "journal"));
    CopyDirectory(Path.Combine(fixture, "index-cache-root"), Path.Combine(fixture, "index-cache"));
    await File.WriteAllBytesAsync(Path.Combine(fixture, "info.bin"), await LocalInfoBytesAsync(account, container));
    await DownloadAllBlobsAsync(factory, account, container, Path.Combine(fixture, "blobs"));

    // 5. what the current release produces when it finishes the run
    var (o3, store3, _) = Build(uploader: null, journals: journals, indexRoot: ...);
    var result = await o3.RunAsync(Request(account, container, source), control: new BackupRunControl(journals, 1, "fixture-run-2"));
    var info = await store3.ReadInfoAsync(account, container, null);
    var v2 = info!.Versions.Single(v => v.Version == 2);
    var index = await store3.ReadIndexAsync(account, container, v2.IndexBlob, null, v2.IndexVolumes);
    await File.WriteAllBytesAsync(Path.Combine(fixture, "expected", "v2.idx"), IndexSerializer.SerializeIndex(index));
    await File.WriteAllBytesAsync(Path.Combine(fixture, "expected", "info.bin"), IndexSerializer.SerializeInfoFile(info));
}
```

`WriteTree`, `MutateTree`, `CopyDirectory`, `DownloadAllBlobsAsync`, `LocalInfoBytesAsync` are private helpers in the same file; `WriteTree` seeds a `Random(seed)` and writes `file{i}.bin` of 1–20 KB under three directories, large files of 6 MB. `Build(...)` here differs from the template in taking the journal store and index root so the fixture captures them; construct `LocalIndexCache(db, store, new VersionIndexFileStore(indexRoot))` directly.

- [ ] **Step 2: Run the recorder**

Run: `ASB_RECORD_FIXTURES=1 dotnet test --filter FullyQualifiedName~FixtureRecorderTests`
Expected: PASS; `Fixtures/resume-2026.9.7/` populated with `journal/`, `index-cache/`, `info.bin`, `blobs/`, `expected/`, `source/`.

- [ ] **Step 3: Make the fixture part of the test build**

Add to `AzureStorageBackup.Api.Tests.csproj`:

```xml
<ItemGroup>
  <None Include="Fixtures/resume-2026.9.7/**" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 4: Commit**

```bash
git add backend/tests/AzureStorageBackup.Api.Tests/Fixtures/resume-2026.9.7 backend/tests/AzureStorageBackup.Api.Tests/FixtureRecorderTests.cs backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
git commit -m "test(fixtures): record a run suspended by 2026.9.7 for the catalog migration's resume test"
```

---

## Phase A: foundations

### Task 1: Streaming index writer and reader, byte-identical with IndexSerializer

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/IndexStreamWriter.cs`
- Create: `backend/src/AzureStorageBackup.Api/Services/IndexStreamReader.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/IndexStreamTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed class IndexStreamWriter : IDisposable
  {
      public IndexStreamWriter(Stream output);                       // leaves the stream open
      public void WriteHeader(int version, int entryCount);
      public void WriteEntry(IndexEntry entry);                      // exactly entryCount times
      public void WriteEmptyDirs(IReadOnlyList<string> dirs);
      public void WriteUnrecoverable(IReadOnlyList<string> paths);   // then Dispose flushes
  }
  public sealed class IndexStreamReader : IDisposable
  {
      public IndexStreamReader(Stream input);                        // reads the header eagerly
      public int Format { get; }  public int Version { get; }  public int EntryCount { get; }
      public IEnumerable<IndexEntry> Entries();                      // yields EntryCount entries, once
      public IReadOnlyList<string> ReadEmptyDirs();                  // after Entries() is exhausted
      public IReadOnlyList<string> ReadUnrecoverable();              // after ReadEmptyDirs()
  }
  ```

- [ ] **Step 1: Write the failing tests**

```csharp
public class IndexStreamTests
{
    private static VersionIndex Sample() => new()
    {
        Version = 7,
        Entries =
        [
            new IndexEntry { Path = "a/b.txt", Kind = "file", Length = 12, Mtime = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(8)),
                Permissions = "0644", HeadHash = "xxh128:" + new string('a', 32), TailHash = "xxh128:" + new string('b', 32),
                FullHash = "xxh128:" + new string('c', 32), Storage = new StorageRef { Kind = "blob", Ref = "data/abc", Volumes = 2, Raw = true, VolumeSizes = [10, 2] } },
            new IndexEntry { Path = "a/link", Kind = "symlink", Length = 0, Mtime = DateTimeOffset.UnixEpoch, Permissions = "0777", Target = "../x" },
            new IndexEntry { Path = "gone.txt", Kind = "file", Length = 3, Mtime = DateTimeOffset.UnixEpoch, Permissions = "0600",
                HeadHash = "sha256:notxxh", UnreadableAt = new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.Zero),
                Storage = new StorageRef { Kind = "pack", Ref = "p0001", EntryName = "gone.txt" } },
            new IndexEntry { Path = "empty", Kind = "file", Length = 0, Mtime = DateTimeOffset.UnixEpoch, Permissions = "0644" },
        ],
        EmptyDirs = ["a/empty", "z"],
        UnrecoverablePaths = ["gone.txt"],
    };

    [Fact]
    public void Writer_matches_IndexSerializer_byte_for_byte()
    {
        var index = Sample();
        using var ms = new MemoryStream();
        using (var w = new IndexStreamWriter(ms))
        {
            w.WriteHeader(index.Version, index.Entries.Count);
            foreach (var e in index.Entries) w.WriteEntry(e);
            w.WriteEmptyDirs(index.EmptyDirs);
            w.WriteUnrecoverable(index.UnrecoverablePaths);
        }
        Assert.Equal(IndexSerializer.SerializeIndex(index), ms.ToArray());
    }

    [Fact]
    public void Reader_reads_what_IndexSerializer_wrote()
    {
        var index = Sample();
        using var r = new IndexStreamReader(new MemoryStream(IndexSerializer.SerializeIndex(index)));
        Assert.Equal(7, r.Version);
        Assert.Equal(4, r.EntryCount);
        var entries = r.Entries().ToList();
        Assert.Equal(index.Entries, entries);
        Assert.Equal(index.EmptyDirs, r.ReadEmptyDirs());
        Assert.Equal(index.UnrecoverablePaths, r.ReadUnrecoverable());
    }

    [Fact]
    public void Reader_rejects_a_newer_format()
    {
        var bytes = IndexSerializer.SerializeIndex(Sample());
        bytes[0] = 99;
        Assert.Throws<NotSupportedException>(() => new IndexStreamReader(new MemoryStream(bytes)));
    }

    [Fact]
    public void Empty_index_round_trips()
    {
        var index = new VersionIndex { Version = 1 };
        using var ms = new MemoryStream();
        using (var w = new IndexStreamWriter(ms)) { w.WriteHeader(1, 0); w.WriteEmptyDirs([]); w.WriteUnrecoverable([]); }
        Assert.Equal(IndexSerializer.SerializeIndex(index), ms.ToArray());
    }
}
```

`IndexEntry` and `StorageRef` are records, so `Assert.Equal` on the lists compares by value; `StorageRef.VolumeSizes` is a `List<long>` and records compare lists by reference — add `Assert.Equal(index.Entries[0].Storage!.VolumeSizes, entries[0].Storage!.VolumeSizes)` and compare the other fields with `with { Storage = null }` copies, or write a small `AssertSameEntry` helper that compares every field explicitly. Do the explicit helper; it is reused by later tasks.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd backend && dotnet test --filter FullyQualifiedName~IndexStreamTests`
Expected: build error, `IndexStreamWriter` not found.

- [ ] **Step 3: Implement the writer and reader**

Copy the encoding primitives from `IndexSerializer.cs:286-372` (`WriteNullableString`, `WriteLongs`, `WriteDto`, `WriteNullableDto`, `WriteHash` and their readers) into a shared `internal static class IndexEncoding` in `IndexStreamWriter.cs`, and make `IndexSerializer` call them so there is one copy. Then:

```csharp
public sealed class IndexStreamWriter(Stream output) : IDisposable
{
    public const byte IndexFormat = 4;
    private readonly BinaryWriter _w = new(output, Encoding.UTF8, leaveOpen: true);
    private int _remaining = -1;

    public void WriteHeader(int version, int entryCount)
    {
        _w.Write(IndexFormat);
        _w.Write(version);
        _w.Write(entryCount);
        _remaining = entryCount;
    }

    public void WriteEntry(IndexEntry e)
    {
        if (_remaining <= 0) throw new InvalidOperationException("More entries than the header announced.");
        _remaining--;
        _w.Write(e.Path);
        _w.Write((byte)(e.Kind == "symlink" ? 1 : 0));
        _w.Write(e.Length);
        IndexEncoding.WriteDto(_w, e.Mtime);
        _w.Write(e.Permissions);
        IndexEncoding.WriteHash(_w, e.HeadHash);
        IndexEncoding.WriteHash(_w, e.TailHash);
        IndexEncoding.WriteHash(_w, e.FullHash);
        IndexEncoding.WriteNullableString(_w, e.Target);
        IndexEncoding.WriteNullableDto(_w, e.UnreadableAt);
        if (e.Storage is { } s)
        {
            _w.Write(true);
            _w.Write((byte)(s.Kind == "pack" ? 1 : 0));
            _w.Write(s.Ref);
            IndexEncoding.WriteNullableString(_w, s.EntryName);
            _w.Write(s.Volumes);
            _w.Write(s.Raw);
            IndexEncoding.WriteLongs(_w, s.VolumeSizes);
        }
        else _w.Write(false);
    }

    public void WriteEmptyDirs(IReadOnlyList<string> dirs)
    {
        if (_remaining != 0) throw new InvalidOperationException($"{_remaining} entries still owed.");
        _w.Write(dirs.Count);
        foreach (var d in dirs) _w.Write(d);
    }

    public void WriteUnrecoverable(IReadOnlyList<string> paths)
    {
        _w.Write(paths.Count);
        foreach (var p in paths) _w.Write(p);
    }

    public void Dispose() => _w.Dispose();   // flushes
}
```

The reader mirrors `IndexSerializer.DeserializeIndex` line for line, with `Entries()` as an iterator that reads one entry per `yield` and honours `format >= 2/3/4` exactly as `DeserializeIndex` does; `ReadUnrecoverable` returns `[]` when `Format < 3` without reading.

- [ ] **Step 4: Run the tests**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~IndexStreamTests|FullyQualifiedName~IndexSerializerTests"`
Expected: PASS, including the existing `IndexSerializerTests` (the primitives moved, the bytes did not).

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/IndexStreamWriter.cs backend/src/AzureStorageBackup.Api/Services/IndexStreamReader.cs backend/src/AzureStorageBackup.Api/Services/IndexSerializer.cs backend/tests/AzureStorageBackup.Api.Tests/IndexStreamTests.cs
git commit -m "feat(index): stream the version index format in and out without an in-memory object"
```

### Task 2: `catalog.db` schema and `VersionCatalog` import / serialize / queries

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/CatalogSql.cs`
- Create: `backend/src/AzureStorageBackup.Api/Services/VersionCatalog.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/VersionCatalogTests.cs`

**Interfaces:**
- Consumes: `IndexStreamReader`, `IndexStreamWriter` (Task 1).
- Produces:
  ```csharp
  public static class CatalogSql
  {
      public static string ConnectionString(string path, bool readOnly);   // Pooling=false; Mode=ReadOnly when readOnly
      public static void ApplyPragmas(SqliteConnection c, bool readOnly);  // journal_mode=WAL (writers only), synchronous=NORMAL, cache_size=-65536, busy_timeout=30000, foreign_keys=OFF
      public static void EnsureSchema(SqliteConnection c);                 // CREATE TABLE IF NOT EXISTS … (DDL below)
  }

  public sealed record CatalogVersionInfo(int Version, long Identity, int EntryCount, DateTimeOffset ImportedAt);
  public sealed record CatalogBlobHit(string Ref, bool Raw, int Volumes, IReadOnlyList<long> VolumeSizes);
  public sealed record CatalogRefOwner(string FullHash, long Length, string? HeadHash, string? TailHash, bool Damaged);
  public sealed record CatalogPackMember(string PackId, string EntryName, string? TailHash);
  public sealed record CatalogChild(string Name, bool IsDir, IndexEntry? Entry);
  public sealed record CatalogPatch(int Version, string Path, DateTimeOffset? UnreadableAt, bool? Unrecoverable, StorageRef? Storage);

  public sealed class VersionCatalog : IAsyncDisposable
  {
      public string Path { get; }
      // versions
      public Task<CatalogVersionInfo?> GetVersionAsync(int version, CancellationToken ct);
      public Task<IReadOnlyList<CatalogVersionInfo>> ListVersionsAsync(CancellationToken ct);
      public Task ImportVersionAsync(int version, long identity, IndexStreamReader reader, CancellationToken ct);   // one transaction; replaces an existing version row set
      public Task ImportVersionAsync(int version, long identity, int entryCount, IAsyncEnumerable<IndexEntry> entries,
                                     IReadOnlyList<string> emptyDirs, IReadOnlyList<string> unrecoverable, CancellationToken ct); // used by the run's finish
      public Task RemoveVersionAsync(int version, CancellationToken ct);
      public Task SerializeVersionAsync(int version, Stream output, IReadOnlyList<CatalogPatch>? patches, CancellationToken ct); // format 4, ORDER BY seq
      public Task ApplyPatchesAsync(IReadOnlyList<CatalogPatch> patches, CancellationToken ct);
      // browsing
      public Task<IndexEntry?> GetEntryAsync(int version, string path, CancellationToken ct);
      public Task<IReadOnlyList<CatalogChild>> ChildrenAsync(int version, string parent, CancellationToken ct);
      public IAsyncEnumerable<IndexEntry> EntriesAsync(int version, CancellationToken ct);                       // ORDER BY path
      public IAsyncEnumerable<IndexEntry> EntriesUnderAsync(int version, string dirPrefix, CancellationToken ct); // path = prefix OR path LIKE prefix/% (range scan, ORDER BY seq)
      public IAsyncEnumerable<IndexEntry> EntriesByStorageAsync(int version, CancellationToken ct);              // ORDER BY storage_kind, storage_ref, seq — groups arrive contiguous
      public Task<IReadOnlyList<IndexEntry>> EntriesAtAsync(int version, IReadOnlyCollection<string> paths, CancellationToken ct); // chunked IN (…) of 500
      public Task<IReadOnlyList<string>> EmptyDirsAsync(int version, CancellationToken ct);
      public Task<IReadOnlyList<string>> UnrecoverableAsync(int version, CancellationToken ct);                   // ORDER BY seq
      public Task<IReadOnlyList<(string Path, DateTimeOffset UnreadableAt)>> UnreadableAsync(int version, CancellationToken ct);
      public Task<IReadOnlyList<IndexEntry>> SampleAsync(int version, int max, CancellationToken ct);            // for LocalRootMigration: stratified by length bucket, see Task 16
      public Task<(long Files, long Bytes)> StatsAsync(int version, CancellationToken ct);
      public Task<IReadOnlyList<(string Path, int Version)>> CaseCollisionsAsync(int version, CancellationToken ct); // GROUP BY path_fold HAVING COUNT(*) > 1
      // dedup (across all versions)
      public Task<CatalogBlobHit?> FindBlobByContentAsync(string fullHash, long length, string? head, string? tail, CancellationToken ct);
      public Task<CatalogRefOwner?> FindRefOwnerAsync(string storageRef, CancellationToken ct);
      public Task<bool> IsDamagedRefAsync(string storageRef, CancellationToken ct);
      public Task<bool> HeadSeenAsync(long length, string headHash, CancellationToken ct);
      public Task<CatalogPackMember?> FindPackMemberAsync(string fullHash, long length, string headHash, CancellationToken ct);
      // maintenance
      public IAsyncEnumerable<string> DistinctRefsAsync(CancellationToken ct);                                   // every storage_ref of every version, blobs and packs, with kind
      public Task<IReadOnlyList<string>> RefsOnlyInAsync(IReadOnlyCollection<int> versions, string kind, CancellationToken ct); // refs referenced by these versions and by no other
      public IAsyncEnumerable<(string PackId, string EntryName, long Length, string FullHash)> LivePackMembersAsync(CancellationToken ct); // across all versions, DISTINCT by (pack, entry_name), latest version wins
      public IAsyncEnumerable<(int Version, IndexEntry Entry)> EntriesReferencingAsync(string storageRef, CancellationToken ct);
      public IAsyncEnumerable<(int Version, IndexEntry Entry)> PackMembersAsync(string packId, CancellationToken ct);
      public IAsyncEnumerable<string> UnrecoverableAnyVersionAsync(CancellationToken ct);                        // DISTINCT path
  }
  ```

The DDL (in `CatalogSql.EnsureSchema`):

```sql
CREATE TABLE IF NOT EXISTS versions (
  version INTEGER PRIMARY KEY, identity INTEGER NOT NULL, entry_count INTEGER NOT NULL, imported_at TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS entries (
  version INTEGER NOT NULL, path TEXT NOT NULL, seq INTEGER NOT NULL, parent TEXT NOT NULL, path_fold TEXT NOT NULL,
  kind TEXT NOT NULL, length INTEGER NOT NULL, mtime_ticks INTEGER NOT NULL, mtime_offset INTEGER NOT NULL, perms TEXT NOT NULL,
  head_hash TEXT, tail_hash TEXT, full_hash TEXT, target TEXT, unreadable_ticks INTEGER, unreadable_offset INTEGER,
  storage_kind TEXT, storage_ref TEXT, entry_name TEXT, volumes INTEGER NOT NULL DEFAULT 1, raw INTEGER NOT NULL DEFAULT 0,
  volume_sizes TEXT, unrecoverable INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (version, path)) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS entries_seq     ON entries (version, seq);
CREATE INDEX IF NOT EXISTS entries_parent  ON entries (version, parent);
CREATE INDEX IF NOT EXISTS entries_fold    ON entries (version, path_fold);
CREATE INDEX IF NOT EXISTS entries_content ON entries (full_hash, length);
CREATE INDEX IF NOT EXISTS entries_ref     ON entries (storage_ref);
CREATE INDEX IF NOT EXISTS entries_head    ON entries (length, head_hash);
CREATE INDEX IF NOT EXISTS entries_storage ON entries (version, storage_kind, storage_ref, seq);
CREATE TABLE IF NOT EXISTS dirs (version INTEGER NOT NULL, path TEXT NOT NULL, parent TEXT NOT NULL, PRIMARY KEY (version, path)) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS dirs_parent ON dirs (version, parent);
CREATE TABLE IF NOT EXISTS empty_dirs (version INTEGER NOT NULL, path TEXT NOT NULL, seq INTEGER NOT NULL, PRIMARY KEY (version, path)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS unrecoverable (version INTEGER NOT NULL, path TEXT NOT NULL, seq INTEGER NOT NULL, PRIMARY KEY (version, path)) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS import_issues (version INTEGER NOT NULL, path TEXT NOT NULL, issue TEXT NOT NULL, PRIMARY KEY (version, path, issue)) WITHOUT ROWID;
```

Notes the implementer must honour:
- `mtime` is stored as (`UtcTicks`, `Offset.TotalMinutes`) to round-trip `IndexSerializer.WriteDto` exactly; same for `unreadable_*`.
- `path_fold` = `path.ToUpperInvariant()`; `parent` = text before the last `/`, `''` at root. `dirs` gets every ancestor of every entry path and of every empty dir (walk the prefixes, `INSERT OR IGNORE`).
- `seq` is the position in the source order (the reader's order on import, the emission order at the run's finish). `empty_dirs.seq` and `unrecoverable.seq` likewise.
- A duplicate `path` inside one version cannot be stored (PK). Keep the **first**, insert `(version, path, 'duplicate')` into `import_issues`, and count it. `SerializeVersionAsync` of such a version is therefore not byte-identical with its source; only the run's own output and repair rewrites need identity, and neither can contain duplicates (the diff's `seen` set forbids it).
- `unrecoverable` flag on `entries` is maintained together with the `unrecoverable` table by import and by `ApplyPatchesAsync`.
- `FindBlobByContentAsync`: `WHERE full_hash=@f AND length=@l AND head_hash IS @h AND tail_hash IS @t AND storage_kind='blob' AND unrecoverable=0 ORDER BY version DESC LIMIT 1` (last version wins, as `LocalDedupResolver.Build` overwrites).
- `FindRefOwnerAsync`: `WHERE storage_ref=@r AND storage_kind='blob' ORDER BY unrecoverable ASC, CASE WHEN unrecoverable THEN version ELSE -version END ASC LIMIT 1` — reproduces Build's "normal rows overwrite, damaged rows `TryAdd`" precedence.
- `IsDamagedRefAsync`: `EXISTS(… WHERE storage_ref=@r AND storage_kind='blob' AND unrecoverable=1)`.
- `HeadSeenAsync`: `EXISTS(… WHERE length=@l AND head_hash=@h AND storage_kind='blob' AND unrecoverable=0)`.
- `FindPackMemberAsync`: `WHERE full_hash=@f AND length=@l AND head_hash=@h AND storage_kind='pack' AND unrecoverable=0 ORDER BY version ASC, seq ASC LIMIT 1` (first wins, as Build's `TryAdd`); returns `entry_name ?? path`.
- `ChildrenAsync(version, parent)`: `SELECT path FROM dirs WHERE version=@v AND parent=@p` (IsDir, HasChildren = exists any entry or dir under it: compute as `EXISTS(SELECT 1 FROM entries WHERE version=@v AND parent=@child) OR EXISTS(SELECT 1 FROM dirs WHERE version=@v AND parent=@child)`) plus `SELECT * FROM entries WHERE version=@v AND parent=@p ORDER BY path`. Name = last path segment.
- `RefsOnlyInAsync(versions, kind)`: `SELECT DISTINCT storage_ref FROM entries e WHERE e.version IN (…) AND e.storage_kind=@k AND NOT EXISTS (SELECT 1 FROM entries o WHERE o.storage_ref=e.storage_ref AND o.storage_kind=@k AND o.version NOT IN (…))`.
- `LivePackMembersAsync`: `SELECT storage_ref, COALESCE(entry_name, path), length, full_hash FROM entries WHERE storage_kind='pack' AND full_hash IS NOT NULL ORDER BY storage_ref, COALESCE(entry_name, path), version DESC` and yield the first row of each (ref, name) group — matches `RetentionCleaner`'s `members[entryName] = …` last-write-wins across ascending versions.
- Every reader method opens its own `SqliteCommand` on the catalog's single connection; `VersionCatalog` is not thread-safe and callers hold one per logical reader (the orchestrator opens two: one for the diff cursor, one for dedup lookups).
- All async enumerables read with `SqliteDataReader` and `await` only on `ReadAsync`; no materialization.

- [ ] **Step 1: Write the failing tests**

Cover, each as its own `[Fact]` on a temp-file catalog (`Path.Combine(Path.GetTempPath(), "asb-catalog-tests", Guid…)`), removed in `Dispose`:

1. `Import_then_serialize_is_byte_identical` — serialize `IndexStreamTests.Sample()` through `IndexSerializer`, import via `IndexStreamReader`, `SerializeVersionAsync` into a `MemoryStream`, assert equal bytes. Include an entry order that is **not** path-sorted (`["b", "a", "a/c"]`) to prove `seq` is honoured.
2. `Duplicate_path_keeps_first_and_records_issue`.
3. `Children_lists_files_and_dirs_one_level` — tree `a/b.txt, a/c/d.txt, e.txt`, empty dir `a/empty`; children of `""` = `a` (dir, has children), `e.txt`; children of `a` = `b.txt`, `c` (dir), `empty` (dir, no children).
4. `FindBlobByContent_prefers_the_latest_version_and_skips_unrecoverable` — same content in v1 (ref `data/x`) and v2 (ref `data/y`); expect `data/y`; mark v2 path unrecoverable via `ApplyPatchesAsync`; expect `data/x`.
5. `FindRefOwner_precedence` — the four combinations from the note above.
6. `FindPackMember_takes_the_first_version`.
7. `RefsOnlyIn_returns_refs_no_retained_version_uses`.
8. `RemoveVersion_drops_every_table` — then `ListVersionsAsync` empty and `entries`/`dirs`/`empty_dirs`/`unrecoverable` have no rows for it.
9. `ApplyPatches_updates_flag_table_and_serialization` — patch a path unrecoverable, serialize, read with `IndexStreamReader`, `ReadUnrecoverable()` contains it; patch it back, gone.
10. `CaseCollisions_finds_paths_differing_only_in_case`.
11. `Stats_counts_files_and_bytes` — matches `entries.Count` and `Sum(Length)`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd backend && dotnet test --filter FullyQualifiedName~VersionCatalogTests`
Expected: build error, types not found.

- [ ] **Step 3: Implement `CatalogSql` and `VersionCatalog`**

Connection string:

```csharp
public static string ConnectionString(string path, bool readOnly) => new SqliteConnectionStringBuilder
{
    DataSource = path,
    Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
    Pooling = false,
    Cache = SqliteCacheMode.Private,
}.ToString();
```

Import, the shape every writer in this task follows:

```csharp
public async Task ImportVersionAsync(int version, long identity, IndexStreamReader reader, CancellationToken ct)
{
    await using var tx = (SqliteTransaction)await _conn.BeginTransactionAsync(ct);
    await DeleteVersionRowsAsync(version, tx, ct);
    using var insert = InsertEntryCommand(tx);           // parameters bound once, reused per row
    using var dir = InsertDirCommand(tx);
    var seq = 0; var kept = 0;
    foreach (var e in reader.Entries())
    {
        ct.ThrowIfCancellationRequested();
        if (!await TryInsertEntryAsync(insert, version, seq++, e, unrecoverable: false, ct))
        {
            await RecordIssueAsync(tx, version, e.Path, "duplicate", ct);
            continue;
        }
        kept++;
        await InsertAncestorsAsync(dir, version, e.Path, ct);
    }
    var emptyDirs = reader.ReadEmptyDirs();
    for (var i = 0; i < emptyDirs.Count; i++) { await InsertEmptyDirAsync(tx, version, emptyDirs[i], i, ct); await InsertAncestorsAsync(dir, version, emptyDirs[i] + "/x", ct); }
    var unrecoverable = reader.ReadUnrecoverable();
    for (var i = 0; i < unrecoverable.Count; i++) await MarkUnrecoverableAsync(tx, version, unrecoverable[i], i, ct);
    await UpsertVersionAsync(tx, version, identity, kept, ct);
    await tx.CommitAsync(ct);
}
```

`TryInsertEntryAsync` uses `INSERT OR IGNORE` and returns `rowsAffected == 1`. `InsertAncestorsAsync(dir, version, "a/b/c.txt")` inserts `("a", "")` and `("a/b", "a")` with `INSERT OR IGNORE`; passing `emptyDir + "/x"` makes the empty dir itself an ancestor and so a `dirs` row.

Serialize:

```csharp
public async Task SerializeVersionAsync(int version, Stream output, IReadOnlyList<CatalogPatch>? patches, CancellationToken ct)
{
    var byPath = patches?.Where(p => p.Version == version).ToDictionary(p => p.Path, StringComparer.Ordinal);
    var count = await ScalarAsync<long>("SELECT entry_count FROM versions WHERE version=@v", ("@v", version), ct)
        ?? throw new InvalidOperationException($"Version {version} is not in the catalog.");
    using var w = new IndexStreamWriter(output);
    w.WriteHeader(version, (int)count);
    await foreach (var e in QueryEntriesAsync("SELECT … FROM entries WHERE version=@v ORDER BY seq", version, ct))
        w.WriteEntry(byPath is not null && byPath.TryGetValue(e.Path, out var p) ? Apply(e, p) : e);
    w.WriteEmptyDirs(await EmptyDirsAsync(version, ct));
    var unrecoverable = (await UnrecoverableAsync(version, ct)).ToList();
    if (byPath is not null)
        foreach (var p in byPath.Values)
            if (p.Unrecoverable == true && !unrecoverable.Contains(p.Path)) unrecoverable.Add(p.Path);
            else if (p.Unrecoverable == false) unrecoverable.Remove(p.Path);
    w.WriteUnrecoverable(unrecoverable);
}
```

`Apply(e, p)` returns `e with { UnreadableAt = p.UnreadableAt ?? e.UnreadableAt, Storage = p.Storage ?? e.Storage }`. Appending newly unrecoverable paths at the end of the list reproduces `List.Add` order in `BackupChecker.cs:284` and `BackupRepairer.cs:786`.

- [ ] **Step 4: Run the tests**

Run: `cd backend && dotnet test --filter FullyQualifiedName~VersionCatalogTests`
Expected: PASS (11 tests).

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/CatalogSql.cs backend/src/AzureStorageBackup.Api/Services/VersionCatalog.cs backend/tests/AzureStorageBackup.Api.Tests/VersionCatalogTests.cs
git commit -m "feat(catalog): a per-container SQLite catalog of every retained version's entries"
```

### Task 3: `VersionCatalogStore` — opening, locking, corruption recovery

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/VersionCatalogStore.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/VersionCatalogStoreTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed class VersionCatalogStore(string rootDir, ILogger<VersionCatalogStore>? logger = null)
  {
      public string PathFor(int accountId, string container);                     // {root}/{accountId}/{Safe(container)}/catalog.db — Safe() copied from VersionIndexFileStore.cs
      public Task<VersionCatalog> OpenAsync(int accountId, string container, bool readOnly, CancellationToken ct);
      public Task<IDisposable> LockForWriteAsync(int accountId, string container, CancellationToken ct);   // per-container SemaphoreSlim(1,1)
      public void RemoveContainer(int accountId, string container);              // deletes catalog.db, -wal, -shm and the directory if empty
  }
  ```

- [ ] **Step 1: Write the failing tests**

1. `Open_creates_the_file_and_schema` — open read-write, `ListVersionsAsync` returns empty, file exists.
2. `Open_readonly_on_a_missing_file_throws_FileNotFound` (do not create files from a reader).
3. `Corrupt_file_is_replaced_and_logged` — write 4096 random bytes to the path, `OpenAsync` read-write succeeds, the file is now a valid empty catalog, logger received one `Warning`. Use `NSubstitute` for `ILogger` or a `TestLogger` that records entries (check the test project for an existing one first: `grep -rl "class TestLogger" backend/tests`).
4. `LockForWrite_serializes` — take the lock, try to take it again with a 100 ms timeout token, expect `OperationCanceledException`; dispose, second take succeeds.
5. `RemoveContainer_deletes_wal_and_shm`.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd backend && dotnet test --filter FullyQualifiedName~VersionCatalogStoreTests`
Expected: build error.

- [ ] **Step 3: Implement**

```csharp
public async Task<VersionCatalog> OpenAsync(int accountId, string container, bool readOnly, CancellationToken ct)
{
    var path = PathFor(accountId, container);
    if (!readOnly) Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
    try
    {
        return await VersionCatalog.OpenAsync(path, readOnly, ct);
    }
    catch (SqliteException ex) when (!readOnly && ex.SqliteErrorCode is 11 /* SQLITE_CORRUPT */ or 26 /* SQLITE_NOTADB */)
    {
        logger?.LogWarning(ex, "Catalog {Path} is unreadable; deleting it. It is a cache and will be rebuilt from the cloud on demand.", path);
        RemoveContainer(accountId, container);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        return await VersionCatalog.OpenAsync(path, readOnly: false, ct);
    }
}
```

`VersionCatalog.OpenAsync(path, readOnly, ct)` (add to Task 2's class as a static factory) opens the connection, applies pragmas, runs `PRAGMA quick_check` when read-write and throws `SqliteException(SQLITE_CORRUPT)` if it does not answer `ok`, then `EnsureSchema`. Locks: `ConcurrentDictionary<string, SemaphoreSlim>` keyed by `PathFor`.

- [ ] **Step 4: Run the tests**

Run: `cd backend && dotnet test --filter FullyQualifiedName~VersionCatalogStoreTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/VersionCatalogStore.cs backend/src/AzureStorageBackup.Api/Services/VersionCatalog.cs backend/tests/AzureStorageBackup.Api.Tests/VersionCatalogStoreTests.cs
git commit -m "feat(catalog): open catalogs per container, one writer at a time, and replace a corrupt file"
```

### Task 4: File-based codec so an index never has to be a byte array

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/IArchiveCodec.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/SevenZipArchiveCodec.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/SevenZipArchiveCodecFileTests.cs`

**Interfaces:**
- Produces, added to `IArchiveCodec`:
  ```csharp
  Task EncodeFileAsync(string inputPath, string archivePath, string? password, CancellationToken ct = default);
  Task DecodeFileAsync(string archivePath, string outputPath, string? password, CancellationToken ct = default);
  ```
  Both keep the `content` entry name so an archive written by `EncodeAsync` decodes with `DecodeFileAsync` and vice versa.

- [ ] **Step 1: Write the failing tests** (gated on `SevenZip()`)

1. `EncodeFile_then_DecodeAsync_round_trips` — write 3 MB of random bytes to a file, `EncodeFileAsync`, read the archive bytes, `DecodeAsync` → equal.
2. `EncodeAsync_then_DecodeFile_round_trips` with a password.
3. `EncodeFile_missing_payload_throws_ArchiveMembersMissingException` — copy the existing `EncodeAsync` exit-code-1 test's approach if one exists in `SevenZipArchiveCodecTests.cs`; otherwise skip this case and note it.

- [ ] **Step 2: Run to verify they fail**

Run: `cd backend && dotnet test --filter FullyQualifiedName~SevenZipArchiveCodecFileTests`
Expected: build error.

- [ ] **Step 3: Implement**

`EncodeFileAsync`: same as `EncodeAsync` but the input is a **hard link or copy** of `inputPath` named `content` inside the work dir (7z uses the file name as the entry name; try `File.CreateSymbolicLink`? No — 7z would store the link. Use `File.Copy` when `new FileInfo(inputPath).Length < 64 MB`, else run 7z with `-si` is not an option either because the byte-array path never used it; so always copy). Then move `out.7z` to `archivePath` with `File.Move(…, overwrite: true)`. `DecodeFileAsync`: extract as today, then `File.Move(Path.Combine(outDir, "content"), outputPath, overwrite: true)`. Any `IArchiveCodec` test doubles in the test project (`grep -rl "IArchiveCodec" backend/tests`) get the two new members implemented by writing/reading files through their existing byte methods.

- [ ] **Step 4: Run the tests**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~SevenZipArchiveCodec"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/IArchiveCodec.cs backend/src/AzureStorageBackup.Api/Services/SevenZipArchiveCodec.cs backend/tests/AzureStorageBackup.Api.Tests/SevenZipArchiveCodecFileTests.cs backend/tests
git commit -m "feat(codec): encode and decode an index through files, not byte arrays"
```

### Task 5: `IBackupInfoStore` reads and writes an index as a file

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/IBackupInfoStore.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupInfoStore.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupInfoStoreFileIndexTests.cs`

**Interfaces:**
- Produces, added to `IBackupInfoStore` (the old `ReadIndexAsync`/`WriteIndexAsync` stay until Task 22):
  ```csharp
  /// serializedPath: a file in IndexStreamWriter format. Returns the blob name and volume count exactly as WriteIndexAsync does.
  Task<(string Name, int Volumes)> WriteIndexFileAsync(Account account, string container, int version, string serializedPath,
      string? password, AccessTier? tier = null, CancellationToken ct = default, StageTracker? progress = null);
  /// Downloads (and concatenates) the volumes, decodes them, and leaves the serialized index at destPath.
  Task ReadIndexToFileAsync(Account account, string container, string indexBlob, string? password, int volumes, string destPath,
      CancellationToken ct = default);
  ```

- [ ] **Step 1: Write the failing tests** (Azurite + 7z)

1. `WriteIndexFile_single_blob_matches_WriteIndexAsync` — serialize `IndexStreamTests.Sample()` to a temp file with `IndexStreamWriter`, `WriteIndexFileAsync`, then `ReadIndexAsync` (old API) returns the same entries.
2. `WriteIndexFile_splits_into_volumes_past_the_threshold` — construct `BackupInfoStore` with `IndexVolumeBytes = 4096`, an index of 2 000 entries, expect `Volumes > 1` and `ReadIndexToFileAsync` + `IndexStreamReader` reads back 2 000 entries.
3. `ReadIndexToFile_decodes_an_encrypted_index`.

Build the store the way `BackupInfoStoreTests.cs` does (look at its `Build()`; reuse).

- [ ] **Step 2: Run to verify they fail**

Run: `cd backend && dotnet test --filter FullyQualifiedName~BackupInfoStoreFileIndexTests`
Expected: build error.

- [ ] **Step 3: Implement**

`WriteIndexFileAsync`: `codec.EncodeFileAsync(serializedPath, encodedPath, password)` into a temp dir under the store's temp root (add a `tempRoot` constructor parameter defaulting to `Path.Combine(Path.GetTempPath(), "asb-index")`; `Program.cs` passes `Path.Combine(tempPath, "index")` in Task 14). Then the same branching as `WriteIndexAsync:117-152` but reading ranges of the encoded file: single-blob path uploads `File.OpenRead(encodedPath)` through `WriteAtomicAsync` (add an overload taking a `Func<Stream>` producer; the existing byte[] overload calls it with `() => new MemoryStream(bytes)`), volume path uploads `new SubStream(file, offset, length)` per volume (write a tiny `SubStream : Stream` in `VolumeBlobIO.cs` if none exists — check `grep -n "class .*SubStream\|RangeStream" Services/*.cs`). Verification after the volume path: `ReadIndexToFileAsync` into a temp file and compare `IndexStreamReader.EntryCount` and `Version` with the source file's header (replaces `VerifyRoundTrip`).

`ReadIndexToFileAsync`: download each volume with `blob.DownloadToAsync(fileStream)` appending into one `encoded.7z` temp file, then `codec.DecodeFileAsync(encoded, destPath, password)`.

- [ ] **Step 4: Run the tests**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~BackupInfoStore"`
Expected: PASS, old tests included.

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/IBackupInfoStore.cs backend/src/AzureStorageBackup.Api/Services/BackupInfoStore.cs backend/src/AzureStorageBackup.Api/Services/VolumeBlobIO.cs backend/tests/AzureStorageBackup.Api.Tests/BackupInfoStoreFileIndexTests.cs
git commit -m "feat(store): upload and download a version index as a file, volumes included"
```

### Task 6: `IVersionCatalogs` — the lazy migration chain

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/IVersionCatalogs.cs`
- Create: `backend/src/AzureStorageBackup.Api/Services/VersionCatalogs.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/VersionIndexFileStore.cs` (add `OpenBodyAsync`)
- Test: `backend/tests/AzureStorageBackup.Api.Tests/VersionCatalogsMigrationTests.cs`
- Test helper: `backend/tests/AzureStorageBackup.Api.Tests/TestCatalogs.cs`

**Interfaces:**
- Consumes: `VersionCatalogStore` (Task 3), `IBackupInfoStore.ReadIndexToFileAsync` (Task 5), `VersionIndexFileStore`, `AppDbContext.CachedVersionIndexes`.
- Produces:
  ```csharp
  public interface IVersionCatalogs
  {
      Task<VersionCatalog> OpenAsync(int accountId, string container, bool readOnly, CancellationToken ct = default);
      /// Guarantees `version` is in the catalog: catalog → {version}.idx → legacy row → cloud. Idempotent.
      Task EnsureVersionAsync(Account account, string container, BackupVersion version, long identityTicks, string? password, CancellationToken ct = default);
      Task RemoveVersionAsync(int accountId, string container, int version, CancellationToken ct = default);
      Task RemoveContainerAsync(int accountId, string container, CancellationToken ct = default);
      Task<IDisposable> LockForWriteAsync(int accountId, string container, CancellationToken ct = default);
  }
  ```
  and `VersionIndexFileStore.OpenBodyAsync(int accountId, string container, int version, long identityTicks, CancellationToken ct) : Task<Stream?>` — validates the 24-byte header exactly as `ReadAsync` does and returns a stream positioned at the body, or null.

  Test helper:
  ```csharp
  internal static class TestCatalogs
  {
      internal static VersionCatalogStore NewStore();   // temp root, removed at ProcessExit, same shape as TestIndexFiles
      internal static VersionCatalogs New(AppDbContext db, IBackupInfoStore store, VersionIndexFileStore? legacyFiles = null);
  }
  ```

- [ ] **Step 1: Write the failing tests**

All use a `TestLocalAuthority`-style in-memory `AppDbContext`, an `IBackupInfoStore` substitute (NSubstitute) and `TestCatalogs.New`.

1. `Ensure_uses_the_catalog_when_present_and_touches_nothing_else` — import v1 directly, call `EnsureVersionAsync`, assert `store.DidNotReceive().ReadIndexToFileAsync(...)`.
2. `Ensure_imports_a_matching_idx_file_and_deletes_it` — write `1.idx` via `VersionIndexFileStore.WriteAsync` with `IndexSerializer.SerializeIndex(sample)`, ensure, assert catalog has 4 entries and the file is gone.
3. `Ensure_ignores_an_idx_file_with_the_wrong_identity_and_falls_through` — file written with identity 1, ensure with identity 2, cloud substitute returns the sample; the file is left alone (today's `ReadAsync` returns null on mismatch and the file is later overwritten; keep that: delete only on a successful import **from that file**).
4. `Ensure_imports_a_legacy_row_and_drops_it` — insert a `CachedVersionIndex` row, ensure, row gone.
5. `Ensure_downloads_from_the_cloud_when_nothing_local_exists` — substitute `ReadIndexToFileAsync` writes the serialized sample to `destPath`.
6. `Ensure_on_a_failed_cloud_read_leaves_no_version_row` — substitute throws; `GetVersionAsync` is null afterwards.
7. `RemoveContainer_removes_catalog_and_legacy_rows`.

- [ ] **Step 2: Run to verify they fail**

Run: `cd backend && dotnet test --filter FullyQualifiedName~VersionCatalogsMigrationTests`
Expected: build error.

- [ ] **Step 3: Implement**

```csharp
public sealed class VersionCatalogs(
    VersionCatalogStore catalogs, VersionIndexFileStore legacyFiles, AppDbContext db, IBackupInfoStore store,
    ILogger<VersionCatalogs>? logger = null) : IVersionCatalogs
{
    public async Task EnsureVersionAsync(Account account, string container, BackupVersion version, long identity, string? password, CancellationToken ct = default)
    {
        await using (var probe = await catalogs.OpenAsync(account.Id, container, readOnly: false, ct))
            if (await probe.GetVersionAsync(version.Version, ct) is { } row && row.Identity == identity)
                return;

        using var _ = await catalogs.LockForWriteAsync(account.Id, container, ct);
        await using var catalog = await catalogs.OpenAsync(account.Id, container, readOnly: false, ct);
        if (await catalog.GetVersionAsync(version.Version, ct) is { } again && again.Identity == identity)
            return;   // another caller got here first

        // 1. the .idx file
        if (await legacyFiles.OpenBodyAsync(account.Id, container, version.Version, identity, ct) is { } body)
        {
            await using (body)
            using (var reader = new IndexStreamReader(body))
                await catalog.ImportVersionAsync(version.Version, identity, reader, ct);
            legacyFiles.Remove(account.Id, container, version.Version);
            return;
        }
        // 2. the legacy row
        var legacy = await db.CachedVersionIndexes.AsNoTracking().FirstOrDefaultAsync(
            x => x.AccountId == account.Id && x.Container == container && x.Version == version.Version, ct);
        if (legacy is not null)
        {
            if (legacy.IdentityTicks == identity)
            {
                using var reader = new IndexStreamReader(new MemoryStream(legacy.Bytes));
                await catalog.ImportVersionAsync(version.Version, identity, reader, ct);
            }
            await db.CachedVersionIndexes.Where(x => x.Id == legacy.Id).ExecuteDeleteAsync(ct);
            if (legacy.IdentityTicks == identity) return;
        }
        // 3. the cloud
        var temp = Path.Combine(Path.GetTempPath(), "asb-index", Guid.NewGuid().ToString("N") + ".idx");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            await store.ReadIndexToFileAsync(account, container, version.IndexBlob, password, version.IndexVolumes, temp, ct);
            await using var file = File.OpenRead(temp);
            using var reader = new IndexStreamReader(file);
            await catalog.ImportVersionAsync(version.Version, identity, reader, ct);
        }
        finally { try { File.Delete(temp); } catch { /* temp */ } }
    }
}
```

A `.idx` body that fails to parse (truncated file) throws out of `ImportVersionAsync`; the transaction rolls back and the file stays. Catch `EndOfStreamException`/`IOException` from that branch, log a warning, delete the file and fall through to the next source — matching today's `Rebuild(bytes) is null` behaviour.

- [ ] **Step 4: Run the tests**

Run: `cd backend && dotnet test --filter FullyQualifiedName~VersionCatalogsMigrationTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/IVersionCatalogs.cs backend/src/AzureStorageBackup.Api/Services/VersionCatalogs.cs backend/src/AzureStorageBackup.Api/Services/VersionIndexFileStore.cs backend/tests/AzureStorageBackup.Api.Tests/VersionCatalogsMigrationTests.cs backend/tests/AzureStorageBackup.Api.Tests/TestCatalogs.cs
git commit -m "feat(catalog): migrate a version lazily from the .idx file, the legacy row, or the cloud"
```

### Task 7: `RunWorkDb` — the per-run scratch database

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/RunWorkDb.cs`
- Create: `backend/src/AzureStorageBackup.Api/Services/RunWorkDbFactory.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/RunWorkDbTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record ScanRow(string Path, EntryKind Kind, long Length, DateTimeOffset ModifiedAt, string Permissions, string? Target, FileCategory Category, string? GroupKey);
  public enum DraftState { Pending = 0, Confirmed = 1, Unreadable = 2, Dropped = 3 }
  public sealed record DraftRow(int Seq, string Path, DraftState State, IndexEntry Entry);
  public sealed record ReservationRow(string Ref, bool Raw, int Volumes, IReadOnlyList<long> VolumeSizes);

  public sealed class RunWorkDb : IAsyncDisposable
  {
      public string Path { get; }
      // writes: enqueued to one writer task, committed in batches of 2 000 rows or 200 ms, whichever first
      public ValueTask InsertScanAsync(ScanRow row, CancellationToken ct);
      public ValueTask InsertDraftAsync(int seq, string path, DraftState state, IndexEntry entry, CancellationToken ct);
      public ValueTask UpdateDraftStorageAsync(string path, StorageRef storage, CancellationToken ct);
      public ValueTask UpdateDraftTailAsync(string path, string tailHash, CancellationToken ct);
      public ValueTask UpdateDraftOverrideAsync(string path, string fullHash, string? headHash, long length, DateTimeOffset mtime, CancellationToken ct);
      public ValueTask MarkDraftUnreadableAsync(string path, string reason, CancellationToken ct);
      public ValueTask InsertReservationAsync(string contentKey, ReservationRow row, CancellationToken ct);
      public ValueTask InsertReservedHeadAsync(string headKey, CancellationToken ct);
      public ValueTask InsertResumeRecordAsync(JournalRecord record, CancellationToken ct);
      public Task FlushAsync(CancellationToken ct);                       // waits until every enqueued write is committed
      // reads (each opens its own connection; safe to interleave with writes)
      public IAsyncEnumerable<ScanRow> ScanOrderedAsync(CancellationToken ct);
      public Task<long> ScanCountAsync(CancellationToken ct);
      public IAsyncEnumerable<(string Dir, int Count)> DirectoryCandidatesAsync(CancellationToken ct);   // GROUP BY group_key WHERE category=DirectoryGroup
      public Task<FileCategory?> CategoryAsync(string path, CancellationToken ct);
      public IAsyncEnumerable<DraftRow> DraftOrderedBySeqAsync(CancellationToken ct);
      public Task<DraftRow?> DraftAsync(string path, CancellationToken ct);
      public Task<(long Files, long Bytes, long Unreadable)> DraftStatsAsync(CancellationToken ct);   // Files/Bytes over Confirmed rows with an entry; Unreadable counts state=Unreadable
      public IAsyncEnumerable<string> DraftUnreadableUnderAsync(string dir, CancellationToken ct);
      public Task<ReservationRow?> ReservationAsync(string contentKey, CancellationToken ct);
      public Task<bool> ReservedHeadAsync(string headKey, CancellationToken ct);
      public Task<JournalRecord?> ResumeBlobByPathAsync(string path, CancellationToken ct);
      public Task<JournalRecord?> ResumeBlobByContentAsync(string fullHash, long length, string headHash, string tailHash, CancellationToken ct);
      public Task<JournalRecord?> ResumePackAsync(string membersKey, CancellationToken ct);
      public Task<int> ResumeRecordCountAsync(CancellationToken ct);
  }

  public sealed class RunWorkDbFactory(string rootDir)
  {
      public string RootDir => rootDir;
      public Task<RunWorkDb> CreateAsync(string runId, CancellationToken ct);   // {root}/{runId}.db, fresh
      public static void ClearStale(string rootDir);                            // delete every *.db* under root; called at startup like DiffWorkQueue.ClearStale
  }
  ```

The `draft` table's columns mirror `entries` (Task 2) minus `version`, plus `state INTEGER` and `reason TEXT`. `resume_blobs` keys on `ref` and adds `path`, `members_key` is `JournalResume.MemberKey`'s string (copy the function verbatim as `RunWorkDb.MemberKey`). Journal records with `Kind == "blob"` go to `resume_blobs` with `INSERT OR IGNORE` **by path** (a second record for the same path is ignored, as `BuildBlobs` does with `TryAdd`), packs to `resume_packs` keyed by `members_key` with `INSERT OR IGNORE`, and `resume_pack_members` rows for each member.

Pragmas for `work.db`: `journal_mode=WAL`, `synchronous=OFF`, `cache_size=-65536`, `temp_store=MEMORY`.

Writer task: a `Channel<Func<SqliteCommand cache, Task>>` unbounded; the consumer opens one connection, begins a transaction, executes items, commits when 2 000 items or 200 ms have elapsed. `FlushAsync` enqueues a `TaskCompletionSource` marker and awaits it after the commit. Faults in the writer are stored and rethrown by the next `FlushAsync`/`DisposeAsync` so a run cannot silently lose rows. `DisposeAsync` completes the channel, awaits the writer, closes readers, deletes `Path`, `Path-wal`, `Path-shm`.

- [ ] **Step 1: Write the failing tests**

1. `Scan_rows_come_back_in_ordinal_path_order` — insert `b`, `a/x`, `a-x`, `A`; `ScanOrderedAsync` yields `A, a-x, a/x, b` (ordinal: `-` 0x2D < `/` 0x2F, uppercase first). SQLite's default `BINARY` collation on UTF-8 text is byte order, which equals `string.CompareOrdinal` for valid UTF-16 only when no surrogate pairs are involved; add a case with `"z\u{1F600}"` vs `"z￿"` and assert the SQL order equals `StringComparer.Ordinal` — if it does not, the implementer must add a `path_key BLOB` column holding UTF-16BE bytes and order by that. Decide from the test's outcome and record the result in the file's header comment.
2. `Draft_updates_apply_in_enqueue_order` — insert draft `p`, update storage, update tail, `FlushAsync`, `DraftAsync("p")` shows both.
3. `Flush_surfaces_a_writer_fault` — enqueue an update for a path whose row does not exist is fine (0 rows); instead enqueue an insert with a `null` required column via a test hook and assert `FlushAsync` throws `SqliteException`.
4. `DirectoryCandidates_counts_per_group_key`.
5. `Resume_blob_by_path_keeps_the_first_record`.
6. `Resume_pack_matches_by_members_key`.
7. `Dispose_deletes_the_files`.
8. `Reads_see_committed_writes_while_the_writer_is_open` — insert 5 000 scan rows, flush, count from a reader while the writer is still alive.

- [ ] **Step 2: Run to verify they fail**

Run: `cd backend && dotnet test --filter FullyQualifiedName~RunWorkDbTests`
Expected: build error.

- [ ] **Step 3: Implement** per the interface block. Keep every SQL string as a `const` at the top of the file. Reuse `IndexEntry` ↔ row mapping code from `VersionCatalog` by extracting `internal static class EntryRowMapper` (in `CatalogSql.cs`) with `Bind(SqliteCommand, IndexEntry)` and `Read(SqliteDataReader) : IndexEntry`; both databases use the same column names for entry fields.

- [ ] **Step 4: Run the tests**

Run: `cd backend && dotnet test --filter FullyQualifiedName~RunWorkDbTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/RunWorkDb.cs backend/src/AzureStorageBackup.Api/Services/RunWorkDbFactory.cs backend/src/AzureStorageBackup.Api/Services/CatalogSql.cs backend/tests/AzureStorageBackup.Api.Tests/RunWorkDbTests.cs
git commit -m "feat(run): a per-run SQLite scratch database for the scan, the draft index and resume records"
```

---

## Phase B: the backup pipeline

### Task 8: The scanner writes rows instead of building a list

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/ScanSink.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/LocalFileScanner.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/GroupingPlanner.cs` (add `ClassifyOne`)
- Test: `backend/tests/AzureStorageBackup.Api.Tests/LocalFileScannerSinkTests.cs`; existing `LocalFileScannerTests.cs` adapted

**Interfaces:**
- Produces:
  ```csharp
  public interface IScanSink
  {
      ValueTask AddAsync(ScannedEntry entry, CancellationToken ct);
  }
  public sealed class ListScanSink : IScanSink { public List<ScannedEntry> Entries { get; } = []; … }
  public sealed class WorkDbScanSink(RunWorkDb work, PlanOptions plan) : IScanSink   // computes FileCategory/GroupKey via GroupingPlanner.ClassifyOne and inserts a ScanRow
  public sealed record ScanSummary(long Entries, IReadOnlyList<string> EmptyDirs, IReadOnlyList<UnreadablePath> Unreadable);

  // LocalFileScanner
  public async Task<ScanSummary> ScanAsync(string rootPath, IgnoreRuleSet ignore, IScanSink sink, ScanOptions? options = null,
      CancellationToken ct = default, StageTracker? tracker = null);
  // GroupingPlanner
  public static FileClass ClassifyOne(string path, long length, PlanOptions options);   // the body of Classify's loop for one entry
  ```
  `ScanResult` is deleted; `ScanSummary` keeps `EmptyDirs` and `Unreadable` as lists (bounded by directory count and by failures, not by file count — accepted in the spec's non-goals).

- [ ] **Step 1: Write the failing tests**

1. `Scanner_feeds_entries_to_the_sink_in_directory_walk_order_and_the_sink_sorts` — with `ListScanSink`, entries are what `ScanAsync` used to return **before** its sort (the old code sorted after the walk). Assert the set equals the old result; order is the sink's concern now.
2. `WorkDbScanSink_writes_category_and_group_key` — two small files in `d/`, one 6 MB file; `ScanOrderedAsync` rows carry `DirectoryGroup/"d"` and `SingleFile/null`; `DirectoryCandidatesAsync` yields `("d", 2)`.
3. Adapt `LocalFileScannerTests.cs`: replace `var result = await scanner.ScanAsync(root, ignore, opts)` with a `ListScanSink` and `result.Entries` with `sink.Entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList()`; `result.EmptyDirs`/`result.Unreadable` come from the returned `ScanSummary`.

- [ ] **Step 2: Run to verify they fail**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~LocalFileScanner"`
Expected: build errors.

- [ ] **Step 3: Implement**

In `ScanDirectory`, replace `entries.Add(x)` with `await sink.AddAsync(x, ct)` (the method becomes `async Task<bool>`; the recursive call is awaited; `_ = ScanDirectory(...)` at line 67 becomes `await ScanDirectory(...)`). Keep `emptyDirs` and `unreadable` lists; sort them as today; return `new ScanSummary(count, emptyDirs, unreadable)` where `count` is incremented per `AddAsync`. `GroupingPlanner.Classify` becomes a two-line wrapper over `ClassifyOne` so `Classify`'s existing tests stay green.

- [ ] **Step 4: Run the tests**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~LocalFileScanner|FullyQualifiedName~GroupingPlanner"`
Expected: PASS. The orchestrator does not compile yet if it still calls the old signature — it does; Tasks 8–12 are built with `dotnet build` errors in `BackupOrchestrator.cs` tolerated only if you stub the call. Do not stub: instead keep the old `ScanAsync(string, IgnoreRuleSet, ScanOptions?, CancellationToken, StageTracker?)` overload for now, implemented as `ListScanSink` + sort, and delete it in Task 13.

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/ScanSink.cs backend/src/AzureStorageBackup.Api/Services/LocalFileScanner.cs backend/src/AzureStorageBackup.Api/Services/GroupingPlanner.cs backend/tests/AzureStorageBackup.Api.Tests
git commit -m "feat(scan): the scanner hands each entry to a sink instead of collecting a list"
```

### Task 9: The diff merges two ordered cursors

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupDiffer.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/LegacyBackupDiffer.cs` (verbatim copy of today's class, renamed, `internal`)
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupDifferMergeTests.cs`; existing `BackupDifferTests.cs` and `BackupDifferUnreadableTests.cs` adapted

**Interfaces:**
- Produces:
  ```csharp
  public sealed record DiffTotals(int ChangedFiles, long ChangedBytes, int Emitted);
  public async Task<DiffTotals> DiffAsync(
      string rootPath,
      IAsyncEnumerable<ScannedEntry> current,          // ordinal path order
      IAsyncEnumerable<IndexEntry>? previous,          // ordinal path order, or null on a first run
      IReadOnlyList<UnreadablePath> unreadable,        // from ScanSummary
      Func<string, CancellationToken, IAsyncEnumerable<IndexEntry>> previousUnder,   // entries of the previous version under a directory, in seq order; "" or "." = all
      DiffOptions? options, CancellationToken ct, StageTracker? tracker,
      Func<FileChange, CancellationToken, Task> onChange,       // now required: every change is delivered here, in the old list order
      Func<string, bool>? fullHashDeferred);
  ```
  `DiffResult` is deleted. `FileChange` is unchanged.

Emission order must equal the old `changes` list order: (1) every scanned entry in scan order, each compared against the previous entry with the same path; (2) for each `unreadable` in list order, the previous entries under it (via `previousUnder`) not yet seen, then the file itself when it is not a directory and not in previous; (3) every previous entry never seen, as `Deleted`, in previous order. Step 3 needs "never seen" without a `seen` set the size of the file count: the merge already knows, per previous entry, whether the scan had it (same path → seen). Previous entries consumed in step 1 are seen; the ones the merge passed over (previous path < current path) are candidates for `Deleted`, **but** they may still be claimed by step 2 (a previous entry under an unreadable directory becomes `Unreadable`, not `Deleted`). So the merge records passed-over previous paths in a `RunWorkDb`-free way: it emits them into a small in-memory list only when `unreadable.Count > 0` **and** the path is under some unreadable directory (`IsUnder` check against the unreadable list, which is short); everything else passed over is emitted as `Deleted` immediately in step 1 — that changes the *position* of `Deleted` changes relative to the old order. Since `Deleted` changes produce no index entry (`BuildEntries` skips them) and only feed counters, their position does not affect the serialized index; assert that in the equivalence test by comparing the change sequences **with Deleted entries removed** and the multiset of Deleted paths separately.

- [ ] **Step 1: Write the failing equivalence test**

```csharp
public class BackupDifferMergeTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(11)] [InlineData(29)]
    public async Task Merge_diff_matches_the_legacy_diff(int seed)
    {
        var rng = new Random(seed);
        using var tree = new TempTree();                       // helper: creates a root, writes files, tracks paths
        var previous = GenerateIndex(rng, tree, entries: 60);   // writes some of the files to disk with matching/mismatching content
        var scan = tree.Scan();                                 // ScannedEntry list, ordinal-sorted
        var unreadable = PickUnreadable(rng, tree);             // 0–3 UnreadablePath, some directories

        var hasher = new FileHasher();
        var legacy = await new LegacyBackupDiffer(hasher).DiffAsync(tree.Root, new ScanResult(scan, [], unreadable), previous);
        var merged = new List<FileChange>();
        await new BackupDiffer(hasher).DiffAsync(tree.Root, scan.ToAsyncEnumerable(), previous.Entries.ToAsyncEnumerable(), unreadable,
            (dir, _) => PreviousUnder(previous, dir).ToAsyncEnumerable(), null, CancellationToken.None, null,
            (c, _) => { merged.Add(c); return Task.CompletedTask; }, null);

        Assert.Equal(legacy.Changes.Where(c => c.Kind != ChangeKind.Deleted).Select(Key), merged.Where(c => c.Kind != ChangeKind.Deleted).Select(Key));
        Assert.Equal(legacy.Changes.Where(c => c.Kind == ChangeKind.Deleted).Select(c => c.Path).Order(), merged.Where(c => c.Kind == ChangeKind.Deleted).Select(c => c.Path).Order());
    }
    private static string Key(FileChange c) => $"{c.Path}|{c.Kind}|{c.HeadHash}|{c.FullHash}|{c.TailHash}|{c.CarriedStorage?.Ref}|{c.UnreadableReason}";
}
```

`PreviousUnder` copies `LegacyBackupDiffer.PreviousEntriesUnder`'s semantics over `previous.Entries` in list order. `ToAsyncEnumerable` is from `System.Linq.Async`? Not referenced — write a 5-line `internal static class AsyncEnumerableTestExtensions` in the test project instead of adding a package.

- [ ] **Step 2: Run to verify it fails**

Run: `cd backend && dotnet test --filter FullyQualifiedName~BackupDifferMergeTests`
Expected: build error (new signature missing).

- [ ] **Step 3: Implement the merge**

```csharp
await using var cur = current.GetAsyncEnumerator(ct);
await using var prev = previous?.GetAsyncEnumerator(ct);
var haveCur = await cur.MoveNextAsync();
var havePrev = prev is not null && await prev.MoveNextAsync();
var pendingUnreadablePrev = new List<IndexEntry>();   // previous entries passed over that sit under an unreadable directory
var unreadableDirs = unreadable.Where(u => u.IsDirectory).Select(u => u.Path).ToList();
var seenUnreadableFiles = new HashSet<string>(StringComparer.Ordinal);   // bounded by unreadable.Count

while (haveCur)
{
    ct.ThrowIfCancellationRequested();
    var entry = cur.Current;
    IndexEntry? match = null;
    while (havePrev && string.CompareOrdinal(prev!.Current.Path, entry.Path) < 0)
    {
        await PassedOverAsync(prev.Current);            // Deleted, or parked for step 2
        havePrev = await prev.MoveNextAsync();
    }
    if (havePrev && prev!.Current.Path == entry.Path) { match = prev.Current; havePrev = await prev.MoveNextAsync(); }
    var change = match is null ? await AddedAsync(entry, …) : await CompareAsync(entry, match, …);
    await Emit(change);
    haveCur = await cur.MoveNextAsync();
}
while (havePrev) { await PassedOverAsync(prev!.Current); havePrev = await prev.MoveNextAsync(); }

foreach (var u in unreadable)
{
    if (u.IsDirectory)
        foreach (var p in pendingUnreadablePrev.Where(p => IsUnder(u.Path, p.Path)))   // in previous order
            if (seenUnreadableFiles.Add(p.Path)) await Emit(new FileChange(p.Path, ChangeKind.Unreadable, null, p, null, null, null, u.Reason));
    else
    {
        var p = pendingUnreadablePrev.FirstOrDefault(x => x.Path == u.Path);
        if (p is not null) { if (seenUnreadableFiles.Add(p.Path)) await Emit(new FileChange(p.Path, ChangeKind.Unreadable, null, p, null, null, null, u.Reason)); }
        else if (!scannedPaths.Contains(u.Path) && seenUnreadableFiles.Add(u.Path)) await Emit(new FileChange(u.Path, ChangeKind.Unreadable, null, null, null, null, null, u.Reason));
    }
}
```

`PassedOverAsync(p)`: if any `unreadableDirs` entry `IsUnder` it (or `unreadableDirs` contains `""`/`"."`), park it in `pendingUnreadablePrev`; else if a non-directory unreadable has this exact path, park it too; else emit `Deleted` now. The only unbounded list is `pendingUnreadablePrev`, which is bounded by the size of the unreadable subtrees — the old code held the whole previous index, so this is strictly smaller; write that in a comment. `scannedPaths.Contains(u.Path)` for a non-directory unreadable: the scanner never emits an entry it also reports unreadable, so this is always false — drop the check and comment why (keep the `seen.Add` semantics through `seenUnreadableFiles`). `previousUnder` is unused by this implementation; remove it from the signature before committing — it was a design placeholder and the parked list covers it. Update the interface block above accordingly when done.

`IsUnder(dir, path)` copies `BackupOrchestrator.IsUnder` (find it with `grep -n "static bool IsUnder" Services/BackupOrchestrator.cs`); move it to a shared `internal static class PathUnder` in `PathBoundary.cs` and call it from both.

- [ ] **Step 4: Adapt the existing differ tests** to the new signature via a small local helper `RunDiff(differ, root, scan, previous, unreadable) : Task<List<FileChange>>`, and run everything:

Run: `cd backend && dotnet test --filter "FullyQualifiedName~BackupDiffer"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupDiffer.cs backend/src/AzureStorageBackup.Api/Services/PathBoundary.cs backend/tests/AzureStorageBackup.Api.Tests
git commit -m "feat(diff): merge the scan and the previous version as two ordered cursors"
```

### Task 10: The dedup resolver queries the catalog and the work database

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/LocalDedupResolver.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/LegacyLocalDedupResolver.cs` (verbatim copy, renamed, `internal`, keeps `Build`)
- Test: `backend/tests/AzureStorageBackup.Api.Tests/LocalDedupResolverCatalogTests.cs`; existing `LocalDedupResolverTests.cs`, `LocalDedupResolverPackTests.cs`, `PackAliasDedupTests.cs`, `PackMemberDedupTests.cs` adapted to construct through the catalog

**Interfaces:**
- Produces:
  ```csharp
  public sealed class LocalDedupResolver(BlobAddressScheme addressing, VersionCatalog catalog, RunWorkDb work)
  {
      public Task<bool> MayDeduplicateAsync(long length, string headHash, CancellationToken ct);
      public ValueTask NoteInFlightAsync(long length, string headHash, CancellationToken ct);      // work.reserved_heads
      public Task<bool> IsDamagedRefAsync(string @ref, CancellationToken ct);
      public Task<ResolvedBlob?> TryFindExistingAsync(string fullHash, long length, string headHash, string tailHash, CancellationToken ct);
      public Task<PackMemberRef?> TryFindPackMemberAsync(string fullHash, long length, string headHash, string? tailHash, CancellationToken ct);
      public Task<Resolution> ResolveAsync(string fullHash, long length, string headHash, string tailHash, CancellationToken ct, StageTracker? tracker = null);
      public static string ContentKey(string fullHash, long length, string? head, string? tail);  // unchanged
  }
  ```
  `Build` is deleted. `Resolution.Complete(...)` additionally writes the finished reservation to `work.reservations` (via a callback the resolver passes in) and removes the in-flight object from `_run`.

Lookup order inside `TryFindExistingAsync`: `catalog.FindBlobByContentAsync` → `work.ResumeBlobByContentAsync` (the journal's confirmed blobs, `TryAdd` semantics = only when the catalog has none) → `work.ReservationAsync(contentKey)` (this run's finished uploads; today these are found through `_run[refName].Completion`, already completed). `ResolveAsync`'s `_priorRefs` probe becomes `catalog.FindRefOwnerAsync(refName)` then `work.ResumeBlobByRefAsync` (add that read to `RunWorkDb`: `SELECT … FROM resume_blobs WHERE ref=@r`). `_run` stays a `ConcurrentDictionary<string, Reservation>` of **in-flight** claims only; `Reservation.Complete` and `Fail` both remove the entry (the `release` action today only runs on `Fail`; extend it to run on `Complete` after the row is written).

- [ ] **Step 1: Write the failing equivalence test**

Generate 3–6 `VersionIndex` objects with overlapping content (same full hash across versions, some marked unrecoverable, some pack members, some ref collisions with `~1` suffixes) plus a list of `ConfirmedBlob`s; build the legacy resolver with `LegacyLocalDedupResolver.Build(addressing, indexes, confirmed)`; import the same indexes into a temp `VersionCatalog` and the confirmed blobs into a temp `RunWorkDb` as `resume_blobs` records; for 200 random probes assert `TryFindExisting`, `IsDamagedRef`, `MayDeduplicate`, `TryFindPackMember` answer identically, and for 50 probes that `ResolveAsync(...).Ref` and `.Collision` are identical (no concurrency in this test).

- [ ] **Step 2: Run to verify it fails**

Run: `cd backend && dotnet test --filter FullyQualifiedName~LocalDedupResolverCatalogTests`
Expected: build error.

- [ ] **Step 3: Implement** per the interface block; keep `ResolveAsync`'s loop structure and comments, swapping dictionary probes for the async lookups.

- [ ] **Step 4: Adapt the four existing resolver test files** — each builds indexes inline; add a `TestResolver.From(addressing, indexes, confirmed)` helper in the test project that imports into a temp catalog and work db and returns `(LocalDedupResolver, IAsyncDisposable cleanup)`.

Run: `cd backend && dotnet test --filter "FullyQualifiedName~Dedup"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/LocalDedupResolver.cs backend/tests/AzureStorageBackup.Api.Tests
git commit -m "feat(dedup): resolve against the catalog and the run's work database instead of in-memory maps"
```

### Task 11: The journal's records live in the work database on resume

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/ResumeLedger.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupRunControl.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupJournal.cs` (add `ReadRecordsAsync`)
- Test: `backend/tests/AzureStorageBackup.Api.Tests/LegacyJournalResume.cs` (verbatim copy of `JournalResume`, renamed)
- Test: `backend/tests/AzureStorageBackup.Api.Tests/ResumeLedgerTests.cs`; existing `JournalResumeTests.cs`, `JournalAdoptionTests.cs`, `BackupResumeTests.cs` adapted

**Interfaces:**
- Produces:
  ```csharp
  public sealed class ResumeLedger(RunWorkDb work)
  {
      public static readonly ResumeLedger? Empty = null;   // callers use null for "no journal"
      public Task<bool> IsEmptyAsync(CancellationToken ct);
      public Task<int> RecordCountAsync(CancellationToken ct);
      public Task<JournalRecord?> FindBlobAsync(string path, string fullHash, long length, string headHash, string tailHash, CancellationToken ct);
      public Task<JournalRecord?> FindUntouchedBlobAsync(string path, DateTimeOffset mtimeUtc, long length, CancellationToken ct);
      public Task<JournalRecord?> FindPackAsync(IReadOnlyList<JournalMember> members, CancellationToken ct);
  }
  // BackupJournal
  public static IAsyncEnumerable<JournalRecord> ReadRecordsAsync(string path, CancellationToken ct);   // header validated, then one record per line, malformed lines skipped as ReadAsync does
  // BackupRunControl
  public ResumeLedger? Resume { get; private set; }
  public async Task OpenJournalAsync(int accountId, string container, int baselineVersion, string localRoot, string encryptionIdentity,
      DateTimeOffset startedAt, RunWorkDb work, CancellationToken ct, bool firstRun = false);   // `work` added
  ```
  `ConfirmedBlobs()` disappears: the dedup resolver (Task 10) reads `resume_blobs` directly.

`OpenJournalAsync` keeps its adoption logic (`BackupRunControl.cs:162-232`) but where it collected `adopted` `JournalContent`s and built `JournalResume.FromVolumes(adopted)` it now streams: for each adopted journal, ordered by `Header.StartedAt` descending as `FromVolumes` does, `await foreach (var r in BackupJournal.ReadRecordsAsync(path, ct)) await work.InsertResumeRecordAsync(r, ct)`, then `await work.FlushAsync(ct)`; `Resume = adoptedAny ? new ResumeLedger(work) : null`. `ListAsync` today parses every journal fully to get headers — replace its use here with `store.ListHeadersAsync` (add: reads only the first line of each `.jsonl`; `JournalSummary` already documents that the first line is the header).

- [ ] **Step 1: Write the failing equivalence test**

Generate 300 random `JournalRecord`s across 3 `JournalContent` volumes with overlapping paths and member sets; `LegacyJournalResume.FromVolumes(volumes)` vs a `ResumeLedger` over a work db fed in the same descending-`StartedAt` order; for 200 probes each of `FindBlob`, `FindUntouchedBlob`, `FindPack` assert same `Ref` (or both null).

- [ ] **Step 2: Run to verify it fails** — `dotnet test --filter FullyQualifiedName~ResumeLedgerTests`, build error.

- [ ] **Step 3: Implement**, then adapt `JournalResumeTests.cs` to exercise `ResumeLedger` through a work db (same cases), delete `JournalResume.cs` from the product, and fix `BackupOrchestrator.cs` call sites `control?.Resume.FindBlob(...)` → `await control.Resume.FindBlobAsync(..., ct)` (lines 2461-2490 and 3375 per the grep in the design notes) — the orchestrator's `OpenJournalAsync` call at line ~709 gains the `work` argument in Task 13; until then pass `work: null!` is **not** acceptable — instead, do Task 11 and Task 13's step "open the work db before the journal" together: create the `RunWorkDb` at the top of `RunCoreAsync` in this task (a two-line change: `await using var work = await workFactory.CreateAsync(control?.RunId ?? Guid.NewGuid().ToString("N"), ct);` next to the `using var work = spillFactory?.Create() …` at line 825 — rename that one to `queue`).

- [ ] **Step 4: Run** `dotnet test --filter "FullyQualifiedName~Journal|FullyQualifiedName~Resume"` — PASS (Azurite + 7z needed for `BackupResumeTests`).

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services backend/tests/AzureStorageBackup.Api.Tests
git commit -m "feat(resume): read the journal into the work database instead of dictionaries"
```

### Task 12: `RunLedger` replaces the four path-keyed dictionaries

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/RunLedger.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/RunLedgerTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record EntryOverride(string FullHash, string? HeadHash, long Length, DateTimeOffset Mtime);   // moved out of BackupOrchestrator, made public
  public sealed class RunLedger(RunWorkDb work)
  {
      // the diff seeds one draft row per change, in emission order
      public ValueTask SeedAsync(int seq, FileChange change, CancellationToken ct);
      // the four dictionaries
      public ValueTask SetStorageAsync(string path, StorageRef storage, CancellationToken ct);          // storageByPath[path] = …
      public ValueTask SetTailAsync(string path, string tailHash, CancellationToken ct);                 // tailByPath[path] = …
      public ValueTask SetOverrideAsync(string path, EntryOverride ov, CancellationToken ct);            // overrides[path] = …
      public ValueTask MarkPostDiffUnreadableAsync(string path, string reason, CancellationToken ct);   // postDiffUnreadable[path] = reason
      public Task<StorageRef?> StorageAsync(string path, CancellationToken ct);
      public Task<bool> HasOverrideAsync(string path, CancellationToken ct);
      public Task<bool> IsPostDiffUnreadableAsync(string path, CancellationToken ct);
      public Task<long> PostDiffUnreadableCountAsync(CancellationToken ct);
      public Task FlushAsync(CancellationToken ct);
      // the finish
      public IAsyncEnumerable<IndexEntry> FinalEntriesAsync(CancellationToken ct);   // BuildEntries, one row at a time, ORDER BY seq
      public Task<(long Files, long Bytes)> FinalStatsAsync(CancellationToken ct);
      public Task<int> FinalEntryCountAsync(CancellationToken ct);
      public IAsyncEnumerable<string> UnreadablePathsAsync(CancellationToken ct);                       // Kind == Unreadable, for RecordUnreadableWarningsAsync
      public Task<int> UnreadableUnderAsync(string dir, CancellationToken ct);
  }
  ```

`SeedAsync` writes the draft row with `state = change.Kind switch { Unreadable => Unreadable, Deleted => Dropped, _ => Pending }`, the entry fields from `change.Current` (or `change.Previous` for Unreadable), `head/full/tail` from the change, and two extra draft columns `carried_kind/carried_ref/...` for `CarriedStorage` (store as the storage columns with a `storage_source INTEGER` = 0 carried / 1 set by the run; `SetStorageAsync` writes source 1) and `prev_tail TEXT`, `prev_unreadable_ticks/offset`, `has_previous INTEGER`.

`FinalEntriesAsync` reproduces `BuildEntries` (`BackupOrchestrator.cs:4007-4043`) row by row:

```sql
SELECT … FROM draft ORDER BY seq
```
and per row:
- `state = Unreadable OR post_diff_reason IS NOT NULL` → if `has_previous` yield the previous entry with `UnreadableAt = prev_unreadable ?? now` (store the previous entry's columns in `prev_*` at seed time — this is `c.Previous with { UnreadableAt = … }`), else skip;
- `state = Dropped OR current is null` → skip;
- else build the entry exactly as `BuildEntries` does: `Length = override_length ?? length`, `Mtime = override_mtime ?? mtime`, `HeadHash = override_head ?? head_hash`, `TailHash = tail_set ?? tail_hash ?? prev_tail`, `FullHash = override_full ?? full_hash`, `Storage = kind == "file" && length == 0 ? null : (storage_source == 1 ? storage : carried storage)`.

`now` is one `DateTimeOffset.UtcNow` taken at the start of `FinalEntriesAsync`, matching the old code's per-call `UtcNow` closely enough (the old code took it per entry; the index does not depend on sub-second differences and the acceptance test tolerates `UnreadableAt` by comparing with a 1-minute window on that field only — see Task 22).

- [ ] **Step 1: Write the failing tests** — seed 6 changes covering each branch (Added with storage set, Modified with override, MetadataOnly carrying storage, Unreadable with previous, Unreadable without previous, Deleted, post-diff-unreadable after storage was set, empty file), run `FinalEntriesAsync`, and compare with `LegacyBuildEntries` (copy `BuildEntries` verbatim into the test project taking the same dictionaries) fed the same data.

- [ ] **Step 2: Run to verify it fails** — build error.

- [ ] **Step 3: Implement.**

- [ ] **Step 4: Run** `dotnet test --filter FullyQualifiedName~RunLedgerTests` — PASS.

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Services/RunLedger.cs backend/tests/AzureStorageBackup.Api.Tests/RunLedgerTests.cs
git commit -m "feat(run): a draft-table ledger that replaces the orchestrator's path-keyed dictionaries"
```

### Task 13: Wire the orchestrator to the work database and the catalog

This is the largest task. It is one commit because the orchestrator does not compile in between; work through the sub-steps in order and build after each. Line numbers refer to `BackupOrchestrator.cs` at commit `82bed38`; use `grep` to relocate them.

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupRunner.cs` (GC after a run)
- Test: existing `BackupOrchestratorTests.cs`, `BackupResumeTests.cs`, `BackupCancelModesTests.cs`, `BackupPauseGateIntegrationTests.cs`, `DeferredFullHashTests.cs`, `CompressionContinuityTests.cs`, `ChaosMatrixTests.cs` and every other test that constructs a `BackupOrchestrator` — they change constructor arguments only.

**Interfaces:**
- Consumes: `IVersionCatalogs` (Task 6), `RunWorkDbFactory`/`RunWorkDb` (Task 7), `IScanSink`/`WorkDbScanSink` (Task 8), `BackupDiffer.DiffAsync` (Task 9), `LocalDedupResolver` (Task 10), `ResumeLedger` (Task 11), `RunLedger` (Task 12), `IBackupInfoStore.WriteIndexFileAsync` (Task 5).
- Produces: `BackupOrchestrator` constructor takes `IVersionCatalogs catalogs` and `RunWorkDbFactory workFactory` in place of `ILocalIndexCache indexCache`; everything else on its public surface is unchanged (`RunAsync(BackupRequest, IProgress<BackupProgress>?, CancellationToken, BackupRunControl?)`, `BackupRunResult`).

- [ ] **Step 1: Constructor and run setup**

Replace `ILocalIndexCache indexCache` in the primary constructor with `IVersionCatalogs catalogs, RunWorkDbFactory workFactory`. At the top of `RunCoreAsync`, after `startedAt`, add:

```csharp
await using var work = await workFactory.CreateAsync(control?.RunId ?? Guid.NewGuid().ToString("N"), ct);
var ledger = new RunLedger(work);
```

(If Task 11 already added the `work` line, keep one.) Rename the existing `using var work = spillFactory?.Create() …` at line 825 to `queue` and every `work.` use of the `DiffWorkQueue` to `queue.`.

- [ ] **Step 2: Scan** (lines 640-660)

```csharp
ScanSummary scan;
using (control?.Gate.BeginWork())
    scan = await BeforeUploadAsync(t => scanner.ScanAsync(
        request.LocalRoot, opts.Ignore, new WorkDbScanSink(work, opts.Plan), opts.Scan, t, scanTracker));
await work.FlushAsync(ct);
```

The empty-scope check becomes `scan.Entries == 0 && scan.EmptyDirs.Count == 0 && scan.Unreadable.Count == 0`.

- [ ] **Step 3: Previous version and the catalog** (lines 690-722)

Delete `previous` as a `VersionIndex`. After `info` is loaded:

```csharp
var identity = info.Backup.CreatedAt.UtcTicks;
BackupVersion? last = info.Versions.Count > 0 ? info.Versions[^1] : null;
foreach (var v in info.Versions)
    await BeforeUploadAsync(async t => { await catalogs.EnsureVersionAsync(request.Account, request.Container, v, identity, password, t); return 0; });
await using var catalogForDiff = await catalogs.OpenAsync(request.Account.Id, request.Container, readOnly: true, ct);
await using var catalogForDedup = await catalogs.OpenAsync(request.Account.Id, request.Container, readOnly: true, ct);
```

Delete the `indexes` list and `LocalDedupResolver.Build`; after `OpenJournalAsync` (which now receives `work`):

```csharp
var localResolver = new LocalDedupResolver(addressing, catalogForDedup, work);
```

`EnsureVersionAsync` for every version is what `Build` needed (all retained indexes); it is the moment lazy migration happens for an upgraded install, so it runs under `BeforeUploadAsync` (stoppable, journal-flushed).

- [ ] **Step 4: Classification** (lines 737, 1522-1523, 1588, 1754)

Delete `var classification = planner.Classify(scan.Entries, packOptions);`. Replace `classification.DirectoryCandidates` with:

```csharp
var dirRemaining = new Dictionary<string, int>(StringComparer.Ordinal);
await foreach (var (dir, count) in work.DirectoryCandidatesAsync(ct)) dirRemaining[dir] = count;
```

Replace `classification.ByPath.TryGetValue(c.Path, out var klass)` (line 1588, inside `OnChangeAsync`) with `GroupingPlanner.ClassifyOne(c.Path, c.Current?.Length ?? 0, packOptions)` — same function the sink used, so the answer is identical and no lookup is needed; `DeferFullHash(path)` (line 1754) is called by the differ per scanned entry; it needs the length: change `fullHashDeferred` to `Func<ScannedEntry, bool>` in `BackupDiffer` and `DeferredFullHashTests.cs`, and implement it as `e => GroupingPlanner.ClassifyOne(e.Path, e.Length, packOptions).Category == FileCategory.SingleFile`.

- [ ] **Step 5: The diff call** (lines 1758-1768)

```csharp
var diffTracker = new StageTracker("Diffing", (int)Math.Min(int.MaxValue, scan.Entries), reporter.ReportDiff, …);
DiffTotals diff;
var seq = 0;
async Task OnChangeSeededAsync(FileChange c, CancellationToken t)
{
    await ledger.SeedAsync(seq++, c, t);
    await OnChangeAsync(c, t);
}
using (control?.Gate.BeginWork())
    diff = await differ.DiffAsync(
        request.LocalRoot,
        work.ScanOrderedAsync(stopProducing.Token).Select(ToScannedEntry),
        last is null ? null : catalogForDiff.EntriesAsync(last.Version, stopProducing.Token),
        scan.Unreadable, opts.Diff, stopProducing.Token, diffTracker, OnChangeSeededAsync, DeferFullHash);
```

`Select` on `IAsyncEnumerable` needs a 6-line helper; add `internal static class AsyncEnumerableExtensions` in `RunWorkDb.cs` with `Select` and `ToListAsync`. `ToScannedEntry(ScanRow)` maps back to the record.

- [ ] **Step 6: Thread the ledger through the helpers**

Every helper that takes `ConcurrentDictionary<string, StorageRef> storageByPath, ConcurrentDictionary<string, string> tailByPath, ConcurrentDictionary<string, EntryOverride> overrides, ConcurrentDictionary<string, string> postDiffUnreadable` (any subset) — signatures at lines 1054, 1141, 1167, 1213, 1218, 1235, 1260, 1292, 2282, 2338, 2356, 3308, 3505, 3823, 3907 — takes `RunLedger ledger` instead, and:

| Old | New |
|---|---|
| `storageByPath[p] = s` | `await ledger.SetStorageAsync(p, s, ct)` |
| `tailByPath[p] = t` | `await ledger.SetTailAsync(p, t, ct)` |
| `overrides[p] = o` | `await ledger.SetOverrideAsync(p, o, ct)` |
| `postDiffUnreadable[p] = r` | `await ledger.MarkPostDiffUnreadableAsync(p, r, ct)` |
| `storageByPath.GetValueOrDefault(p)` | `await ledger.StorageAsync(p, ct)` |
| `overrides.ContainsKey(p)` | `await ledger.HasOverrideAsync(p, ct)` |
| `postDiffUnreadable.ContainsKey(p)` | `await ledger.IsPostDiffUnreadableAsync(p, ct)` |
| `postDiffUnreadable.Count` | `await ledger.PostDiffUnreadableCountAsync(ct)` |

The alias backfill (lines 1884-1915) reads `storageByPath` for each leader and writes for each alias; it becomes `await ledger.FlushAsync(ct)` first (so reads see everything), then the same loop with the async calls. `aliasTable`, `dirPending`, `crossPending` stay in memory (spec: bounded by duplicates and by directory count).

- [ ] **Step 7: The finish** (lines 1985-2030)

```csharp
await ledger.FlushAsync(ct);
var version = (info.Versions.LastOrDefault()?.Version ?? 0) + 1;
var emptyDirs = await CarryEmptyDirsAsync(scan, last is null ? null : catalogForDiff, last?.Version, ct);
var entryCount = await ledger.FinalEntryCountAsync(ct);
var serialized = Path.Combine(workFactory.RootDir, $"{work.RunIdOrName}.v{version}.idx");   // add RunWorkDb.Name = file stem
await using (var file = File.Create(serialized))
using (var w = new IndexStreamWriter(file))
{
    w.WriteHeader(version, entryCount);
    await foreach (var e in ledger.FinalEntriesAsync(ct)) w.WriteEntry(e);
    w.WriteEmptyDirs(emptyDirs);
    w.WriteUnrecoverable([]);
}
progress?.Report(new BackupProgress(BackupStage.WritingIndex, diff.ChangedFiles, diff.ChangedBytes, uploaded, total));
string indexBlob; int indexVolumes;
try
{
    (indexBlob, indexVolumes) = await store.WriteIndexFileAsync(
        request.Account, request.Container, version, serialized, password, request.IndexTier, ct, indexTracker);
}
finally { indexTracker.Complete(); }
var (files, bytes) = await ledger.FinalStatsAsync(ct);
// … info.Versions.Add(new BackupVersion { …, Stats = new VersionStats(files, bytes, diff.ChangedFiles, diff.ChangedBytes) }) and the info write exactly as today …
// after the info file is written and the local state updated:
try
{
    using var _ = await catalogs.LockForWriteAsync(request.Account.Id, request.Container, ct);
    await using var catalog = await catalogs.OpenAsync(request.Account.Id, request.Container, readOnly: false, ct);
    await using var file = File.OpenRead(serialized);
    using var reader = new IndexStreamReader(file);
    await catalog.ImportVersionAsync(version, identity, reader, ct);
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    await Record(NotificationEvents.BackupFailure, source, $"Local catalog not updated: {request.Name}",
        $"Version {version} was written to the cloud but could not be recorded in the local catalog ({ex.Message}). It will be re-downloaded on first use; for an Archive-tier index that means a rehydration.", ct, OperationLogLevel.Error);
}
finally { try { File.Delete(serialized); } catch { /* temp */ } }
```

Importing the file we just serialized (rather than inserting from `draft`) guarantees the catalog holds exactly the bytes the cloud holds, `seq` included. Retry-once from the spec: wrap the import in a two-attempt loop before the `catch`.

`CarryEmptyDirsAsync(scan, catalog, prevVersion, ct)` keeps `CarryEmptyDirs`'s logic with `previous.EmptyDirs` replaced by `await catalog.EmptyDirsAsync(prevVersion, ct)`.

- [ ] **Step 8: Result and warnings** (lines 2142-2160, 2195-2210)

`foreach (var c in diff.Changes)` at 2142 computes counts; take them from `ledger` instead: add `Task<(int New, int Modified, int Deleted, long DeletedBytes)> ChangeCountsAsync(ct)` to `RunLedger` (`SELECT COUNT(*) … GROUP BY kind` over a `kind INTEGER` column seeded from `FileChange.Kind`; `DeletedBytes` sums `prev_length` where kind = Deleted). `RecordUnreadableWarningsAsync` (2195-2210) uses `ledger.UnreadableUnderAsync(dir.Path, ct)` and `ledger.UnreadablePathsAsync(ct)`.

- [ ] **Step 9: Delete the old scanner overload** kept in Task 8, delete `BuildEntries`, `EntryOverride` (now in `RunLedger.cs`), `CarryEmptyDirs`. Build:

Run: `cd backend && dotnet build`
Expected: errors only in tests that construct `BackupOrchestrator` with the old arguments.

- [ ] **Step 10: Fix the test constructors** — every `new BackupOrchestrator(… indexCache …)` becomes `new BackupOrchestrator(… TestCatalogs.New(db, store), new RunWorkDbFactory(TestTemp.Dir("work")) …)` (add `TestTemp.Dir(string)` to the test project if no such helper exists: a per-process temp root like `TestIndexFiles`). `BackupRunner` change: after `state.Completion.TrySetResult()` in each terminal branch of `RunAsync` (`BackupRunner.cs:757-790`), call a new `private static void ReleaseRunMemory()`:

```csharp
private static void ReleaseRunMemory()
{
    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
}
```

`GCCollectionMode.Aggressive` (available since .NET 7) also decommits. Call it once per run end, from a `finally` around the four branches.

Run: `cd backend && dotnet test`
Expected: PASS with Azurite and 7z present, `0 skipped` apart from platform gates. Fix anything that regressed before committing; do not weaken assertions.

- [ ] **Step 11: Commit**

```bash
git add backend/src backend/tests
git commit -m "feat(backup): run the pipeline over the work database and the catalog; no structure grows with the file count"
```

### Task 14: Wiring, GC mode, startup cleanup and the retired setting

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Program.cs`
- Modify: `backend/src/AzureStorageBackup.Api/AzureStorageBackup.Api.csproj`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/IndexCacheSizeConfigTests.cs` (rewrite), `TestWebAppFactory.cs`

- [ ] **Step 1: csproj**

```xml
<ServerGarbageCollection>false</ServerGarbageCollection>
<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
```
inside the first `<PropertyGroup>`. Verify with `dotnet publish -c Release -o /tmp/asb-pub && grep -A1 '"System.GC.Server"' /tmp/asb-pub/AzureStorageBackup.Api.runtimeconfig.json` → `false`; delete `/tmp/asb-pub`.

- [ ] **Step 2: Program.cs**

Replace lines 79-87 (`ILocalIndexCache`, `VersionIndexMemoryCache`) with:

```csharp
builder.Services.AddSingleton(sp => new VersionCatalogStore(Path.Combine(dbDir, "index-cache"), sp.GetService<ILogger<VersionCatalogStore>>()));
builder.Services.AddScoped<IVersionCatalogs, VersionCatalogs>();
if (builder.Configuration["Backup:IndexCacheSize"] is { } retired)
    startupNotes.Add($"Backup__IndexCacheSize={retired} is no longer used: version indexes are read from the SQLite catalog on demand.");
```

`dbDir` is computed at lines 115-116; move that block above. `startupNotes` is a `List<string>` logged after `app.Logger` exists (look at how `ioPriorityOutcome` is logged at line 332 and do the same). Keep `VersionIndexFileStore` registered (Task 6 reads from it). Add:

```csharp
var workDir = Path.Combine(tempPath, "work");
RunWorkDbFactory.ClearStale(workDir);
builder.Services.AddSingleton(new RunWorkDbFactory(workDir));
```
next to `DiffWorkQueue.ClearStale(spillDir)`. Pass `Path.Combine(tempPath, "index")` as the `BackupInfoStore` temp root. Replace `sp.GetRequiredService<ILocalIndexCache>()` in the `BackupRepairer` registration with `sp.GetRequiredService<IVersionCatalogs>()` (the repairer's constructor changes in Task 20; until then this line will not compile — do Task 14 immediately before Task 15 and accept that `Program.cs` compiles only after Task 21; the unit tests of Tasks 15-21 do not need the host). **Alternative that keeps every commit green:** register both during the transition — leave `ILocalIndexCache` registered until Task 22 removes it. Do this.

- [ ] **Step 3: `IndexCacheSizeConfigTests`** — replace with a test that boots `TestWebAppFactory` with `Backup:IndexCacheSize=0` and asserts the app starts and the log contains "no longer used" (the factory exposes logs? check `TestWebAppFactory.cs`; if not, assert only that startup succeeds and `IVersionCatalogs` resolves).

- [ ] **Step 4: Run** `cd backend && dotnet test` — PASS.

- [ ] **Step 5: Commit**

```bash
git add backend/src/AzureStorageBackup.Api/Program.cs backend/src/AzureStorageBackup.Api/AzureStorageBackup.Api.csproj backend/tests
git commit -m "feat(host): register the catalog and work database, workstation GC, and retire Backup__IndexCacheSize"
```

---

## Phase C: browsing and maintenance consumers

### Task 15: Endpoints read the catalog

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/BackupConfigEndpoints.cs` (lines 48-190 import, 276-350 delete, 504-540 file-versions, 540-620 repair-plan, 622-660 hash-file, 679-735 unrecoverable/unreadable, 735-765 tree, 765-840 restore-estimate, 1349-1452 local-root)
- Modify: `backend/src/AzureStorageBackup.Api/Services/VersionTreeService.cs`, `RestoreEstimator.cs`, `LocalRootMigration.cs`
- Test: `VersionTreeServiceTests.cs`, `RestoreEstimatorTests.cs`, `LocalRootMigrationTests.cs`, `BackupConfigEndpointsTests.cs`, `BrowseEndpointTests.cs`, `LocalRootEndpointTests.cs`, `BackupImportLifecycleTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // VersionTreeService: pure function over what the catalog returns
  public static IReadOnlyList<TreeNode> Children(IReadOnlyList<CatalogChild> children, string? dirPath);
  // RestoreEstimator
  public static RestoreEstimate Compute(IReadOnlyList<IndexEntry> selected, BackupInfoFile info);   // selected = EntriesAtAsync(version, paths) filtered to Storage != null
  // LocalRootMigration
  public static LocalRootPreviewResponse Inspect(string newRoot, IReadOnlyList<IndexEntry>? sample);   // sample = catalog.SampleAsync(version, DefaultSampleSize) or null
  ```
  A shared endpoint helper replaces `ReadIndexOrGoneAsync`:
  ```csharp
  private static async Task<VersionCatalog?> OpenVersionOrGoneAsync(IVersionCatalogs catalogs, Account account, string container,
      BackupVersion ver, long identityTicks, string? password, CancellationToken ct)
  // EnsureVersionAsync; on RequestFailedException 404 return null; else OpenAsync(readOnly: true)
  ```

- [ ] **Step 1: Tests first** — for the three static services, keep the existing unit tests but build inputs from the new shapes (`CatalogChild` lists, entry lists, samples). `VersionCatalog.SampleAsync` implements the stratified sample from `LocalRootMigration.Sample` (`:134-199`) in SQL: per length bucket (`length = 0`, `< 1 MB`, `< 100 MB`, else) `SELECT COUNT(*)` and `SELECT … ORDER BY seq LIMIT @take OFFSET @step*i` — reproduce `TakeEvenly` by computing the offsets in C# and issuing one query per picked row, at most 200 queries; move `Sample`'s bucket/quota arithmetic into a `internal static class SamplePlan` shared by the catalog and by `LocalRootMigrationTests`. Endpoint tests: adapt to the new DI (they go through `TestWebAppFactory`, so mostly unchanged); add `FileVersions_answers_from_one_query_per_version` if not covered.

- [ ] **Step 2: Implement each endpoint**

| Endpoint | Query |
|---|---|
| `/import` | `catalogs.EnsureVersionAsync` per version (same try/catch, same `unreadable` list). |
| `DELETE /{id}` | `catalogs.RemoveContainerAsync`. |
| `/file-versions` | per version: `catalog.UnrecoverableAsync(v).Contains(path)` → use a new `catalog.IsUnrecoverableAsync(v, path)`; then `catalog.GetEntryAsync(v, path)` with `Storage != null`. |
| `/repair-plan` | `catalog.EntriesAtAsync(v, badPaths)` instead of the full dictionary. |
| `/hash-file` | `GetEntryAsync`. |
| `/unrecoverable` | `UnrecoverableAsync`. |
| `/unreadable` | `UnreadableAsync`. |
| `/tree` | `VersionTreeService.Children(await catalog.ChildrenAsync(v, NormalizePrefix(path)), path)`. |
| `/restore-estimate` | `EntriesAtAsync(v, body.Paths)` → `RestoreEstimator.Compute(selected, info)`; the `volumesByKey` second scan (794-802) is built from the same `selected` list, not the whole index. |
| local-root | `LoadBaselineAsync` returns `IReadOnlyList<IndexEntry>?` from `SampleAsync(latest.Version, LocalRootMigration.DefaultSampleSize)`. |

- [ ] **Step 3: Run** `cd backend && dotnet test --filter "FullyQualifiedName~Endpoint|FullyQualifiedName~VersionTree|FullyQualifiedName~RestoreEstimator|FullyQualifiedName~LocalRoot|FullyQualifiedName~Import"` — PASS.

- [ ] **Step 4: Commit**

```bash
git add backend/src backend/tests
git commit -m "feat(api): browse, look up and estimate from the catalog instead of a whole index"
```

### Task 16: Restore streams from the catalog

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/RestoreOrchestrator.cs` (constructor, `RunCoreAsync` 107-330, `IndexByPath` 975-1000, `FindCaseCollisions` 1012)
- Modify: `backend/src/AzureStorageBackup.Api/Program.cs` (restore registration gets `IVersionCatalogs`)
- Test: `RestoreOrchestratorTests.cs`, `RestoreConflictTests.cs` (if present), `RestoreSubstitutionTests.cs` (if present) — constructor only, plus one new case

**Interfaces:**
- Consumes: `IVersionCatalogs`, `VersionCatalog.EntriesByStorageAsync`, `EntriesAtAsync`, `GetEntryAsync`, `EmptyDirsAsync`, `UnrecoverableAsync`, `CaseCollisionsAsync`.
- Produces: constructor `RestoreOrchestrator(IBlobClientFactory, IBackupInfoStore, IVersionCatalogs, IFileCompressor, IFileHasher, string tempRoot, INotifier?, IOperationLog?)`.

- [ ] **Step 1: New test** — `Restore_of_a_version_not_in_the_catalog_imports_it_first` (backup with the orchestrator, delete the catalog file, restore; expect success and the catalog to exist again).

- [ ] **Step 2: Rewrite `RunCoreAsync`'s index handling**

- `EnsureVersionAsync` for the target and every substitution source version; open one read-only catalog.
- Substitutions: for each `(path, version)` in `request.Substitutions`, `GetEntryAsync(version, path)` and `IsUnrecoverableAsync(version, path)`; store the resolved `IndexEntry` in a small `Dictionary<string, IndexEntry> substituted` (bounded by the substitution count, a user selection).
- `unresolved`: `UnrecoverableAsync(version)` filtered as today (list bounded by damage, not file count).
- Case collisions: `CaseCollisionsAsync(version)` returns the colliding paths; exclude `unresolved`; group by `path_fold` in C# (the list is the collisions only).
- Empty dirs: `EmptyDirsAsync(version)` when `selected is null`.
- The entry walk: when `selected is null`, `await foreach` over `EntriesByStorageAsync(version)`, applying substitutions and skipping collisions/unresolved per entry; when `selected` is given, `EntriesAtAsync(version, selected)` sorted by storage key in C# (bounded by the selection). Groups are formed by comparing consecutive storage keys — `RestoreGroupAsync` keeps its `List<IndexEntry> group` signature and is called when the key changes. Symlinks and empty files are handled inline as today, in the same order relative to files (symlinks first, then empty files, then groups: since the old code did three passes over `byPath.Values`, do three passes over the cursor: `EntriesAsync(version)` filtered to symlinks, then empty files, then `EntriesByStorageAsync` for the rest — three cheap scans beat holding the index).
- Duplicate paths: `import_issues` rows with `issue = 'duplicate'` for the version → `phase.Report` and `failed += count` (add `catalog.ImportIssuesAsync(version)`).
- `groups.Count` for the tracker total: `SELECT COUNT(DISTINCT storage_kind || ':' || storage_ref) FROM entries WHERE version=@v AND storage_kind IS NOT NULL` (add `catalog.StorageGroupCountAsync`), and `groupWork`/`downloadSizes` are computed per group as it arrives (the tracker's `Plan` calls accept increments — check `StageTracker` for an `AddPlanned` or equivalent; if the tracker needs totals up front, compute `SUM(length)` grouped in one query `SELECT storage_kind, storage_ref, SUM(length) … GROUP BY 1,2` streamed into the tracker before the walk).

- [ ] **Step 3: Run** `dotnet test --filter "FullyQualifiedName~Restore"` — PASS.

- [ ] **Step 4: Commit**

```bash
git add backend/src backend/tests
git commit -m "feat(restore): walk the version from the catalog, grouped by storage object"
```

### Task 17: Retention cleans up from a set difference

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/RetentionCleaner.cs` (constructor 47-51, eviction 186-187, referenced set 195-232, compactor input 283-286)
- Test: `RetentionCleanerJournalTests.cs`, `RetentionCleanerVolumeNameTests.cs`, `BackupLifecycleTests.cs`; new `RetentionCleanerCatalogTests.cs`

**Interfaces:**
- Consumes: `VersionCatalog.RefsOnlyInAsync`, `LivePackMembersAsync`, `DistinctRefsAsync`, `RemoveVersionAsync`.
- Produces: constructor parameter `IVersionCatalogs? catalogs = null` replaces `ILocalIndexCache? indexCache = null`.

- [ ] **Step 1: Equivalence test** — build 4 versions with shared refs into a catalog, retire versions {1, 3}; assert the set of refs the new cleaner would delete equals `allRefsOf(1,3) − allRefsOf(2,4)` computed in C# from the same `VersionIndex` objects, for blobs and for packs; and that `liveByPack` handed to the compactor equals the old nested dictionary built by the loop at 199-229 (copy that loop into the test as `LegacyLiveByPack`).

- [ ] **Step 2: Implement** — the deletion candidates: `RefsOnlyInAsync(retired, "blob")` and `RefsOnlyInAsync(retired, "pack")`; the compactor's `liveByPack` is built by streaming `LivePackMembersAsync` **after** the retired versions' rows are removed from the catalog — but the rule is cloud first: so order is (1) delete retired index blobs and data blobs in the cloud as today, (2) `catalogs.RemoveVersionAsync` for each retired version, (3) build `liveByPack` from the catalog's remaining versions and run the compactor. Journal-protected refs (`ActiveJournalRefs`) stay excluded exactly as today.

- [ ] **Step 3: Run** `dotnet test --filter "FullyQualifiedName~Retention|FullyQualifiedName~Lifecycle"` — PASS.

- [ ] **Step 4: Commit**

```bash
git add backend/src backend/tests
git commit -m "feat(retention): find unreferenced blobs with one catalog query and drop retired versions after the cloud"
```

### Task 18: The checker reads a cursor and rewrites through a patch

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupChecker.cs` (constructor 15-24, 200-300, 383-434, 440-460, 505-560)
- Modify: `backend/src/AzureStorageBackup.Api/Program.cs` (checker registration)
- Test: `BackupCheckerTests.cs`, `BackupReferencedSetTests.cs`, `CheckRunSettleOrderingTests.cs`; new case for the patch path

**Interfaces:**
- Consumes: `VersionCatalog.EntriesAsync`, `EntriesByStorageAsync`, `EntriesAtAsync`, `DistinctRefsAsync`, `SerializeVersionAsync(version, stream, patches)`, `ApplyPatchesAsync`; `IBackupInfoStore.WriteIndexFileAsync`.
- Produces: constructor gains `IVersionCatalogs catalogs`; `ReferencedBlobNames(BackupInfoFile info, IAsyncEnumerable<(string Kind, string Ref)> refs)` replaces the dictionary overload (keep the pure function's tests by feeding them an in-memory async enumerable).

- [ ] **Step 1: Tests** — `Check_marks_unrecoverable_through_the_cloud_then_the_catalog`: run a backup, delete one data blob, run a check with `markFindings: true`, assert the cloud index (read via `ReadIndexToFileAsync` + `IndexStreamReader`) lists the path and `catalog.UnrecoverableAsync` lists it too. `Check_that_fails_to_upload_leaves_the_catalog_unchanged`: substitute a store whose `WriteIndexFileAsync` throws; catalog unchanged.

- [ ] **Step 2: Implement**

- Load: `EnsureVersionAsync(ver)`, open read-only; scope → `EntriesAtAsync(ver, scope)` (bounded by scope) or `EntriesAsync(ver)` (cursor). `index.Entries.Count` for the tracker → `StatsAsync(ver).Files`.
- `CloudCheckAsync` groups → `EntriesByStorageAsync` with the consecutive-key grouping from Task 16 (extract `internal static IAsyncEnumerable<List<IndexEntry>> GroupByStorageAsync(IAsyncEnumerable<IndexEntry>)` into `CatalogSql.cs` and use it in both).
- Findings: `List<FileFinding>` stays — it is the check report (persisted; already file-count-proportional today and outside this spec's scope; note it in the commit body).
- `markFindings`: build `List<CatalogPatch>` (`Unrecoverable = true/false` per finding), then: serialize with patches to a temp file → `WriteIndexFileAsync` → info write → `LockForWriteAsync` + `ApplyPatchesAsync`.
- `BuildReferencedSetAsync`: `EnsureVersionAsync` for every version, then `ReferencedBlobNames(info, catalog.DistinctRefsAsync(ct))`.

- [ ] **Step 3: Run** `dotnet test --filter "FullyQualifiedName~Check"` — PASS.

- [ ] **Step 4: Commit**

```bash
git add backend/src backend/tests
git commit -m "feat(check): verify a version from the catalog and record marks cloud-first"
```

### Task 19: The repairer patches instead of mutating lists

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupRepairer.cs` (constructor 23-41, 170-225, 299-322, 348-362, 470-510, 774-800)
- Test: `BackupRepairerTests.cs`, `BackupRepairerPlanTests.cs` (if present), `DeferredRepairsTests.cs`

**Interfaces:**
- Consumes: `VersionCatalog.EntriesReferencingAsync(ref)`, `PackMembersAsync(packId)`, `ApplyPatchesAsync`, `SerializeVersionAsync(…, patches)`.
- Produces: constructor parameter `IVersionCatalogs catalogs` replaces `ILocalIndexCache? indexCache`; `MarkUnrecoverable`/`ClearUnrecoverable` become methods on a small `RepairPatchSet` class (`Mark(version, path)`, `Clear(version, path)`, `SetStorage(version, path, StorageRef)`, `ChangedVersions`, `PatchesFor(version)`).

- [ ] **Step 1: Tests** — the existing repairer tests exercise the flows end to end; add `Repair_rewrites_only_versions_it_changed_and_keeps_entry_order` (byte-compare the untouched version's cloud index before and after).

- [ ] **Step 2: Implement**

- `indexes` dictionary (175-177) → `EnsureVersionAsync` for every version, one read-only catalog handle.
- Pre-marks (213-218): `foreach damaged ref: await foreach (v, e) in EntriesReferencingAsync(ref) → patches.Mark(v, e.Path)`.
- `PersistChangedAsync`: for each changed version: `SerializeVersionAsync(v, file, patches.PatchesFor(v))` → `WriteIndexFileAsync` → info write → then, under the write lock, `ApplyPatchesAsync(all patches of v)` and clear that version from the patch set.
- `RepairBlobAsync` refs (358-361) → `EntriesReferencingAsync(blobRef)` materialized into `refs` (bounded by how many entries reference one blob — typically ≤ versions count).
- Volume-size rewrite (474-481) → `patches.SetStorage(v, e.Path, e.Storage with { Volumes = …, VolumeSizes = … })`; `CatalogPatch.Storage` carries it (Task 2 already supports it in `Apply`).
- `RepairPackAsync` members (500-507) → `PackMembersAsync(packId)`.

- [ ] **Step 3: Run** `dotnet test --filter "FullyQualifiedName~Repair"` — PASS.

- [ ] **Step 4: Commit**

```bash
git add backend/src backend/tests
git commit -m "feat(repair): patch entries in the catalog after the rewritten index is in the cloud"
```

### Task 20: Deferred repairs ask the catalog

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/DeferredRepairs.cs` (45-70, 86-107)
- Test: `DeferredRepairsTests.cs`

- [ ] **Step 1: Tests** — `HealCandidates` takes `IReadOnlyList<IndexEntry> markedEntries` (the latest version's entries at the marked paths) and `localRoot`; adapt the existing tests.

- [ ] **Step 2: Implement** — `marked = await catalog.UnrecoverableAnyVersionAsync(ct).ToListAsync()`; `markedEntries = await catalog.EntriesAtAsync(latest.Version, marked, ct)`; `HealCandidates(markedEntries, localRoot)` keeps the file checks.

- [ ] **Step 3: Run** `dotnet test --filter FullyQualifiedName~DeferredRepairs` — PASS.

- [ ] **Step 4: Commit**

```bash
git add backend/src backend/tests
git commit -m "feat(repair): find heal candidates with two catalog queries"
```

### Task 21: Remove the retired code

**Files:**
- Delete: `Services/LocalIndexCache.cs`, `Services/VersionIndexMemoryCache.cs`, `Services/JournalResume.cs` (if not already), `tests/TestLocalAuthority.cs` (replace uses with `TestCatalogs`), `tests/IndexCacheLockContentionTests.cs`, `tests/LocalIndexCacheTests.cs`, `tests/VersionIndexFileStoreTests.cs` (keep the cases that cover `OpenBodyAsync`, move them to `VersionCatalogsMigrationTests`)
- Modify: `Services/IBackupInfoStore.cs` and `BackupInfoStore.cs` — delete `ReadIndexAsync`/`WriteIndexAsync(VersionIndex)`; `Services/IArchiveCodec.cs` keeps the byte methods (the info file still uses them); `Services/IndexSerializer.cs` — delete `SerializeIndex`/`DeserializeIndex`, move them verbatim into `tests/LegacyIndexSerializer.cs`; `Services/VersionIndexFileStore.cs` — delete `WriteAsync`, `ReadAsync`; keep `OpenBodyAsync`, `Remove`, `RemoveForContainer`, `PathFor`; `Program.cs` — remove `ILocalIndexCache` registration.
- Test: everything.

- [ ] **Step 1:** `grep -rn "ILocalIndexCache\|VersionIndexMemoryCache\|JournalResume\b\|IndexSerializer.SerializeIndex\|IndexSerializer.DeserializeIndex\|ReadIndexAsync\|WriteIndexAsync(" backend/src` must return nothing when done. Tests that used `IndexSerializer.SerializeIndex` as a fixture builder switch to `LegacyIndexSerializer`.

- [ ] **Step 2: Run** `cd backend && dotnet test` — PASS, `0 skipped` beyond platform gates.

- [ ] **Step 3: Commit**

```bash
git add -A backend
git commit -m "refactor(index): retire the in-memory index cache, the .idx writer and the byte-array index APIs"
```

---

## Phase D: acceptance, evidence, documentation

### Task 22: A run suspended by 2026.9.7 resumes on this code and produces the same index

**Files:**
- Create: `backend/tests/AzureStorageBackup.Api.Tests/CrossReleaseResumeTests.cs`
- Uses: `Fixtures/resume-2026.9.7/` from Task 0

**Interfaces:**
- Consumes: `BackupJournalStore`, `VersionIndexFileStore` (with the fixture's `index-cache` copied to a temp root), `LocalBackupStateStore`, `BackupOrchestrator`, `IndexStreamReader`, `LegacyIndexSerializer` (to read `expected/v2.idx`).

- [ ] **Step 1: Write the test** (Azurite + 7z)

```csharp
[SkippableFact]
public async Task Suspended_by_previous_release_resumes_and_writes_the_same_index()
{
    Skip.IfNot(AzuriteReachable(), "Azurite not running");
    Skip.IfNot(SevenZip(), "7z not found");
    var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "resume-2026.9.7");

    // 1. Azurite gets the blobs the old release left in the container
    var account = AzuriteAccount();
    var container = RandomName("xrel");
    var cc = factory.CreateServiceClient(account).GetBlobContainerClient(container);
    await cc.CreateAsync();
    foreach (var file in Directory.EnumerateFiles(Path.Combine(fixture, "blobs")))
        await cc.GetBlobClient(Path.GetFileName(file).Replace("__", "/")).UploadAsync(file);

    // 2. the local state the old release left: journal + mark (rewritten for this account/container), .idx cache, info bytes
    var journalRoot = TestTemp.Dir("xrel-journal");
    CopyDirectoryRewritingContainer(Path.Combine(fixture, "journal"), journalRoot, fixtureAccountId: 1, fixtureContainer: FixtureContainerName, account.Id, container);
    var idxRoot = TestTemp.Dir("xrel-idx");
    CopyDirectoryRewritingContainer(Path.Combine(fixture, "index-cache"), idxRoot, 1, FixtureContainerName, account.Id, container);
    await localState.PutAsync(account.Id, container, await File.ReadAllBytesAsync(Path.Combine(fixture, "info.bin")), etag: null);

    // 3. resume with the new code (the journal header's LocalRoot must match: the fixture recorded a temp path — rewrite it to `source` here as well)
    var source = Path.Combine(fixture, "source");
    var (orchestrator, store, _) = Build(journals: new BackupJournalStore(journalRoot), legacyFiles: new VersionIndexFileStore(idxRoot));
    await using var control = new BackupRunControl(new BackupJournalStore(journalRoot), configId: 1, runId: "xrel-resume");
    var result = await orchestrator.RunAsync(Request(account, container, source), control: control);

    // 4. the index must be what the old release produced
    var info = await store.ReadInfoAsync(account, container, null);
    var v2 = info!.Versions.Single(v => v.Version == 2);
    var actualPath = TestTemp.File("v2.idx");
    await store.ReadIndexToFileAsync(account, container, v2.IndexBlob, null, v2.IndexVolumes, actualPath);
    var expected = LegacyIndexSerializer.DeserializeIndex(await File.ReadAllBytesAsync(Path.Combine(fixture, "expected", "v2.idx")));
    using var actual = new IndexStreamReader(File.OpenRead(actualPath));
    var actualEntries = actual.Entries().ToList();
    Assert.Equal(expected.Entries.Count, actualEntries.Count);
    for (var i = 0; i < expected.Entries.Count; i++)
        AssertSameEntry(expected.Entries[i], actualEntries[i], tolerateUnreadableAt: TimeSpan.FromMinutes(1));
    Assert.Equal(expected.EmptyDirs, actual.ReadEmptyDirs());
    Assert.Equal(expected.UnrecoverablePaths, actual.ReadUnrecoverable());

    // 5. and the resume really reused the journal: fewer uploads than files changed
    Assert.True(result.UploadedBytes < result.ChangedBytes);
}
```

The journal header records `ConfigId`, `LocalRoot`, `EncryptionIdentity` and the account/container are in the path. `CopyDirectoryRewritingContainer` copies files, moving them from `1/{FixtureContainerName}/` to `{account.Id}/{container}/`, and for `.jsonl` files rewrites the first line's `LocalRoot` to `source`. `FixtureContainerName` is a constant the recorder wrote into `fixture/container.txt`; read it. The blob names of an unencrypted fixture are content-addressed (`data/{hash}`), so they are container-independent.

- [ ] **Step 2: Run it**

Run: `cd backend && dotnet test --filter FullyQualifiedName~CrossReleaseResumeTests`
Expected: PASS. If the entry comparison fails on `seq` ordering, the merge in Task 9 or the seed in Task 12 is wrong; fix there, not by relaxing the assertion.

- [ ] **Step 3: Commit**

```bash
git add backend/tests/AzureStorageBackup.Api.Tests/CrossReleaseResumeTests.cs
git commit -m "test(resume): a run suspended by 2026.9.7 resumes on the catalog and writes the same index"
```

### Task 23: Memory benchmark script

**Files:**
- Create: `backend/tests/AzureStorageBackup.Api.Tests/MemoryBenchmarkTests.cs`
- Create: `docs/superpowers/plans/2026-09-07-sqlite-index-catalog-benchmark.md` (the recorded numbers)

- [ ] **Step 1: The benchmark** is a `[SkippableFact]` gated on `ASB_BENCH=1`. It generates 1 000 000 tiny files? No — that takes too long on a laptop. Generate 200 000 files of 1 byte in 2 000 directories (about 4 minutes), run a first backup to Azurite with an `IBlobUploader` substitute that discards bytes (uploads are not the subject), sample `Process.GetCurrentProcess().WorkingSet64` and `GC.GetTotalMemory(false)` every 2 s on a timer, and write the peak of each plus the final values after `ReleaseRunMemory()` to the console and to `benchmark.json` in the test output. Run it twice: once at `82bed38` (before this plan) and once at the end; both numbers go in the doc with the file count so the reader can extrapolate (memory per file, before vs after).

- [ ] **Step 2: Run** `ASB_BENCH=1 dotnet test --filter FullyQualifiedName~MemoryBenchmarkTests` on both commits (use `git worktree add /tmp/asb-before 82bed38` for the old one and copy the test file in).

- [ ] **Step 3: Write the numbers** into the doc: a table with commit, files, peak working set, peak managed heap, post-run working set. The after-run working set at 200k files must be under 400 MB, and peak managed heap must not scale with file count between a 100k and a 200k run (add the 100k run to prove it).

- [ ] **Step 4: Commit**

```bash
git add backend/tests/AzureStorageBackup.Api.Tests/MemoryBenchmarkTests.cs docs/superpowers/plans/2026-09-07-sqlite-index-catalog-benchmark.md
git commit -m "test(bench): record peak memory before and after the catalog"
```

### Task 24: Documentation, release note and version

**Files:**
- Modify: `docs/storage-format.md` ("The local cache" section, lines 180-236), `docs/architecture.md` (line 142 table row), `docs/run-lifecycle.md` (a paragraph on the work database's role vs the journal), `docs/operations.md` and `README.md` (env var table: `Backup__IndexCacheSize` removed with a note; `/temp` sizing gains the work database), `docs/history.md` (release entry), `backend/src/AzureStorageBackup.Api/AzureStorageBackup.Api.csproj` (`<Version>`).

- [ ] **Step 1: storage-format.md** — replace the `.idx` paragraphs with the catalog: path, tables in one sentence each, the four secondary indexes and what asks them, `seq` and why order matters, lazy migration order, corruption handling, "derived data: the cloud index is the recovery copy". Keep the paragraph on why indexes are not rows in `app.db` (the single-writer stall) — it is the reason the catalog is a separate file.

- [ ] **Step 2: architecture.md** row: `Version indexes | SQLite catalog per container in data/index-cache/…, rebuilt from the cloud on demand | authoritative for recovery`. Add `Temp (…, work databases)` to the temp row.

- [ ] **Step 3: README / operations** — remove `Backup__IndexCacheSize` from the table with one line: "Removed in 2026.9.x; ignored with a startup log line." Add to the `/temp` guidance: "plus roughly 500 bytes per scanned file for the run's work database, deleted when the run ends."

- [ ] **Step 4: history.md** entry, in the existing style, including: memory no longer scales with file count; downgrade past this version is unsupported once `.idx` files have been migrated; a run suspended by an earlier version resumes; workstation GC.

- [ ] **Step 5: Version** — bump `<Version>` per the calendar rule in the csproj comment (`YYYY.M.D` or `.N`). Do not publish.

- [ ] **Step 6: Run the whole suite one last time** — `cd backend && dotnet test` and `cd frontend && npx vitest run` — PASS.

- [ ] **Step 7: Commit**

```bash
git add docs README.md backend/src/AzureStorageBackup.Api/AzureStorageBackup.Api.csproj
git commit -m "docs: describe the SQLite index catalog and the work database; version 2026.9.x"
```

---

## Self-review notes

- Spec §Storage layout → Tasks 2, 3, 7. §Backup pipeline → 8–13. §Browsing and maintenance → 15–20. §Serialization, migration, compatibility → 1, 5, 6, 21, 22. §Error handling → 3 (corruption), 13 step 7 (catalog write after cloud), 7 (`FlushAsync` faults). §Concurrency → 3 (lock), 7 (writer channel). §Testing → every task's step 1, plus 22 and 23. §Delivery → 24.
- Spec item not carried over verbatim: "version compare" — the repository has no such endpoint; `/file-versions` (Task 15) is the cross-version query. The spec file should be corrected to say so (one line) in Task 24.
- Known bounded-by-something-other-than-file-count structures left in memory, by design: `ScanSummary.EmptyDirs` and `Unreadable`, `dirRemaining`, `aliasTable`, `dirPending`, `crossPending`, `pendingUnreadablePrev` (Task 9), the check report's `List<FileFinding>` (Task 18), restore substitutions and selections.

---

## Added during execution

### Task 25: The pack alias table's leader map leaves memory

Found by Task 23's benchmark: with unique file contents the live managed heap (forced collection) still grows
by ~56 MB per 100 000 files. `PackAliasTable._leaderByContent` records the first path for EVERY pack member's
content key (`PackAliasTable.cs:97`), not only for duplicates, so it is one entry per small file. The spec's
non-goal ("`aliasTable` … bounded by duplicates") was wrong about this half of the table; `_aliasesByLeader`
(one list per leader that actually has aliases) is the half that is bounded by duplicates and stays in memory.

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/PackLeaderStore.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/PackAliasTable.cs`, `Services/BackupOrchestrator.cs` (the `TryClaim` call and construction), `Services/RunWorkDbFactory.cs` (side-file path + `ClearStale` glob), tests `PackAliasTableTests.cs`, `PackAliasDedupTests.cs`, `MemoryBenchmarkTests.cs` (no change expected), `docs/superpowers/plans/2026-09-07-sqlite-index-catalog-benchmark.md` (round 3), `docs/storage-format.md` (one sentence on the side file).

**Interfaces:**
- `PackLeaderStore : IAsyncDisposable` — its own SQLite file `{tempPath}/work/{runId}.aliases.db` (so its write transaction never contends with `work.db`'s writer), table `pack_leaders(content_key TEXT PRIMARY KEY, path TEXT NOT NULL) WITHOUT ROWID`, pragmas `journal_mode=OFF`, `synchronous=OFF`, `cache_size=-16384`; one connection; `ValueTask<string?> ClaimAsync(string contentKey, string path, CancellationToken ct)` returns the existing leader's path or null after inserting the caller as leader (`INSERT OR IGNORE` + `SELECT` on the same connection inside one long transaction committed every 2 000 claims and on dispose — the connection sees its own uncommitted rows, so a duplicate arriving inside the batch window still finds its leader). File deleted on dispose.
- `PackAliasTable` becomes `PackAliasTable(PackLeaderStore leaders)` with `ValueTask<bool> TryClaimAsync(fullHash, length, headHash, tailHash, path, ct)`; `AliasesByLeader` unchanged.

**Steps:** (1) `PackAliasTableTests` rewritten to the async API over a temp store (same cases) plus one test that 200 000 distinct claims leave the table's managed footprint flat (assert `_aliasesByLeader.Count == 0` and that the store file holds 200 000 rows); (2) implement; (3) orchestrator: construct the store from the factory next to the work db, `await using`, call `TryClaimAsync`; (4) `RunWorkDbFactory.ClearStale` also removes `*.aliases.db`; (5) full suite; (6) re-run the AFTER benchmark (`ASB_BENCH=1`) at 100 k / 200 k with unique contents, update the benchmark doc's primary table and the acceptance verdict (and correct the round-2 attribution to `diff.Changes`, which no longer exists — the structure was the leader map); (7) commit `feat(pack): keep the alias table's leader map in a per-run SQLite file` with the numbers in the body.
