# Catalog Format v2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Store one `entries` row per path per change (a half-open version interval) so a backup's catalog work is proportional to its changes and the catalog's size to paths plus changes, with existing catalogs converted in place.

**Architecture:** `VersionCatalog` keeps its public surface; its SQL changes from `WHERE version=@v` to interval predicates, its import becomes a path-ordered merge against the rows current at the previous version, and its patches isolate one version of a row before writing. A `CatalogUpgrade` converts a format-1 file version by version, resumably, on the first write open after the upgrade; the backup shows it as its own stage. Repository conventions: raw ADO over Microsoft.Data.Sqlite, xunit tests (SQLite-only tests need nothing; Azurite-gated ones use `SkippableFact`), one topic branch per task merged `--no-ff` into main, docs updated with the code.

**Tech Stack:** .NET 10, Microsoft.Data.Sqlite (SQLite 3.46), xunit + NSubstitute, React/TypeScript frontend with vitest.

**Spec:** `docs/catalog-format-v2.md`

## Global Constraints

- Every `VersionCatalog` public method keeps its signature and its documented result; `IVersionCatalogs` gains members only with default implementations.
- The wire format (`IndexStreamWriter.IndexFormat = 4`) does not change; `SerializeVersionAsync` must reproduce imported bytes exactly.
- `EntryRowMapper.Columns` / `ColumnDefinitions` are shared with the run's work database and do not change.
- `PRAGMA user_version = 2` marks a converted or freshly created catalog; format 1 files have `user_version 0` and an `entries.seq` column.
- The sentinel for "still current" is `CatalogSql.OpenEnd = int.MaxValue` (2147483647).
- Tests: run `dotnet test --filter "FullyQualifiedName~<Class>"` per task; run the full suite with Azurite (`systemctl --user start azurite` … `systemctl --user stop azurite` and wipe `~/.local/share/azurite/{AzuriteConfig,__azurite_db_*,__*storage__}`) before each merge. The suite is only meaningful with `7zz` and Azurite present (see README § Tests).
- Commit messages follow the repo's shape (`feat(catalog): …` / `fix(…)` / `docs:`) and end with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- Documentation lives in `docs/*.md` only. When Task 7 lands, the spec and this plan are folded into `docs/storage-format.md` and deleted.

---

## File structure

| File | Responsibility |
|---|---|
| `backend/src/AzureStorageBackup.Api/Services/CatalogSql.cs` | v2 DDL, `OpenEnd`, `Format`, format detection (`CatalogFormat`), `EntryRowMapper.SameEntry` |
| `backend/src/AzureStorageBackup.Api/Services/IndexStreamReader.cs` | exposes `Input` so an import can make two passes over a seekable stream |
| `backend/src/AzureStorageBackup.Api/Services/VersionCatalog.cs` | open, versions, serialization, patches (isolation), plumbing |
| `backend/src/AzureStorageBackup.Api/Services/VersionCatalog.Import.cs` (new) | the merge import, staged import, removal |
| `backend/src/AzureStorageBackup.Api/Services/VersionCatalog.Queries.cs` | every read query on interval rows |
| `backend/src/AzureStorageBackup.Api/Services/CatalogUpgrade.cs` (new) | format 1 → 2 conversion, resumable |
| `backend/src/AzureStorageBackup.Api/Services/VersionCatalogStore.cs` | upgrade hooks on open, `NeedsUpgrade`, `UpgradeAsync` |
| `backend/src/AzureStorageBackup.Api/Services/IVersionCatalogs.cs`, `VersionCatalogs.cs` | pass-through of the two new members, `CatalogUpgradeProgress` |
| `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs` | `UpgradingCatalog` stage; the run's import without the index bracket |
| `backend/src/AzureStorageBackup.Api/Services/CatalogUpgradeAccounting.cs` (new) | books upgrade progress into a `StageTracker` |
| `frontend/src/api/backupConfigs.ts`, `frontend/src/lib/stageLines.ts`, `frontend/src/lib/windDownControls.ts`, `frontend/src/pages/BackupConfigsPage.tsx` | the new stage on screen |
| `backend/tests/AzureStorageBackup.Api.Tests/CatalogV2Tests.cs` (new) | row mechanics, query parity, retention, patches, legacy order |
| `backend/tests/AzureStorageBackup.Api.Tests/CatalogUpgradeTests.cs` (new) | conversion, resume, store hooks |
| `backend/tests/AzureStorageBackup.Api.Tests/LegacyCatalogFixture.cs` (new) | builds a format-1 file with the old DDL |
| `docs/storage-format.md`, `docs/progress-display.md`, `docs/history.md` | the running system's description |

---

### Task 1: Entry equality and a re-readable index stream

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/CatalogSql.cs` (class `EntryRowMapper`, after `Read`)
- Modify: `backend/src/AzureStorageBackup.Api/Services/IndexStreamReader.cs:24-35`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/CatalogV2Tests.cs` (new)

**Interfaces:**
- Produces: `static bool EntryRowMapper.SameEntry(IndexEntry a, IndexEntry b)` — field-by-field equality including `Storage` and its `VolumeSizes` sequence.
- Produces: `IndexStreamReader.Input` (`Stream`) — the stream the reader was built over.

- [ ] **Step 1: Write the failing tests**

```csharp
// backend/tests/AzureStorageBackup.Api.Tests/CatalogV2Tests.cs
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class CatalogV2Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "asb-catalog-v2-tests", Guid.NewGuid().ToString("N"));
    public CatalogV2Tests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private Task<VersionCatalog> OpenAsync() =>
        VersionCatalog.OpenAsync(Path.Combine(_dir, "catalog.db"), readOnly: false, CancellationToken.None);

    internal static IndexEntry Entry(string path, long length, string? hash = null, StorageRef? storage = null) => new()
    {
        Path = path, Kind = "file", Length = length, Mtime = DateTimeOffset.UnixEpoch.AddSeconds(length), Permissions = "0644",
        HeadHash = hash is null ? null : hash + ":head", TailHash = hash is null ? null : hash + ":tail", FullHash = hash,
        Storage = storage,
    };

    [Fact]
    public void SameEntry_compares_every_field_including_the_volume_sizes()
    {
        var a = Entry("a.bin", 5, "xxh128:a", new StorageRef { Kind = "blob", Ref = "data/a", Volumes = 2, VolumeSizes = [3, 2] });
        var same = a with { Storage = new StorageRef { Kind = "blob", Ref = "data/a", Volumes = 2, VolumeSizes = [3, 2] } };
        var sizes = a with { Storage = new StorageRef { Kind = "blob", Ref = "data/a", Volumes = 2, VolumeSizes = [3, 3] } };
        var mtime = a with { Mtime = a.Mtime.AddSeconds(1) };
        var unreadable = a with { UnreadableAt = DateTimeOffset.UnixEpoch };
        var noStorage = a with { Storage = null };

        Assert.True(EntryRowMapper.SameEntry(a, same));
        Assert.False(EntryRowMapper.SameEntry(a, sizes));
        Assert.False(EntryRowMapper.SameEntry(a, mtime));
        Assert.False(EntryRowMapper.SameEntry(a, unreadable));
        Assert.False(EntryRowMapper.SameEntry(a, noStorage));
        Assert.True(EntryRowMapper.SameEntry(noStorage, noStorage with { }));
    }

    [Fact]
    public void An_index_stream_reader_exposes_its_input_for_a_second_pass()
    {
        var bytes = LegacyIndexSerializer.SerializeIndex(IndexSamples.Sample());
        using var stream = new MemoryStream(bytes);
        using var reader = new IndexStreamReader(stream);
        Assert.Same(stream, reader.Input);
        foreach (var _ in reader.Entries()) { }
        Assert.Equal(2, reader.ReadEmptyDirs().Count);
        stream.Position = 0;
        using var again = new IndexStreamReader(reader.Input);
        Assert.Equal(reader.EntryCount, again.Entries().Count());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~CatalogV2Tests"`
Expected: build errors — `SameEntry` and `Input` do not exist.

- [ ] **Step 3: Implement**

In `CatalogSql.cs`, inside `EntryRowMapper` after `Read`:

```csharp
    /// <summary>
    /// Whether two entries are the same row: every field the wire format carries, storage and its volume sizes
    /// included. Records compare <c>VolumeSizes</c> by reference, so <c>==</c> on <see cref="IndexEntry"/> is not
    /// this. The v2 catalog decides "unchanged, write nothing" with it, so it must be exactly the wire format's
    /// notion of equality — a field this misses would be a change the catalog silently drops.
    /// </summary>
    public static bool SameEntry(IndexEntry a, IndexEntry b) =>
        a.Path == b.Path && a.Kind == b.Kind && a.Length == b.Length && a.Mtime == b.Mtime
        && a.Permissions == b.Permissions && a.HeadHash == b.HeadHash && a.TailHash == b.TailHash
        && a.FullHash == b.FullHash && a.Target == b.Target && a.UnreadableAt == b.UnreadableAt
        && SameStorage(a.Storage, b.Storage);

    private static bool SameStorage(StorageRef? a, StorageRef? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        return a.Kind == b.Kind && a.Ref == b.Ref && a.EntryName == b.EntryName && a.Volumes == b.Volumes
            && a.Raw == b.Raw && a.VolumeSizes.SequenceEqual(b.VolumeSizes);
    }
```

In `IndexStreamReader.cs`, add a property and set it in the constructor:

```csharp
    /// <summary>The stream this reader parses. The catalog's import makes two passes over an index — the lists at
    /// the tail first, then the entries — and does so by seeking this stream back to zero and building a second
    /// reader on it; it requires <c>CanSeek</c> and throws otherwise.</summary>
    public Stream Input { get; }

    public IndexStreamReader(Stream input)
    {
        Input = input;
        _r = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
        // … unchanged
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~CatalogV2Tests"`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git checkout -b catalog-v2-equality
git add backend/src/AzureStorageBackup.Api/Services/CatalogSql.cs backend/src/AzureStorageBackup.Api/Services/IndexStreamReader.cs backend/tests/AzureStorageBackup.Api.Tests/CatalogV2Tests.cs
git commit -m "feat(catalog): entry equality over every wire field, and an index reader that can be read twice

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: The v2 schema, the merge import, removal, serialization and every query

This is the atomic switch: the schema, the write path and the read path change together, and the existing catalog tests (`VersionCatalogTests`, `VersionCatalogsMigrationTests`, `VersionCatalogStoreTests`, `RetentionCleanerTests`, checker/repair tests) are the regression net. Upgrading old files is Task 5; until then, a format-1 file on disk is not handled (tests create fresh files).

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/CatalogSql.cs` (`Schema`, `GlobalIndexSchema`, `EnsureSchema`, new constants)
- Modify: `backend/src/AzureStorageBackup.Api/Services/VersionCatalog.cs` (constants, `OpenAsync`, `SerializeVersionAsync`, `ApplyPatchesAsync`, plumbing)
- Create: `backend/src/AzureStorageBackup.Api/Services/VersionCatalog.Import.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/VersionCatalog.Queries.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/CatalogV2Tests.cs`

**Interfaces:**
- Consumes: `EntryRowMapper.SameEntry`, `IndexStreamReader.Input` (Task 1).
- Produces: `CatalogSql.OpenEnd`, `CatalogSql.Format`, `CatalogSql.MarkCurrent(SqliteConnection)`, `VersionCatalog.NextPresentAsync(int, CancellationToken)` (internal), the private merge core `ImportCoreAsync(int version, long identity, int expected, IAsyncEnumerable<IndexEntry> entries, IReadOnlyList<string> emptyDirs, IReadOnlyList<string> unrecoverable, bool ordered, Action<long>? onEntries, CancellationToken ct)`. Task 5 splits it into a transaction wrapper and `MergeVersionAsync`, and adds the upgrade's door `ImportOrderedInTransactionAsync`; the `ImportOrderedAsync` member in this task's file is a placeholder for that door and can be dropped there.

- [ ] **Step 1: Write the failing tests**

Append to `CatalogV2Tests`:

```csharp
    private static byte[] Bytes(VersionIndex index) => LegacyIndexSerializer.SerializeIndex(index);

    private static async Task ImportAsync(VersionCatalog catalog, VersionIndex index, long identity = 1)
    {
        using var reader = new IndexStreamReader(new MemoryStream(Bytes(index)));
        await catalog.ImportVersionAsync(index.Version, identity, reader, CancellationToken.None);
    }

    private static async Task<byte[]> SerializeAsync(VersionCatalog catalog, int version)
    {
        using var ms = new MemoryStream();
        await catalog.SerializeVersionAsync(version, ms, patches: null, CancellationToken.None);
        return ms.ToArray();
    }

    private static async Task<long> CountAsync(VersionCatalog catalog, string sql)
    {
        // The row shape is the thing under test here, so this one helper reads the table directly.
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(CatalogSql.ConnectionString(catalog.Path, readOnly: true));
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static VersionIndex Version(int version, params IndexEntry[] entries) => new()
    {
        Version = version,
        Entries = [.. entries.OrderBy(e => e.Path, StringComparer.Ordinal)],
    };

    [Fact]
    public async Task A_fresh_catalog_is_format_2()
    {
        await using var catalog = await OpenAsync();
        Assert.Equal(2, await CountAsync(catalog, "PRAGMA user_version"));
        Assert.Equal(0, await CountAsync(catalog, "SELECT COUNT(*) FROM sqlite_master WHERE name='entries_seq'"));
    }

    [Fact]
    public async Task A_second_version_writes_only_its_changes()
    {
        await using var catalog = await OpenAsync();
        var v1 = Version(1, Enumerable.Range(0, 1000).Select(i => Entry($"d/{i:D4}.bin", i + 1, $"h{i}")).ToArray());
        await ImportAsync(catalog, v1);
        Assert.Equal(1000, await CountAsync(catalog, "SELECT COUNT(*) FROM entries"));

        // One modified, one deleted, one added; 997 untouched.
        var v2Entries = v1.Entries.Where(e => e.Path != "d/0500.bin").Select(e => e.Path == "d/0100.bin" ? e with { Length = 999 } : e).ToList();
        v2Entries.Add(Entry("d/9999.bin", 1, "new"));
        await ImportAsync(catalog, Version(2, [.. v2Entries]));

        Assert.Equal(1002, await CountAsync(catalog, "SELECT COUNT(*) FROM entries"));                                // 1000 + modified's new row + added
        Assert.Equal(2, await CountAsync(catalog, "SELECT COUNT(*) FROM entries WHERE version_to = 2"));               // modified's old row, deleted
        Assert.Equal(2, await CountAsync(catalog, "SELECT COUNT(*) FROM entries WHERE version_from = 2"));             // modified's new row, added
        Assert.Equal(998, await CountAsync(catalog, $"SELECT COUNT(*) FROM entries WHERE version_from = 1 AND version_to = {int.MaxValue}"));

        Assert.Equal(Bytes(v1), await SerializeAsync(catalog, 1));
        Assert.Equal(Bytes(Version(2, [.. v2Entries])), await SerializeAsync(catalog, 2));
    }

    [Fact]
    public async Task Every_query_answers_per_version_as_the_indexes_say()
    {
        await using var catalog = await OpenAsync();
        var blobA = new StorageRef { Kind = "blob", Ref = "data/a", Volumes = 1, VolumeSizes = [5] };
        var packP = new StorageRef { Kind = "pack", Ref = "p0001", EntryName = "x/one.txt" };
        var v1 = Version(1,
            Entry("x/one.txt", 5, "xxh128:one", packP),
            Entry("x/two.txt", 5, "xxh128:two", new StorageRef { Kind = "pack", Ref = "p0001", EntryName = "x/two.txt" }),
            Entry("y/big.bin", 5, "xxh128:big", blobA));
        var v2 = Version(2,
            Entry("x/one.txt", 5, "xxh128:one", packP),
            Entry("x/Two.txt", 5, "xxh128:two2", new StorageRef { Kind = "pack", Ref = "p0002", EntryName = "x/Two.txt" }), // renamed by case
            Entry("y/big.bin", 6, "xxh128:big2", new StorageRef { Kind = "blob", Ref = "data/b", Volumes = 1, VolumeSizes = [6] }));
        var v3 = Version(3,
            Entry("x/one.txt", 5, "xxh128:one", packP),
            Entry("x/Two.txt", 5, "xxh128:two2", new StorageRef { Kind = "pack", Ref = "p0002", EntryName = "x/Two.txt" }),
            Entry("x/two.txt", 5, "xxh128:two", new StorageRef { Kind = "pack", Ref = "p0001", EntryName = "x/two.txt" }),
            Entry("y/big.bin", 6, "xxh128:big2", new StorageRef { Kind = "blob", Ref = "data/b", Volumes = 1, VolumeSizes = [6] }));
        foreach (var index in new[] { v1, v2, v3 })
            await ImportAsync(catalog, index);

        foreach (var index in new[] { v1, v2, v3 })
        {
            Assert.Equal(Bytes(index), await SerializeAsync(catalog, index.Version));
            var entries = await catalog.EntriesAsync(index.Version, CancellationToken.None).ToListAsync();
            Assert.Equal(index.Entries.Select(e => e.Path), entries.Select(e => e.Path));
            foreach (var e in index.Entries)
                IndexAssert.AssertSameEntry(e, (await catalog.GetEntryAsync(index.Version, e.Path, CancellationToken.None))!);
            var (files, bytes) = await catalog.StatsAsync(index.Version, CancellationToken.None);
            Assert.Equal(index.Entries.Count, files);
            Assert.Equal(index.Entries.Sum(e => e.Length), bytes);
            var root = await catalog.ChildrenAsync(index.Version, "", CancellationToken.None);
            Assert.Equal(["x", "y"], root.Select(c => c.Name));
            var x = await catalog.ChildrenAsync(index.Version, "x", CancellationToken.None);
            Assert.Equal(index.Entries.Where(e => e.Path.StartsWith("x/")).Select(e => e.Path[2..]).OrderBy(n => n, StringComparer.Ordinal), x.Select(c => c.Name));
        }

        Assert.Null(await catalog.GetEntryAsync(1, "x/Two.txt", CancellationToken.None));
        Assert.Null(await catalog.GetEntryAsync(2, "x/two.txt", CancellationToken.None));
        Assert.Equal([("x/Two.txt", 3), ("x/two.txt", 3)], await catalog.CaseCollisionsAsync(3, CancellationToken.None));
        Assert.Empty(await catalog.CaseCollisionsAsync(2, CancellationToken.None));

        // Dedup: the latest version that holds the content wins.
        var hit = await catalog.FindBlobByContentAsync("xxh128:big2", 6, "xxh128:big2:head", "xxh128:big2:tail", CancellationToken.None);
        Assert.Equal("data/b", hit!.Ref);
        Assert.NotNull(await catalog.FindBlobByContentAsync("xxh128:big", 5, "xxh128:big:head", "xxh128:big:tail", CancellationToken.None));
        Assert.Equal("p0001", (await catalog.FindPackMemberAsync("xxh128:two", 5, "xxh128:two:head", CancellationToken.None))!.PackId);

        // Per-version references, expanded from the interval rows.
        var refs = await catalog.EntriesReferencingAsync("p0001", CancellationToken.None).ToListAsync();
        Assert.Equal([(1, "x/one.txt"), (1, "x/two.txt"), (2, "x/one.txt"), (3, "x/one.txt"), (3, "x/two.txt")],
            refs.Select(r => (r.Version, r.Entry.Path)));
        var distinct = await catalog.DistinctRefsAsync(CancellationToken.None).ToListAsync();
        Assert.Equal([("blob", "data/a", 1), ("blob", "data/b", 1), ("pack", "p0001", 1), ("pack", "p0002", 1)], distinct);
        var live = await catalog.LivePackMembersAsync(CancellationToken.None).ToListAsync();
        Assert.Equal(3, live.Count);

        // What becomes garbage when versions retire.
        Assert.Equal(["data/a"], await catalog.RefsOnlyInAsync([1], "blob", CancellationToken.None));
        Assert.Empty(await catalog.RefsOnlyInAsync([1], "pack", CancellationToken.None));     // p0001 lives on through x/one.txt
        Assert.Equal(["p0002"], await catalog.RefsOnlyInAsync([2, 3], "pack", CancellationToken.None)); // p0002 is v2 and v3's alone
        Assert.Empty(await catalog.RefsOnlyInAsync([1, 3], "pack", CancellationToken.None));  // x/one.txt in v2 still points at p0001
    }

    [Fact]
    public async Task Removing_a_version_deletes_only_the_rows_no_remaining_version_reaches()
    {
        await using var catalog = await OpenAsync();
        var a1 = Entry("a", 1, "h1");
        var b = Entry("b", 1, "hb");
        var v1 = Version(1, a1, b);
        var v2 = Version(2, a1 with { Length = 2 }, b);          // a changes at 2
        var v3 = Version(3, a1 with { Length = 3 }, b);          // a changes at 3
        foreach (var index in new[] { v1, v2, v3 })
            await ImportAsync(catalog, index);
        Assert.Equal(4, await CountAsync(catalog, "SELECT COUNT(*) FROM entries"));   // a: [1,2) [2,3) [3,∞); b: [1,∞)

        await catalog.RemoveVersionAsync(2, CancellationToken.None);                   // the middle
        Assert.Equal(3, await CountAsync(catalog, "SELECT COUNT(*) FROM entries"));   // a's [2,3) is unreachable
        Assert.Equal(Bytes(v1), await SerializeAsync(catalog, 1));
        Assert.Equal(Bytes(v3), await SerializeAsync(catalog, 3));

        await catalog.RemoveVersionAsync(3, CancellationToken.None);                   // the newest
        Assert.Equal(2, await CountAsync(catalog, "SELECT COUNT(*) FROM entries"));   // a's [3,∞) goes; b stays as [1,∞)
        Assert.Equal(Bytes(v1), await SerializeAsync(catalog, 1));

        await catalog.RemoveVersionAsync(1, CancellationToken.None);
        Assert.Equal(0, await CountAsync(catalog, "SELECT COUNT(*) FROM entries"));
        Assert.Equal(0, await CountAsync(catalog, "SELECT COUNT(*) FROM dirs"));
    }

    [Fact]
    public async Task A_version_re_imported_in_the_middle_of_the_history_lands_between_its_neighbours()
    {
        await using var catalog = await OpenAsync();
        var a = Entry("a", 1, "h1");
        var v1 = Version(1, a);
        var v2 = Version(2, a with { Length = 2 });
        var v3 = Version(3, a with { Length = 2 });              // unchanged from 2
        foreach (var index in new[] { v1, v2, v3 })
            await ImportAsync(catalog, index);
        Assert.Equal(2, await CountAsync(catalog, "SELECT COUNT(*) FROM entries"));   // [1,2) [2,∞)

        // A repair rewrote v2: re-import with different content. v1 and v3 must not change.
        var v2b = Version(2, a with { Length = 22 });
        await ImportAsync(catalog, v2b);
        Assert.Equal(Bytes(v1), await SerializeAsync(catalog, 1));
        Assert.Equal(Bytes(v2b), await SerializeAsync(catalog, 2));
        Assert.Equal(Bytes(v3), await SerializeAsync(catalog, 3));
        Assert.Equal(3, await CountAsync(catalog, "SELECT COUNT(*) FROM entries"));   // [1,2) [2,3) [3,∞)
    }

    [Fact]
    public async Task A_duplicate_path_inside_one_version_keeps_the_first_and_records_the_issue()
    {
        await using var catalog = await OpenAsync();
        var index = new VersionIndex { Version = 1, Entries = [Entry("a", 1, "h1"), Entry("a", 2, "h2"), Entry("b", 1, "hb")] };
        await ImportAsync(catalog, index);
        Assert.Equal(2, (await catalog.GetVersionAsync(1, CancellationToken.None))!.EntryCount);
        Assert.Equal(1, (await catalog.GetEntryAsync(1, "a", CancellationToken.None))!.Length);
        Assert.Equal([("a", "duplicate")], await catalog.ImportIssuesAsync(1, CancellationToken.None));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~CatalogV2Tests"`
Expected: `A_fresh_catalog_is_format_2` fails on `user_version` (0) and `entries_seq` (present); the others fail on row counts.

- [ ] **Step 3: Replace the schema in `CatalogSql.cs`**

Replace the `Schema`, `GlobalIndexSchema` and `EnsureSchema` members, and add the constants:

```csharp
    /// <summary>The catalog file format this build writes; <c>PRAGMA user_version</c> carries it. A file at 0 is
    /// format 1 (one row per version per path, an <c>entries.seq</c> column) and is converted by
    /// <see cref="CatalogUpgrade"/> on its first write open.</summary>
    public const int Format = 2;

    /// <summary>The <c>version_to</c> of a row that is still current: the interval <c>[version_from, OpenEnd)</c>
    /// covers every version from <c>version_from</c> on.</summary>
    public const int OpenEnd = int.MaxValue;

    public static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Schema;
        command.ExecuteNonQuery();
    }

    /// <summary>Stamps the file as this build's format. Separate from <see cref="EnsureSchema"/> because the upgrade
    /// creates the v2 tables first and may be killed before the last version is in; the stamp is the last thing it
    /// writes, and a file without it is resumed, not trusted.</summary>
    public static void MarkCurrent(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {Format}";
        command.ExecuteNonQuery();
    }

    public static int FormatOf(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public static bool HasTable(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n";
        command.Parameters.AddWithValue("@n", name);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// One <c>entries</c> row is one path's complete entry together with the half-open version interval
    /// <c>[version_from, version_to)</c> over which that entry is what the path looked like — a path unchanged over
    /// fifty versions is one row, a path modified at every version is one row per version. The table is an ordinary
    /// rowid table so that every secondary index carries an 8-byte pointer rather than a copy of the path: on the
    /// previous <c>WITHOUT ROWID</c> layout the eight indexes were 1,150 of a row's 1,765 bytes (measured on the
    /// production schema, 68-character paths). <c>(path, version_from)</c> is unique — a version starts at most one
    /// row per path. <c>path_key</c> (UTF-16BE bytes, see <see cref="PathKey"/>) is the ordinal order both diff
    /// cursors walk; <c>dirs</c> carries it too, so the import's directory merge walks in the same order as its
    /// entry merge. The three content-keyed indexes are unchanged in meaning (see <see cref="GlobalIndexNames"/>);
    /// on this layout a version inserts only its changes into them.
    /// </summary>
    private const string Schema = $"""
        CREATE TABLE IF NOT EXISTS versions (
          version INTEGER PRIMARY KEY, identity INTEGER NOT NULL, entry_count INTEGER NOT NULL, imported_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS entries (
          id INTEGER PRIMARY KEY, version_from INTEGER NOT NULL, version_to INTEGER NOT NULL,
          parent TEXT NOT NULL, path_fold TEXT NOT NULL, path_key BLOB NOT NULL,
          {EntryRowMapper.ColumnDefinitions}, unrecoverable INTEGER NOT NULL DEFAULT 0);
        CREATE UNIQUE INDEX IF NOT EXISTS entries_path_from ON entries (path, version_from);
        CREATE INDEX IF NOT EXISTS entries_path_key ON entries (path_key);
        CREATE INDEX IF NOT EXISTS entries_parent   ON entries (parent, path_key);
        CREATE INDEX IF NOT EXISTS entries_fold     ON entries (path_fold);
        CREATE INDEX IF NOT EXISTS entries_to       ON entries (version_to);
        {GlobalIndexSchema}
        CREATE TABLE IF NOT EXISTS dirs (
          id INTEGER PRIMARY KEY, version_from INTEGER NOT NULL, version_to INTEGER NOT NULL,
          path TEXT NOT NULL, parent TEXT NOT NULL, path_key BLOB NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS dirs_path_from ON dirs (path, version_from);
        CREATE INDEX IF NOT EXISTS dirs_parent ON dirs (parent, path_key);
        CREATE INDEX IF NOT EXISTS dirs_to ON dirs (version_to);
        CREATE TABLE IF NOT EXISTS empty_dirs (version INTEGER NOT NULL, path TEXT NOT NULL, seq INTEGER NOT NULL, PRIMARY KEY (version, path)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS unrecoverable (version INTEGER NOT NULL, path TEXT NOT NULL, seq INTEGER NOT NULL, PRIMARY KEY (version, path)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS import_issues (version INTEGER NOT NULL, path TEXT NOT NULL, issue TEXT NOT NULL, PRIMARY KEY (version, path, issue)) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS entry_order (version INTEGER NOT NULL, seq INTEGER NOT NULL, path TEXT NOT NULL, PRIMARY KEY (version, seq)) WITHOUT ROWID;
        """;

    internal static readonly IReadOnlyList<string> GlobalIndexNames = ["entries_content", "entries_ref", "entries_head"];

    /// <summary><c>entries_ref</c> leads with the ref and carries the kind and the path order, so "every row of this
    /// object" and "this version's rows grouped by object" both come off it.</summary>
    internal const string GlobalIndexSchema = """
        CREATE INDEX IF NOT EXISTS entries_content  ON entries (full_hash, length);
        CREATE INDEX IF NOT EXISTS entries_ref      ON entries (storage_ref, storage_kind, path_key);
        CREATE INDEX IF NOT EXISTS entries_head     ON entries (length, head_hash);
        """;
```

Keep `DropGlobalIndexesSql` as it is. Update the doc comment on `GlobalIndexNames` to say that on the v2 layout a version inserts only its changes, and the bracket remains for `EnsureVersionsAsync`'s multi-version migration only.

- [ ] **Step 4: Rewrite `VersionCatalog.cs`'s constants, open, serialization, patches**

Replace the SQL constants at the top of the class with:

```csharp
    private const string SelectVersionSql = "SELECT version, identity, entry_count, imported_at FROM versions WHERE version=@v";
    private const string SelectVersionsSql = "SELECT version, identity, entry_count, imported_at FROM versions ORDER BY version";
    private const string SelectEntryCountSql = "SELECT entry_count FROM versions WHERE version=@v";
    private const string SelectNextPresentSql = "SELECT MIN(version) FROM versions WHERE version > @v";
    private const string SelectMaxPresentSql = "SELECT MAX(version) FROM versions";

    private const string UpsertVersionSql = """
        INSERT INTO versions (version, identity, entry_count, imported_at) VALUES (@v, @identity, @count, @at)
          ON CONFLICT (version) DO UPDATE SET identity=@identity, entry_count=@count, imported_at=@at
        """;

    private const string DeleteVersionSql = "DELETE FROM versions WHERE version=@v";

    /// <summary>A row is reachable when some retained version lies in its interval. After a version is dropped, the
    /// rows nothing reaches go. Only rows that end at or before the newest retained version, or start after it, can
    /// be unreachable: a row spanning the newest version is reached by it. The two disjuncts are separate
    /// statements so each can use an index.</summary>
    private const string DeleteUnreachableEntriesSql = """
        DELETE FROM entries WHERE version_to <= @max
          AND NOT EXISTS (SELECT 1 FROM versions v WHERE v.version >= entries.version_from AND v.version < entries.version_to);
        DELETE FROM entries WHERE version_from > @max;
        DELETE FROM dirs WHERE version_to <= @max
          AND NOT EXISTS (SELECT 1 FROM versions v WHERE v.version >= dirs.version_from AND v.version < dirs.version_to);
        DELETE FROM dirs WHERE version_from > @max;
        """;

    private const string DeletePerVersionRowsSql = """
        DELETE FROM empty_dirs WHERE version=@v;
        DELETE FROM unrecoverable WHERE version=@v;
        DELETE FROM import_issues WHERE version=@v;
        DELETE FROM entry_order WHERE version=@v;
        """;

    private const string InsertEmptyDirSql = "INSERT OR IGNORE INTO empty_dirs (version, path, seq) VALUES (@v, @path, @seq)";
    private const string InsertIssueSql = "INSERT OR IGNORE INTO import_issues (version, path, issue) VALUES (@v, @path, @issue)";
    private const string InsertUnrecoverableListSql = "INSERT OR IGNORE INTO unrecoverable (version, path, seq) VALUES (@v, @path, @seq)";

    /// <summary>Appends at the end of the version's list, reproducing the <c>List.Add</c> order the check and repair
    /// flows write their unrecoverable paths in — that order is part of the index's bytes.</summary>
    private const string AppendUnrecoverableListSql = """
        INSERT OR IGNORE INTO unrecoverable (version, path, seq)
          VALUES (@v, @path, (SELECT COALESCE(MAX(seq), -1) + 1 FROM unrecoverable WHERE version=@v))
        """;
    private const string RemoveUnrecoverableListSql = "DELETE FROM unrecoverable WHERE version=@v AND path=@path";

    private const string SelectCoveringRowSql =
        "SELECT id, version_from, version_to FROM entries WHERE path=@path AND version_from <= @v AND version_to > @v";
    private const string ShrinkRowToVersionSql = "UPDATE entries SET version_from=@v, version_to=@v + 1 WHERE id=@id";
    private const string CopyRowSql = $"""
        INSERT INTO entries (version_from, version_to, parent, path_fold, path_key, {EntryRowMapper.Columns}, unrecoverable)
        SELECT @from, @to, parent, path_fold, path_key, {EntryRowMapper.Columns}, unrecoverable FROM entries WHERE id=@id
        """;
    private const string PatchUnreadableSql =
        "UPDATE entries SET unreadable_ticks=@unreadable_ticks, unreadable_offset=@unreadable_offset WHERE id=@id";
    private const string PatchStorageSql =
        "UPDATE entries SET storage_kind=@storage_kind, storage_ref=@storage_ref, entry_name=@entry_name, " +
        "volumes=@volumes, raw=@raw, volume_sizes=@volume_sizes WHERE id=@id";
    private const string PatchUnrecoverableFlagSql = "UPDATE entries SET unrecoverable=@flag WHERE id=@id";

    private const string SelectEmptyDirsSql = "SELECT path FROM empty_dirs WHERE version=@v ORDER BY seq";
    private const string SelectUnrecoverableSql = "SELECT path FROM unrecoverable WHERE version=@v ORDER BY seq";
    private const string HasEntryOrderSql = "SELECT EXISTS (SELECT 1 FROM entry_order WHERE version=@v)";
    private const string SelectEntriesByOrderSql = $"""
        SELECT {EntryRowMapper.Columns} FROM entry_order o
        JOIN entries e ON e.path = o.path AND e.version_from <= @v AND e.version_to > @v
        WHERE o.version=@v ORDER BY o.seq
        """;
```

Change `OpenAsync` so a fresh file is stamped and a legacy file is neither given the v2 schema nor opened read-only:

```csharp
    public static async Task<VersionCatalog> OpenAsync(string path, bool readOnly, CancellationToken ct)
    {
        var connection = new SqliteConnection(CatalogSql.ConnectionString(path, readOnly));
        try
        {
            await connection.OpenAsync(ct);
            CatalogSql.ApplyPragmas(connection, readOnly);
            var legacy = CatalogSql.FormatOf(connection) < CatalogSql.Format
                && (CatalogSql.HasTable(connection, "v1_entries") || CatalogSql.HasTable(connection, "entries"));
            if (legacy && readOnly)
                throw new CatalogFormatException(path);
            if (!readOnly && !legacy)
            {
                CatalogSql.EnsureSchema(connection);
                CatalogSql.MarkCurrent(connection);
            }
            return new VersionCatalog(path, connection) { NeedsUpgrade = legacy };
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>True when the file is format 1 (or a conversion was interrupted): the schema has not been applied
    /// and every query would fail. <see cref="VersionCatalogStore"/> runs <see cref="CatalogUpgrade"/> before
    /// handing such a catalog out; nothing else opens one.</summary>
    public bool NeedsUpgrade { get; private init; }
```

Add, in a new small file or at the bottom of `VersionCatalog.cs`:

```csharp
/// <summary>A read-only open met a format-1 catalog. Readers cannot convert (they hold no write lock), so the
/// store answers this by taking the lock, converting, and opening again.</summary>
public sealed class CatalogFormatException(string path) : IOException($"Catalog '{path}' is in an older format and has to be upgraded by a write open first.")
{
    public string Path { get; } = path;
}
```

Replace `SerializeVersionAsync`'s entry loop:

```csharp
        var ordered = await ExistsAsync(HasEntryOrderSql, ct, ("@v", version));
        await foreach (var entry in QueryEntriesAsync(ordered ? SelectEntriesByOrderSql : SelectEntriesByPathSql, version, ct))
            writer.WriteEntry(byPath is not null && byPath.TryGetValue(entry.Path, out var patch) ? Apply(entry, patch) : entry);
```

Replace `ApplyPatchesAsync`'s body with isolation:

```csharp
        await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
        _transaction = transaction;
        try
        {
            using var unreadable = Command(PatchUnreadableSql);
            using var storage = Command(PatchStorageSql);
            using var flag = Command(PatchUnrecoverableFlagSql);
            using var append = Command(AppendUnrecoverableListSql);
            using var remove = Command(RemoveUnrecoverableListSql);
            foreach (var patch in patches)
            {
                ct.ThrowIfCancellationRequested();
                // The row that covers (version, path) may span other versions; the patch must not leak into them.
                // A path the version does not have gets no row and no patch, as the old UPDATE … WHERE affected none.
                var id = await IsolateAsync(patch.Version, patch.Path, ct);
                if (id is null)
                    continue;

                if (patch.UnreadableAt is { } at)
                {
                    Set(unreadable, "@id", id);
                    Set(unreadable, "@unreadable_ticks", at.UtcTicks);
                    Set(unreadable, "@unreadable_offset", (int)at.Offset.TotalMinutes);
                    await unreadable.ExecuteNonQueryAsync(ct);
                }

                if (patch.Storage is not null)
                {
                    Set(storage, "@id", id);
                    EntryRowMapper.BindStorage(storage, patch.Storage);
                    await storage.ExecuteNonQueryAsync(ct);
                }

                if (patch.Unrecoverable is { } on)
                {
                    Set(flag, "@id", id);
                    Set(flag, "@flag", on ? 1 : 0);
                    await flag.ExecuteNonQueryAsync(ct);
                    var list = on ? append : remove;
                    Set(list, "@v", patch.Version);
                    Set(list, "@path", patch.Path);
                    await list.ExecuteNonQueryAsync(ct);
                }
            }

            await transaction.CommitAsync(ct);
        }
        finally
        {
            _transaction = null;
        }
```

And the isolation helper in the plumbing section:

```csharp
    /// <summary>
    /// The row that covers (<paramref name="version"/>, <paramref name="path"/>), narrowed to that one version: a row
    /// <c>[a, b)</c> with <c>a &lt; version</c> or <c>b &gt; version + 1</c> is split into up to three — the outer
    /// pieces keep the old values under new ids, the original becomes <c>[version, version + 1)</c> and is the one
    /// returned. Splits are never merged back; a repaired path gains at most two rows per patch. Null when the
    /// version has no entry at the path. The original is shrunk before the copies are inserted, or the copy that
    /// keeps <c>version_from = a</c> would collide with it on the unique <c>(path, version_from)</c>.
    /// </summary>
    private async Task<long?> IsolateAsync(int version, string path, CancellationToken ct)
    {
        long id; int from, to;
        using (var find = Command(SelectCoveringRowSql))
        {
            Set(find, "@path", path);
            Set(find, "@v", version);
            await using var reader = (SqliteDataReader)await find.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;
            id = reader.GetInt64(0);
            from = reader.GetInt32(1);
            to = reader.GetInt32(2);
        }

        if (from == version && to == version + 1)
            return id;

        using (var shrink = Command(ShrinkRowToVersionSql))
        {
            Set(shrink, "@id", id);
            Set(shrink, "@v", version);
            await shrink.ExecuteNonQueryAsync(ct);
        }

        using var copy = Command(CopyRowSql);
        Set(copy, "@id", id);
        if (from < version)
        {
            Set(copy, "@from", from);
            Set(copy, "@to", version);
            await copy.ExecuteNonQueryAsync(ct);
        }
        if (to > version + 1)
        {
            Set(copy, "@from", version + 1);
            Set(copy, "@to", to);
            await copy.ExecuteNonQueryAsync(ct);
        }

        return id;
    }

    internal async Task<int?> NextPresentAsync(int version, CancellationToken ct)
    {
        using var command = Command(SelectNextPresentSql);
        Set(command, "@v", version);
        return await command.ExecuteScalarAsync(ct) is long next ? (int)next : null;
    }

    private async Task<int> MaxPresentAsync(CancellationToken ct)
    {
        using var command = Command(SelectMaxPresentSql);
        return await command.ExecuteScalarAsync(ct) is long max ? (int)max : -1;
    }
```

Delete from `VersionCatalog.cs`: `DeleteVersionRowsSql`, `InsertEntrySql`, `InsertDirSql`, `MarkUnrecoverableSql`, `AddUnrecoverableSql`, `ClearUnrecoverableSql`, `SelectEntriesBySeqSql`, the two `ImportVersionAsync` overloads, `ImportCoreAsync`, `ImportOnThisThreadAsync`, `RemoveVersionAsync`, `DeleteVersionRowsAsync`, `InsertAncestorsAsync`, `ToAsync` — they move to the new file. Keep `EntryProgressEvery`, `OffThePoolAsync`, `PrefersRebuild`, `HistoryRowsAsync`, `DropGlobalIndexesAsync`, `RebuildGlobalIndexesAsync`, `GlobalIndexCountAsync`, `QuickCheckAsync`, `RecordIssueAsync`, `ParentOf`, `EntryCountAsync`, `StringsAsync`, `QueryEntriesAsync`, `Command`, `Set`, `ReadVersion`, `UpsertVersionAsync`.

- [ ] **Step 5: Create `VersionCatalog.Import.cs` with the merge import and removal**

```csharp
using System.Runtime.CompilerServices;
using AzureStorageBackup.Api.Models;
using Microsoft.Data.Sqlite;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The write side of the catalog: a version goes in as a merge against the rows current at its predecessor, so what
/// is written is the version's changes, not its entries. See docs/storage-format.md, "One row per path per change".
/// </summary>
public sealed partial class VersionCatalog
{
    /// <summary>Rows that cover the version being imported, in path order. <c>id &lt;= @maxId</c> keeps the rows the
    /// import itself inserts out of its own cursor: SQLite makes no promise about whether a cursor sees rows added
    /// to the table it is walking, and a new <c>[N, next)</c> row seen again would be closed as "gone from N".</summary>
    private const string SelectCoveringSql = $"""
        SELECT id, version_from, version_to, unrecoverable, path_key, {EntryRowMapper.Columns} FROM entries
        WHERE version_from <= @v AND version_to > @v AND id <= @maxId ORDER BY path_key
        """;
    private const string SelectCoveringDirsSql =
        "SELECT id, version_from, version_to, path, path_key FROM dirs WHERE version_from <= @v AND version_to > @v AND id <= @maxId ORDER BY path_key";
    private const string MaxEntryIdSql = "SELECT COALESCE(MAX(id), 0) FROM entries";
    private const string MaxDirIdSql = "SELECT COALESCE(MAX(id), 0) FROM dirs";

    private const string InsertEntrySql = $"""
        INSERT INTO entries (version_from, version_to, parent, path_fold, path_key, {EntryRowMapper.Columns}, unrecoverable)
        VALUES (@version_from, @version_to, @parent, @path_fold, @path_key, {EntryRowMapper.Parameters}, @unrecoverable)
        """;
    private const string CloseEntrySql = "UPDATE entries SET version_to=@v WHERE id=@id";
    private const string StartEntryAtSql = "UPDATE entries SET version_from=@v WHERE id=@id";
    private const string DeleteEntrySql = "DELETE FROM entries WHERE id=@id";

    private const string InsertDirSql =
        "INSERT INTO dirs (version_from, version_to, path, parent, path_key) VALUES (@version_from, @version_to, @path, @parent, @path_key)";
    private const string CloseDirSql = "UPDATE dirs SET version_to=@v WHERE id=@id";
    private const string StartDirAtSql = "UPDATE dirs SET version_from=@v WHERE id=@id";
    private const string DeleteDirSql = "DELETE FROM dirs WHERE id=@id";
    private const string CopyDirSql = "INSERT INTO dirs (version_from, version_to, path, parent, path_key) SELECT @from, @to, path, parent, path_key FROM dirs WHERE id=@id";

    private const string CreateStageSql = $"""
        CREATE TEMP TABLE IF NOT EXISTS import_stage (seq INTEGER PRIMARY KEY, path_key BLOB NOT NULL, {EntryRowMapper.ColumnDefinitions});
        CREATE INDEX IF NOT EXISTS temp.import_stage_key ON import_stage (path_key);
        DELETE FROM import_stage;
        """;
    private const string InsertStageSql = $"INSERT INTO import_stage (seq, path_key, {EntryRowMapper.Columns}) VALUES (@seq, @path_key, {EntryRowMapper.Parameters})";
    private const string SelectStageSql = $"SELECT {EntryRowMapper.Columns} FROM import_stage ORDER BY path_key, seq";
    private const string SelectStageOrderSql = "INSERT INTO entry_order (version, seq, path) SELECT @v, seq, path FROM import_stage ORDER BY seq";
    private const string DropStageSql = "DELETE FROM import_stage";

    /// <summary>How often <c>onEntries</c> hears from an import, in rows.</summary>
    internal const int EntryProgressEvery = 10_000;

    /// <summary>Imports a version's index straight off the stream it was downloaded as. Two passes: the lists at the
    /// tail first (the unrecoverable flag is part of a row and has to be known while the entries stream), then the
    /// entries, merged against the rows current at the previous version. Requires a seekable stream.</summary>
    public Task ImportVersionAsync(int version, long identity, IndexStreamReader reader, CancellationToken ct, Action<long>? onEntries = null) =>
        OffThePoolAsync(() => ImportFromReaderAsync(version, identity, reader, onEntries, ct), ct);

    private async Task ImportFromReaderAsync(int version, long identity, IndexStreamReader reader, Action<long>? onEntries, CancellationToken ct)
    {
        if (!reader.Input.CanSeek)
            throw new NotSupportedException("A catalog import needs a seekable index stream: the tail lists are read before the entries.");

        // Pass 1: skip the entries (checking their order on the way) and read the lists behind them.
        var ordered = true;
        byte[]? last = null;
        foreach (var entry in reader.Entries())
        {
            var key = CatalogSql.PathKey(entry.Path);
            if (last is not null && Compare(key, last) < 0)
                ordered = false;
            last = key;
        }
        var emptyDirs = reader.ReadEmptyDirs();
        var unrecoverable = reader.ReadUnrecoverable();

        // Pass 2: the entries, from the start.
        reader.Input.Position = 0;
        using var second = new IndexStreamReader(reader.Input);
        await ImportCoreAsync(version, identity, second.EntryCount, ToAsync(second.Entries()), emptyDirs, unrecoverable, ordered, onEntries, ct);
    }

    /// <summary>Imports a version from an in-memory enumeration. The entries need not be ordered: they are staged
    /// and merged in path order, and their own order is kept as the version's serialization order when it differs.</summary>
    public Task ImportVersionAsync(int version, long identity, int entryCount, IAsyncEnumerable<IndexEntry> entries,
        IReadOnlyList<string> emptyDirs, IReadOnlyList<string> unrecoverable, CancellationToken ct) =>
        OffThePoolAsync(() => ImportCoreAsync(version, identity, entryCount, entries, emptyDirs, unrecoverable, ordered: false, onEntries: null, ct), ct);

    /// <summary>The upgrade's door: entries already in path order, on the calling thread (the upgrade owns one).</summary>
    internal Task ImportOrderedAsync(int version, long identity, int entryCount, IAsyncEnumerable<IndexEntry> entries,
        IReadOnlyList<string> emptyDirs, IReadOnlyList<string> unrecoverable, Action<long>? onEntries, CancellationToken ct) =>
        ImportCoreAsync(version, identity, entryCount, entries, emptyDirs, unrecoverable, ordered: true, onEntries, ct);

    /// <param name="ordered">Whether <paramref name="entries"/> come in ascending <see cref="CatalogSql.PathKey"/>
    /// order. If not, they are staged into a temp table, merged from it in path order, and their given order is
    /// recorded in <c>entry_order</c> so <see cref="SerializeVersionAsync"/> reproduces it.</param>
    private async Task ImportCoreAsync(int version, long identity, int expected, IAsyncEnumerable<IndexEntry> entries,
        IReadOnlyList<string> emptyDirs, IReadOnlyList<string> unrecoverable, bool ordered, Action<long>? onEntries, CancellationToken ct)
    {
        await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
        _transaction = transaction;
        try
        {
            // Re-importing a version replaces it wholesale: a repair rewrites an index in place. Its rows go the way
            // retention takes them, and what other versions reach stays.
            if (await GetVersionAsync(version, ct) is not null)
                await RemoveVersionCoreAsync(version, ct);

            var source = entries;
            if (!ordered)
            {
                var seen = await StageAsync(entries, ct);
                if (seen != expected)
                    throw new InvalidOperationException($"Version {version} announced {expected} entries but produced {seen}.");
                using (var order = Command(SelectStageOrderSql))
                {
                    Set(order, "@v", version);
                    await order.ExecuteNonQueryAsync(ct);
                }
                source = QueryStageAsync(ct);
            }

            var kept = await MergeEntriesAsync(version, expected, source, unrecoverable, ordered, onEntries, ct);
            await MergeDirsAsync(version, ct);

            using var emptyDir = Command(InsertEmptyDirSql);
            for (var i = 0; i < emptyDirs.Count; i++)
            {
                Set(emptyDir, "@v", version);
                Set(emptyDir, "@path", emptyDirs[i]);
                Set(emptyDir, "@seq", i);
                await emptyDir.ExecuteNonQueryAsync(ct);
            }

            using var list = Command(InsertUnrecoverableListSql);
            for (var i = 0; i < unrecoverable.Count; i++)
            {
                Set(list, "@v", version);
                Set(list, "@path", unrecoverable[i]);
                Set(list, "@seq", i);
                await list.ExecuteNonQueryAsync(ct);
            }

            if (!ordered)
            {
                using var drop = Command(DropStageSql);
                await drop.ExecuteNonQueryAsync(ct);
            }

            await UpsertVersionAsync(version, identity, kept, ct);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            _transaction = null;
        }
    }

    /// <summary>The directories of the version being merged, gathered as its entries go by (every prefix of every
    /// path, and the empty directories themselves), then reconciled against the <c>dirs</c> rows covering the
    /// version. In memory: a version's directories are a small fraction of its paths.</summary>
    private readonly SortedSet<string> _mergeDirs = new(StringComparer.Ordinal);

    private async Task<int> MergeEntriesAsync(int version, int expected, IAsyncEnumerable<IndexEntry> entries,
        IReadOnlyList<string> unrecoverable, bool checkOrder, Action<long>? onEntries, CancellationToken ct)
    {
        var next = await NextPresentAsync(version, ct) ?? CatalogSql.OpenEnd;
        var flagged = new HashSet<string>(unrecoverable, StringComparer.Ordinal);
        _mergeDirs.Clear();

        long maxId;
        using (var max = Command(MaxEntryIdSql))
            maxId = Convert.ToInt64(await max.ExecuteScalarAsync(ct));

        using var covering = Command(SelectCoveringSql);
        Set(covering, "@v", version);
        Set(covering, "@maxId", maxId);
        await using var rows = (SqliteDataReader)await covering.ExecuteReaderAsync(ct);
        var haveRow = await rows.ReadAsync(ct);

        using var insert = Command(InsertEntrySql);
        using var close = Command(CloseEntrySql);
        using var start = Command(StartEntryAtSql);
        using var delete = Command(DeleteEntrySql);
        using var copy = Command(CopyRowSql);

        var seen = 0;
        var kept = 0;
        byte[]? lastKey = null;
        await foreach (var entry in entries.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            seen++;
            if (onEntries is not null && seen % EntryProgressEvery == 0)
                onEntries(seen);

            var key = CatalogSql.PathKey(entry.Path);
            if (lastKey is not null)
            {
                var order = Compare(key, lastKey);
                if (order == 0)
                {
                    // The version names the same path twice: the first wins, the loss is recorded (it makes the
                    // version's serialization no longer byte-identical), exactly as the old primary key did.
                    await RecordIssueAsync(version, entry.Path, "duplicate", ct);
                    continue;
                }
                if (order < 0 && checkOrder)
                    throw new InvalidOperationException(
                        $"Version {version}'s entries are not in ascending ordinal path order: '{entry.Path}' came after the previous path.");
            }
            lastKey = key;
            AddDirsOf(entry.Path);

            // Everything current before this path is gone from the version.
            while (haveRow && Compare((byte[])rows["path_key"], key) < 0)
            {
                await CloseRowAsync(rows, version, next, close, start, delete, copy, ct);
                haveRow = await rows.ReadAsync(ct);
            }

            var flag = flagged.Contains(entry.Path);
            if (haveRow && Compare((byte[])rows["path_key"], key) == 0)
            {
                var current = EntryRowMapper.Read(rows);
                var currentFlag = rows.GetInt64(rows.GetOrdinal("unrecoverable")) != 0;
                if (!EntryRowMapper.SameEntry(current, entry) || currentFlag != flag)
                {
                    await CloseRowAsync(rows, version, next, close, start, delete, copy, ct);
                    await InsertRowAsync(insert, version, next, entry, flag, key, ct);
                }
                haveRow = await rows.ReadAsync(ct);
            }
            else
            {
                await InsertRowAsync(insert, version, next, entry, flag, key, ct);
            }

            kept++;
        }

        while (haveRow)
        {
            ct.ThrowIfCancellationRequested();
            await CloseRowAsync(rows, version, next, close, start, delete, copy, ct);
            haveRow = await rows.ReadAsync(ct);
        }

        if (seen != expected)
            throw new InvalidOperationException($"Version {version} announced {expected} entries but produced {seen}.");

        return kept;
    }

    /// <summary>
    /// The row no longer describes the version being imported. A row that started before it is closed at it; if
    /// it also reached past the next retained version, that tail is re-inserted under its own id, since those
    /// versions still see the old entry. A row that started exactly at this version (left by an earlier import of
    /// the same version, kept because a later version reaches it) is moved to start at the next version, or
    /// deleted when nothing past this version reaches it.
    /// </summary>
    private async Task CloseRowAsync(SqliteDataReader row, int version, int next,
        SqliteCommand close, SqliteCommand start, SqliteCommand delete, SqliteCommand copy, CancellationToken ct)
    {
        var id = row.GetInt64(0);
        var from = row.GetInt32(1);
        var to = row.GetInt32(2);
        if (from == version)
        {
            if (to > next)
            {
                Set(start, "@id", id);
                Set(start, "@v", next);
                await start.ExecuteNonQueryAsync(ct);
            }
            else
            {
                Set(delete, "@id", id);
                await delete.ExecuteNonQueryAsync(ct);
            }
            return;
        }

        Set(close, "@id", id);
        Set(close, "@v", version);
        await close.ExecuteNonQueryAsync(ct);
        if (to > next)
        {
            Set(copy, "@id", id);
            Set(copy, "@from", next);
            Set(copy, "@to", to);
            await copy.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task InsertRowAsync(SqliteCommand insert, int version, int next, IndexEntry entry, bool flag, byte[] key, CancellationToken ct)
    {
        Set(insert, "@version_from", version);
        Set(insert, "@version_to", next);
        Set(insert, "@parent", ParentOf(entry.Path));
        Set(insert, "@path_fold", entry.Path.ToUpperInvariant());
        Set(insert, "@path_key", key);
        Set(insert, "@unrecoverable", flag ? 1 : 0);
        EntryRowMapper.Bind(insert, entry);
        await insert.ExecuteNonQueryAsync(ct);
    }

    private void AddDirsOf(string path)
    {
        for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
            if (slash > 0)
                _mergeDirs.Add(path[..slash]);
    }

    /// <summary>The same merge for directories: the set gathered by <see cref="MergeEntriesAsync"/> plus the empty
    /// directories, against the <c>dirs</c> rows covering the version. Directories carry no content, so "same"
    /// is "present on both sides".</summary>
    private async Task MergeDirsAsync(int version, CancellationToken ct)
    {
        var next = await NextPresentAsync(version, ct) ?? CatalogSql.OpenEnd;
        long maxId;
        using (var max = Command(MaxDirIdSql))
            maxId = Convert.ToInt64(await max.ExecuteScalarAsync(ct));

        using var covering = Command(SelectCoveringDirsSql);
        Set(covering, "@v", version);
        Set(covering, "@maxId", maxId);
        await using var rows = (SqliteDataReader)await covering.ExecuteReaderAsync(ct);
        var haveRow = await rows.ReadAsync(ct);

        using var insert = Command(InsertDirSql);
        using var close = Command(CloseDirSql);
        using var start = Command(StartDirAtSql);
        using var delete = Command(DeleteDirSql);
        using var copy = Command(CopyDirSql);

        foreach (var dir in _mergeDirs)
        {
            var key = CatalogSql.PathKey(dir);
            while (haveRow && Compare((byte[])rows["path_key"], key) < 0)
            {
                await CloseRowAsync(rows, version, next, close, start, delete, copy, ct);
                haveRow = await rows.ReadAsync(ct);
            }
            if (haveRow && Compare((byte[])rows["path_key"], key) == 0)
            {
                haveRow = await rows.ReadAsync(ct);
                continue;
            }
            Set(insert, "@version_from", version);
            Set(insert, "@version_to", next);
            Set(insert, "@path", dir);
            Set(insert, "@parent", ParentOf(dir));
            Set(insert, "@path_key", key);
            await insert.ExecuteNonQueryAsync(ct);
        }

        while (haveRow)
        {
            await CloseRowAsync(rows, version, next, close, start, delete, copy, ct);
            haveRow = await rows.ReadAsync(ct);
        }

        _mergeDirs.Clear();
    }

    /// <summary>Empty directories are browsable nodes too: their prefixes and themselves join the directory set
    /// before the directory merge. Called by <see cref="ImportCoreAsync"/> before <see cref="MergeDirsAsync"/>.</summary>
    private void AddEmptyDirs(IReadOnlyList<string> emptyDirs)
    {
        foreach (var dir in emptyDirs)
        {
            AddDirsOf(dir);
            _mergeDirs.Add(dir);
        }
    }

    private async Task<int> StageAsync(IAsyncEnumerable<IndexEntry> entries, CancellationToken ct)
    {
        using (var create = Command(CreateStageSql))
            await create.ExecuteNonQueryAsync(ct);
        using var insert = Command(InsertStageSql);
        var seq = 0;
        await foreach (var entry in entries.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            Set(insert, "@seq", seq++);
            Set(insert, "@path_key", CatalogSql.PathKey(entry.Path));
            EntryRowMapper.Bind(insert, entry);
            await insert.ExecuteNonQueryAsync(ct);
        }
        return seq;
    }

    private async IAsyncEnumerable<IndexEntry> QueryStageAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var command = Command(SelectStageSql);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return EntryRowMapper.Read(reader);
    }

    public async Task RemoveVersionAsync(int version, CancellationToken ct)
    {
        await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
        _transaction = transaction;
        try
        {
            await RemoveVersionCoreAsync(version, ct);
            await transaction.CommitAsync(ct);
        }
        finally
        {
            _transaction = null;
        }
    }

    /// <summary>Inside a transaction: the version row goes first, so "reachable" below is judged against what
    /// remains; then every entry and directory row nothing remaining reaches; then the per-version tables.</summary>
    private async Task RemoveVersionCoreAsync(int version, CancellationToken ct)
    {
        using (var drop = Command(DeleteVersionSql))
        {
            Set(drop, "@v", version);
            await drop.ExecuteNonQueryAsync(ct);
        }
        using (var unreachable = Command(DeleteUnreachableEntriesSql))
        {
            Set(unreachable, "@max", await MaxPresentAsync(ct));
            await unreachable.ExecuteNonQueryAsync(ct);
        }
        using var rest = Command(DeletePerVersionRowsSql);
        Set(rest, "@v", version);
        await rest.ExecuteNonQueryAsync(ct);
    }

    private static int Compare(byte[] a, byte[] b) => a.AsSpan().SequenceCompareTo(b);

    private static async IAsyncEnumerable<IndexEntry> ToAsync(IEnumerable<IndexEntry> entries)
    {
        await Task.CompletedTask;
        foreach (var entry in entries)
            yield return entry;
    }
}
```

Wire `AddEmptyDirs(emptyDirs)` into `ImportCoreAsync` between `MergeEntriesAsync` and `MergeDirsAsync`. Note the duplicate-path case also skips `AddDirsOf` — the first occurrence already added them.

- [ ] **Step 6: Rewrite the queries in `VersionCatalog.Queries.cs`**

Replace every SQL constant:

```csharp
    private const string Current = "version_from <= @v AND version_to > @v";

    private const string SelectEntrySql = $"SELECT {EntryRowMapper.Columns} FROM entries WHERE path=@path AND {Current}";
    private const string SelectEntriesByPathSql = $"SELECT {EntryRowMapper.Columns} FROM entries WHERE {Current} ORDER BY path_key";
    private const string SelectEntriesByStorageSql =
        $"SELECT {EntryRowMapper.Columns} FROM entries WHERE {Current} ORDER BY storage_ref, storage_kind, path_key";
    private const string SelectChildDirsSql = $"""
        SELECT d.path,
               EXISTS (SELECT 1 FROM entries e WHERE e.parent=d.path AND e.version_from <= @v AND e.version_to > @v)
            OR EXISTS (SELECT 1 FROM dirs c WHERE c.parent=d.path AND c.version_from <= @v AND c.version_to > @v)
        FROM dirs d WHERE d.parent=@parent AND d.version_from <= @v AND d.version_to > @v ORDER BY d.path
        """;
    private const string SelectChildEntriesSql = $"SELECT {EntryRowMapper.Columns} FROM entries WHERE parent=@parent AND {Current} ORDER BY path";
    private const string SelectUnreadableSql =
        $"SELECT path, unreadable_ticks, unreadable_offset FROM entries WHERE {Current} AND unreadable_ticks IS NOT NULL ORDER BY path_key";
    private const string SelectStatsSql = $"SELECT COUNT(*), COALESCE(SUM(length), 0) FROM entries WHERE {Current}";
    private const string IsUnrecoverableSql = "SELECT EXISTS (SELECT 1 FROM unrecoverable WHERE version=@v AND path=@path)";
    private const string SelectComparableEntriesSql =
        $"SELECT {EntryRowMapper.Columns} FROM entries WHERE {Current} AND unreadable_ticks IS NULL ORDER BY path_key";
    private const string SelectCaseCollisionsSql = $"""
        SELECT path, @v FROM entries WHERE {Current} AND path_fold IN
          (SELECT path_fold FROM entries WHERE {Current} GROUP BY path_fold HAVING COUNT(*) > 1)
        ORDER BY path_fold, path
        """;
    private const string FindBlobByContentSql = """
        SELECT storage_ref, raw, volumes, volume_sizes FROM entries
        WHERE full_hash=@f AND length=@l AND head_hash IS @h AND tail_hash IS @t AND storage_kind='blob' AND unrecoverable=0
        ORDER BY version_from DESC LIMIT 1
        """;
    private const string FindRefOwnerSql = """
        SELECT full_hash, length, head_hash, tail_hash, unrecoverable FROM entries
        WHERE storage_ref=@r AND storage_kind='blob' AND full_hash IS NOT NULL
        ORDER BY unrecoverable ASC, CASE WHEN unrecoverable THEN version_from ELSE -version_from END ASC LIMIT 1
        """;
    private const string IsDamagedRefSql =
        "SELECT EXISTS (SELECT 1 FROM entries WHERE storage_ref=@r AND storage_kind='blob' AND unrecoverable=1 AND full_hash IS NOT NULL)";
    private const string HeadSeenSql =
        "SELECT EXISTS (SELECT 1 FROM entries WHERE length=@l AND head_hash=@h AND storage_kind='blob' AND unrecoverable=0 AND full_hash IS NOT NULL)";
    private const string FindPackMemberSql = """
        SELECT storage_ref, COALESCE(entry_name, path), tail_hash FROM entries
        WHERE full_hash=@f AND length=@l AND head_hash=@h AND storage_kind='pack' AND unrecoverable=0
        ORDER BY version_from ASC, path_key ASC LIMIT 1
        """;
    private const string SelectDistinctRefsSql =
        "SELECT DISTINCT storage_kind, storage_ref, volumes FROM entries WHERE storage_ref IS NOT NULL ORDER BY storage_kind, storage_ref, volumes";
    private const string SelectLivePackMembersSql = """
        SELECT storage_ref, COALESCE(entry_name, path), length, full_hash FROM entries
        WHERE storage_kind='pack' AND full_hash IS NOT NULL
        ORDER BY storage_ref, COALESCE(entry_name, path), version_from DESC
        """;
    /// <summary>One interval row stands for every version in its interval; the join with <c>versions</c> expands it
    /// back to the one-row-per-version shape repair reads.</summary>
    private const string SelectEntriesReferencingSql = $"""
        SELECT v.version, {EntryRowMapper.PrefixedColumns("e.")} FROM entries e
        JOIN versions v ON v.version >= e.version_from AND v.version < e.version_to
        WHERE e.storage_ref=@r ORDER BY v.version, e.path_key
        """;
    private const string SelectPackMembersSql = $"""
        SELECT v.version, {EntryRowMapper.PrefixedColumns("e.")} FROM entries e
        JOIN versions v ON v.version >= e.version_from AND v.version < e.version_to
        WHERE e.storage_kind='pack' AND e.storage_ref=@r ORDER BY v.version, e.path_key
        """;
    private const string SelectUnrecoverableAnyVersionSql = "SELECT DISTINCT path FROM unrecoverable ORDER BY path";
    private const string SelectImportIssuesSql = "SELECT path, issue FROM import_issues WHERE version=@v ORDER BY path";
    private const string SelectStorageGroupSizesSql = $"""
        SELECT storage_kind, storage_ref, NULL AS entry_name, volumes, raw, volume_sizes, SUM(length)
        FROM entries WHERE {Current} AND storage_kind IS NOT NULL AND unrecoverable=0
        GROUP BY storage_kind, storage_ref ORDER BY storage_kind, storage_ref
        """;
```

In `EntriesAtAsync`, the chunk query becomes `$"SELECT {EntryRowMapper.Columns} FROM entries WHERE {Current} AND path IN ({placeholders})"`. In `SampleAsync`, both interpolated statements use `WHERE {Current} AND unreadable_ticks IS NULL …` and the pick orders `ORDER BY path_key`. `RefsOnlyInAsync` becomes:

```csharp
        var list = string.Join(", ", versions.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        using var command = Command($"""
            SELECT e.storage_ref FROM entries e
            WHERE e.storage_kind=@k AND e.storage_ref IS NOT NULL
            GROUP BY e.storage_ref
            HAVING SUM(EXISTS (SELECT 1 FROM versions v WHERE v.version NOT IN ({list}) AND v.version >= e.version_from AND v.version < e.version_to)) = 0
               AND SUM(EXISTS (SELECT 1 FROM versions v WHERE v.version IN ({list}) AND v.version >= e.version_from AND v.version < e.version_to)) > 0
            ORDER BY e.storage_ref
            """);
```

with its doc comment updated: "refs whose every row is reachable only through these versions".

- [ ] **Step 7: Build, run the catalog test classes**

Run: `cd backend && dotnet build 2>&1 | grep -E " error |Build succeeded"`, then
`dotnet test --filter "FullyQualifiedName~CatalogV2Tests|FullyQualifiedName~VersionCatalogTests|FullyQualifiedName~VersionCatalogsMigrationTests|FullyQualifiedName~VersionCatalogStoreTests"`
Expected: all pass. Two existing tests need their premise updated, and the plan states how:
- `VersionCatalogTests.Import_reports_the_running_row_count_every_ten_thousand_rows` — unchanged (the merge reports at 10,000 and 20,000).
- `VersionCatalogStoreTests.BuildMultiPageCatalogAsync` uses the `IAsyncEnumerable` overload with 500 ordered entries — unchanged.
If a test reads `entries.seq` or `entries.version` directly, it is reading the old layout; rewrite it against the public API.

- [ ] **Step 8: Run the whole suite with Azurite**

```bash
systemctl --user start azurite && sleep 2 && (cd backend && dotnet test 2>&1 | grep -E "\[FAIL\]|Passed!|Failed!"); systemctl --user stop azurite; rm -rf ~/.local/share/azurite/{AzuriteConfig,__azurite_db_*,__*storage__}
```
Expected: `Failed: 0`. The checker, repairer, retention and restore tests exercise every query against real runs.

- [ ] **Step 9: Commit and merge**

```bash
git add -A
git commit -m "feat(catalog): format 2 — one row per path per change, merged in path order, every query on interval rows

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git checkout main && git merge --no-ff catalog-v2-equality -m "Merge catalog-v2-equality: catalog format 2" && git branch -d catalog-v2-equality
```

---

### Task 3: Patches isolate one version of a spanning row

**Files:**
- Test: `backend/tests/AzureStorageBackup.Api.Tests/CatalogV2Tests.cs`
- (Implementation landed in Task 2's `IsolateAsync`; this task proves it.)

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task A_patch_on_a_spanning_row_changes_only_its_version()
    {
        await using var catalog = await OpenAsync();
        var a = Entry("a", 1, "h1", new StorageRef { Kind = "blob", Ref = "data/old" });
        var v1 = Version(1, a); var v2 = Version(2, a); var v3 = Version(3, a);
        foreach (var index in new[] { v1, v2, v3 })
            await ImportAsync(catalog, index);
        Assert.Equal(1, await CountAsync(catalog, "SELECT COUNT(*) FROM entries"));    // one row spans [1,∞)

        await catalog.ApplyPatchesAsync(
            [new CatalogPatch(2, "a", UnreadableAt: null, Unrecoverable: true, Storage: new StorageRef { Kind = "blob", Ref = "data/new" })],
            CancellationToken.None);

        Assert.Equal(3, await CountAsync(catalog, "SELECT COUNT(*) FROM entries"));    // [1,2) [2,3) [3,∞)
        Assert.Equal("data/old", (await catalog.GetEntryAsync(1, "a", CancellationToken.None))!.Storage!.Ref);
        Assert.Equal("data/new", (await catalog.GetEntryAsync(2, "a", CancellationToken.None))!.Storage!.Ref);
        Assert.Equal("data/old", (await catalog.GetEntryAsync(3, "a", CancellationToken.None))!.Storage!.Ref);
        Assert.True(await catalog.IsUnrecoverableAsync(2, "a", CancellationToken.None));
        Assert.False(await catalog.IsUnrecoverableAsync(3, "a", CancellationToken.None));
        Assert.Equal(["a"], await catalog.UnrecoverableAsync(2, CancellationToken.None));
        Assert.Equal(Bytes(v1), await SerializeAsync(catalog, 1));
        Assert.Equal(Bytes(v3), await SerializeAsync(catalog, 3));

        // Damage marks flow to dedup exactly as before: the ref is damaged if any version says so.
        Assert.True(await catalog.IsDamagedRefAsync("data/new", CancellationToken.None));
        Assert.False(await catalog.IsDamagedRefAsync("data/old", CancellationToken.None));

        // Clearing the mark on the same version isolates nothing further (the row is already [2,3)).
        await catalog.ApplyPatchesAsync([new CatalogPatch(2, "a", null, Unrecoverable: false, null)], CancellationToken.None);
        Assert.Equal(3, await CountAsync(catalog, "SELECT COUNT(*) FROM entries"));
        Assert.Empty(await catalog.UnrecoverableAsync(2, CancellationToken.None));
    }
```

- [ ] **Step 2: Run it**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~A_patch_on_a_spanning_row"`
Expected: PASS if Task 2's `IsolateAsync` is right; otherwise fix `IsolateAsync` until it passes (the split order — shrink first, then copies — is the usual mistake).

- [ ] **Step 3: Commit**

```bash
git checkout -b catalog-v2-patches
git add backend/tests/AzureStorageBackup.Api.Tests/CatalogV2Tests.cs
git commit -m "test(catalog): a patch on a spanning row changes only its version

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git checkout main && git merge --no-ff catalog-v2-patches -m "Merge catalog-v2-patches" && git branch -d catalog-v2-patches
```

---

### Task 4: Legacy order — a version whose entries are not in path order serializes as it came

**Files:**
- Test: `backend/tests/AzureStorageBackup.Api.Tests/CatalogV2Tests.cs`
- Modify (only if the test fails): `backend/src/AzureStorageBackup.Api/Services/VersionCatalog.Import.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task A_version_out_of_path_order_is_stored_merged_and_serialized_in_its_own_order()
    {
        await using var catalog = await OpenAsync();
        var index = new VersionIndex { Version = 1, Entries = [Entry("b.bin", 5, "hb"), Entry("a.bin", 6, "ha"), Entry("a/c.bin", 7, "hc")] };
        await ImportAsync(catalog, index);                                              // pass 1 notices the order

        Assert.Equal(Bytes(index), await SerializeAsync(catalog, 1));                   // byte-identical, in the given order
        var walked = await catalog.EntriesAsync(1, CancellationToken.None).ToListAsync();
        Assert.Equal(["a.bin", "a/c.bin", "b.bin"], walked.Select(e => e.Path));       // the diff cursor still walks in path order
        Assert.Equal(3, await CountAsync(catalog, "SELECT COUNT(*) FROM entry_order WHERE version=1"));

        // The next version, in path order, merges against it and needs no order table.
        var v2 = Version(2, Entry("a.bin", 6, "ha"), Entry("a/c.bin", 7, "hc"), Entry("b.bin", 55, "hb2"));
        await ImportAsync(catalog, v2);
        Assert.Equal(Bytes(v2), await SerializeAsync(catalog, 2));
        Assert.Equal(0, await CountAsync(catalog, "SELECT COUNT(*) FROM entry_order WHERE version=2"));
        Assert.Equal(4, await CountAsync(catalog, "SELECT COUNT(*) FROM entries"));
    }
```

Note: ordinal order puts `a.bin` before `a/c.bin` because `.` (0x2E) sorts before `/` (0x2F).

- [ ] **Step 2: Run it**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~A_version_out_of_path_order"`
Expected: PASS with Task 2's staged path; if `entry_order` is empty, `ImportFromReaderAsync`'s order check is not reaching `ImportCoreAsync` with `ordered: false`.

- [ ] **Step 3: Commit**

```bash
git checkout -b catalog-v2-order
git add backend/tests/AzureStorageBackup.Api.Tests/CatalogV2Tests.cs backend/src/AzureStorageBackup.Api/Services/VersionCatalog.Import.cs
git commit -m "test(catalog): an out-of-order version keeps its order for serialization and path order for the cursor

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git checkout main && git merge --no-ff catalog-v2-order -m "Merge catalog-v2-order" && git branch -d catalog-v2-order
```

---

### Task 5: Converting a format-1 catalog in place, resumably, from the store

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/CatalogUpgrade.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/VersionCatalogStore.cs` (`OpenAsync`, `OpenCheckedAsync`, new `NeedsUpgrade`, `UpgradeAsync`)
- Modify: `backend/src/AzureStorageBackup.Api/Services/IVersionCatalogs.cs`, `VersionCatalogs.cs` (pass-through, `CatalogUpgradeProgress`)
- Create: `backend/tests/AzureStorageBackup.Api.Tests/LegacyCatalogFixture.cs`
- Create: `backend/tests/AzureStorageBackup.Api.Tests/CatalogUpgradeTests.cs`

**Interfaces:**
- Produces: `public readonly record struct CatalogUpgradeProgress(int Version, long RowsDone, long RowsTotal, bool VersionDone)` in `IVersionCatalogs.cs`.
- Produces: `bool IVersionCatalogs.NeedsUpgrade(int accountId, string container)` (default false) and `Task IVersionCatalogs.UpgradeAsync(int accountId, string container, IProgress<CatalogUpgradeProgress>? progress, CancellationToken ct)` (default completed).
- Produces: `VersionCatalogStore.NeedsUpgrade(int, string)`, `VersionCatalogStore.UpgradeAsync(int, string, IProgress<CatalogUpgradeProgress>?, CancellationToken)`.
- Produces: `internal static class CatalogUpgrade { static Task RunAsync(VersionCatalog catalog, IProgress<CatalogUpgradeProgress>? progress, CancellationToken ct); }`.

- [ ] **Step 1: Write the fixture that builds a format-1 file**

```csharp
// backend/tests/AzureStorageBackup.Api.Tests/LegacyCatalogFixture.cs
using System.Globalization;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;

namespace AzureStorageBackup.Api.Tests;

/// <summary>Writes a catalog in the format-1 layout (one row per version per path, <c>seq</c>, <c>WITHOUT ROWID</c>
/// keyed by (version, path)) exactly as 2026.9.13.1 wrote it, so the conversion has a real file to convert.</summary>
internal static class LegacyCatalogFixture
{
    private const string Schema = $"""
        PRAGMA journal_mode=WAL;
        CREATE TABLE versions (version INTEGER PRIMARY KEY, identity INTEGER NOT NULL, entry_count INTEGER NOT NULL, imported_at TEXT NOT NULL);
        CREATE TABLE entries (version INTEGER NOT NULL, seq INTEGER NOT NULL, parent TEXT NOT NULL, path_fold TEXT NOT NULL,
          path_key BLOB NOT NULL, {EntryRowMapper.ColumnDefinitions}, unrecoverable INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY (version, path)) WITHOUT ROWID;
        CREATE INDEX entries_seq ON entries (version, seq);
        CREATE INDEX entries_parent ON entries (version, parent);
        CREATE INDEX entries_fold ON entries (version, path_fold);
        CREATE INDEX entries_path_key ON entries (version, path_key);
        CREATE INDEX entries_storage ON entries (version, storage_kind, storage_ref, seq);
        CREATE INDEX entries_content ON entries (full_hash, length);
        CREATE INDEX entries_ref ON entries (storage_ref);
        CREATE INDEX entries_head ON entries (length, head_hash);
        CREATE TABLE dirs (version INTEGER NOT NULL, path TEXT NOT NULL, parent TEXT NOT NULL, PRIMARY KEY (version, path)) WITHOUT ROWID;
        CREATE INDEX dirs_parent ON dirs (version, parent);
        CREATE TABLE empty_dirs (version INTEGER NOT NULL, path TEXT NOT NULL, seq INTEGER NOT NULL, PRIMARY KEY (version, path)) WITHOUT ROWID;
        CREATE TABLE unrecoverable (version INTEGER NOT NULL, path TEXT NOT NULL, seq INTEGER NOT NULL, PRIMARY KEY (version, path)) WITHOUT ROWID;
        CREATE TABLE import_issues (version INTEGER NOT NULL, path TEXT NOT NULL, issue TEXT NOT NULL, PRIMARY KEY (version, path, issue)) WITHOUT ROWID;
        """;

    /// <summary>Each index's entries are inserted in the order given — the fixture deliberately does not sort, so a
    /// test can hand it a version whose seq order is not path order.</summary>
    public static async Task WriteAsync(string path, IReadOnlyList<VersionIndex> versions, long identity = 1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var connection = new SqliteConnection(CatalogSql.ConnectionString(path, readOnly: false));
        await connection.OpenAsync();
        using (var schema = connection.CreateCommand()) { schema.CommandText = Schema; schema.ExecuteNonQuery(); }

        foreach (var index in versions)
        {
            await using var tx = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = $"INSERT INTO entries (version, seq, parent, path_fold, path_key, {EntryRowMapper.Columns}, unrecoverable) " +
                                 $"VALUES (@version, @seq, @parent, @path_fold, @path_key, {EntryRowMapper.Parameters}, @unrecoverable)";
            using var dir = connection.CreateCommand();
            dir.Transaction = tx;
            dir.CommandText = "INSERT OR IGNORE INTO dirs (version, path, parent) VALUES (@v, @path, @parent)";
            var seq = 0;
            foreach (var e in index.Entries)
            {
                EntryRowMapper.Set(insert, "@version", index.Version);
                EntryRowMapper.Set(insert, "@seq", seq++);
                EntryRowMapper.Set(insert, "@parent", VersionCatalog.ParentOf(e.Path));
                EntryRowMapper.Set(insert, "@path_fold", e.Path.ToUpperInvariant());
                EntryRowMapper.Set(insert, "@path_key", CatalogSql.PathKey(e.Path));
                EntryRowMapper.Set(insert, "@unrecoverable", index.UnrecoverablePaths.Contains(e.Path) ? 1 : 0);
                EntryRowMapper.Bind(insert, e);
                insert.ExecuteNonQuery();
                for (var slash = e.Path.IndexOf('/'); slash > 0; slash = e.Path.IndexOf('/', slash + 1))
                {
                    var d = e.Path[..slash];
                    EntryRowMapper.Set(dir, "@v", index.Version); EntryRowMapper.Set(dir, "@path", d); EntryRowMapper.Set(dir, "@parent", VersionCatalog.ParentOf(d));
                    dir.ExecuteNonQuery();
                }
            }
            using var lists = connection.CreateCommand();
            lists.Transaction = tx;
            for (var i = 0; i < index.EmptyDirs.Count; i++)
            {
                lists.CommandText = "INSERT INTO empty_dirs (version, path, seq) VALUES (@v, @p, @s)";
                lists.Parameters.Clear(); lists.Parameters.AddWithValue("@v", index.Version); lists.Parameters.AddWithValue("@p", index.EmptyDirs[i]); lists.Parameters.AddWithValue("@s", i);
                lists.ExecuteNonQuery();
                // the empty dir is a browsable node
                EntryRowMapper.Set(dir, "@v", index.Version); EntryRowMapper.Set(dir, "@path", index.EmptyDirs[i]); EntryRowMapper.Set(dir, "@parent", VersionCatalog.ParentOf(index.EmptyDirs[i]));
                dir.ExecuteNonQuery();
            }
            for (var i = 0; i < index.UnrecoverablePaths.Count; i++)
            {
                lists.CommandText = "INSERT INTO unrecoverable (version, path, seq) VALUES (@v, @p, @s)";
                lists.Parameters.Clear(); lists.Parameters.AddWithValue("@v", index.Version); lists.Parameters.AddWithValue("@p", index.UnrecoverablePaths[i]); lists.Parameters.AddWithValue("@s", i);
                lists.ExecuteNonQuery();
            }
            lists.CommandText = "INSERT INTO versions (version, identity, entry_count, imported_at) VALUES (@v, @i, @c, @at)";
            lists.Parameters.Clear();
            lists.Parameters.AddWithValue("@v", index.Version); lists.Parameters.AddWithValue("@i", identity);
            lists.Parameters.AddWithValue("@c", index.Entries.Count); lists.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            lists.ExecuteNonQuery();
            await tx.CommitAsync();
        }
    }
}
```

`VersionCatalog.ParentOf` is already `internal static`.

- [ ] **Step 2: Write the failing tests**

```csharp
// backend/tests/AzureStorageBackup.Api.Tests/CatalogUpgradeTests.cs
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class CatalogUpgradeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "asb-catalog-upgrade-tests", Guid.NewGuid().ToString("N"));
    private const int AccountId = 3;
    private const string Container = "database";
    public CatalogUpgradeTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    private static IndexEntry E(string path, long length, string hash) => CatalogV2Tests.Entry(path, length, hash,
        new StorageRef { Kind = "blob", Ref = "data/" + hash, Volumes = 1, VolumeSizes = [length] });

    private static List<VersionIndex> History()
    {
        var a = E("x/a.txt", 1, "ha"); var b = E("x/b.txt", 2, "hb"); var c = E("y/c.txt", 3, "hc");
        return
        [
            new VersionIndex { Version = 1, Entries = [a, b, c], EmptyDirs = ["z"] },
            new VersionIndex { Version = 2, Entries = [a, b with { Length = 22, FullHash = "hb2" }, c] },                 // b modified
            new VersionIndex { Version = 3, Entries = [c, a, b with { Length = 22, FullHash = "hb2" }], UnrecoverablePaths = ["y/c.txt"] }, // out of path order, c marked
            new VersionIndex { Version = 4, Entries = [a, b with { Length = 22, FullHash = "hb2" }] },                     // c deleted
        ];
    }

    private static async Task<byte[]> SerializeAsync(VersionCatalog catalog, int version)
    {
        using var ms = new MemoryStream();
        await catalog.SerializeVersionAsync(version, ms, patches: null, CancellationToken.None);
        return ms.ToArray();
    }

    [Fact]
    public async Task A_format_1_catalog_converts_on_its_first_write_open_and_serializes_every_version_identically()
    {
        var store = new VersionCatalogStore(_root);
        var path = store.PathFor(AccountId, Container);
        var history = History();
        await LegacyCatalogFixture.WriteAsync(path, history);
        Assert.True(store.NeedsUpgrade(AccountId, Container));

        var reports = new List<CatalogUpgradeProgress>();
        await store.UpgradeAsync(AccountId, Container, new InlineProgress<CatalogUpgradeProgress>(reports.Add), CancellationToken.None);
        Assert.False(store.NeedsUpgrade(AccountId, Container));
        Assert.Contains(reports, r => r.VersionDone && r.Version == 4);
        Assert.Equal(history.Sum(v => v.Entries.Count), reports[^1].RowsTotal);
        Assert.Equal(reports[^1].RowsTotal, reports[^1].RowsDone);

        await using var catalog = await store.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None);
        foreach (var index in history)
            Assert.Equal(LegacyIndexSerializer.SerializeIndex(index), await SerializeAsync(catalog, index.Version));
        Assert.Equal(CatalogSql.GlobalIndexNames.Count, await catalog.GlobalIndexCountAsync(CancellationToken.None));
        // a: one row; b: [1,2) [2,∞); c: [1,3) [3,4) (the mark starts a row); total 5
        var rows = await catalog.HistoryRowsAsync(CancellationToken.None);   // versions' declared counts survive
        Assert.Equal(history.Sum(v => v.Entries.Count), rows);
    }

    [Fact]
    public async Task A_read_only_open_of_a_format_1_catalog_converts_it_by_taking_the_write_lock()
    {
        var store = new VersionCatalogStore(_root);
        var path = store.PathFor(AccountId, Container);
        var history = History();
        await LegacyCatalogFixture.WriteAsync(path, history);

        await using var catalog = await store.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None);
        Assert.Equal(LegacyIndexSerializer.SerializeIndex(history[2]), await SerializeAsync(catalog, 3));
        Assert.False(store.NeedsUpgrade(AccountId, Container));
    }

    [Fact]
    public async Task A_conversion_killed_between_versions_resumes_where_it_stopped()
    {
        var store = new VersionCatalogStore(_root);
        var path = store.PathFor(AccountId, Container);
        var history = History();
        await LegacyCatalogFixture.WriteAsync(path, history);

        using var cts = new CancellationTokenSource();
        var stopAfterFirst = new InlineProgress<CatalogUpgradeProgress>(r => { if (r.VersionDone && r.Version == 2) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpgradeAsync(AccountId, Container, stopAfterFirst, cts.Token));
        Assert.True(store.NeedsUpgrade(AccountId, Container));   // not stamped: versions 1 and 2 are in, 3 and 4 are not

        var resumed = new List<CatalogUpgradeProgress>();
        await new VersionCatalogStore(_root).UpgradeAsync(AccountId, Container, new InlineProgress<CatalogUpgradeProgress>(resumed.Add), CancellationToken.None);
        Assert.DoesNotContain(resumed, r => r.Version <= 2 && !r.VersionDone);  // no rows re-imported for 1 and 2
        await using var catalog = await VersionCatalog.OpenAsync(path, readOnly: true, CancellationToken.None);
        foreach (var index in history)
            Assert.Equal(LegacyIndexSerializer.SerializeIndex(index), await SerializeAsync(catalog, index.Version));
    }

    [Fact]
    public async Task A_catalog_nobody_wrote_yet_needs_no_upgrade_and_neither_does_a_fresh_one()
    {
        var store = new VersionCatalogStore(_root);
        Assert.False(store.NeedsUpgrade(AccountId, Container));
        using var held = await store.LockForWriteAsync(AccountId, Container, CancellationToken.None);
        await using var _ = await store.OpenForWriteAsync(held, AccountId, Container, CancellationToken.None);
        Assert.False(store.NeedsUpgrade(AccountId, Container));
    }
}
```

`InlineProgress<T>` exists in the test project (used by `VersionCatalogsMigrationTests`).

- [ ] **Step 3: Run the tests to verify they fail**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~CatalogUpgradeTests"`
Expected: build errors — `NeedsUpgrade`, `UpgradeAsync`, `CatalogUpgradeProgress` do not exist.

- [ ] **Step 4: Implement `CatalogUpgrade`**

```csharp
// backend/src/AzureStorageBackup.Api/Services/CatalogUpgrade.cs
using System.Runtime.CompilerServices;
using AzureStorageBackup.Api.Models;
using Microsoft.Data.Sqlite;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Converts a format-1 catalog (one row per version per path) to format 2 (one row per path per change) in place:
/// the old tables are renamed aside, the v2 schema is created, and every version is merged in ascending order
/// through the same import the run uses, one transaction per version, with an <c>upgrade_done</c> row per version
/// so a conversion killed halfway resumes from the first version not yet in. The last thing written is the format
/// stamp; a file without it is resumed, never trusted. Reads are sequential over the old rows; the cost is the old
/// row count, once — minutes to tens of minutes for a history of millions of rows.
/// </summary>
internal static class CatalogUpgrade
{
    private const string BeginSql = """
        DROP INDEX IF EXISTS entries_seq; DROP INDEX IF EXISTS entries_parent; DROP INDEX IF EXISTS entries_fold;
        DROP INDEX IF EXISTS entries_path_key; DROP INDEX IF EXISTS entries_storage;
        DROP INDEX IF EXISTS entries_content; DROP INDEX IF EXISTS entries_ref; DROP INDEX IF EXISTS entries_head;
        DROP INDEX IF EXISTS dirs_parent;
        ALTER TABLE entries RENAME TO v1_entries;
        ALTER TABLE dirs RENAME TO v1_dirs;
        ALTER TABLE empty_dirs RENAME TO v1_empty_dirs;
        ALTER TABLE unrecoverable RENAME TO v1_unrecoverable;
        ALTER TABLE import_issues RENAME TO v1_import_issues;
        CREATE TABLE upgrade_done (version INTEGER PRIMARY KEY);
        """;
    private const string EndSql = """
        DROP TABLE v1_entries; DROP TABLE v1_dirs; DROP TABLE v1_empty_dirs; DROP TABLE v1_unrecoverable; DROP TABLE v1_import_issues;
        DROP TABLE upgrade_done;
        """;
    private const string PendingVersionsSql =
        "SELECT version, identity, entry_count FROM versions WHERE version NOT IN (SELECT version FROM upgrade_done) ORDER BY version";
    private const string TotalRowsSql = "SELECT COALESCE(SUM(entry_count), 0) FROM versions";
    private const string DoneRowsSql = "SELECT COALESCE(SUM(v.entry_count), 0) FROM versions v JOIN upgrade_done d ON d.version = v.version";
    private const string V1EntriesSql = $"SELECT {EntryRowMapper.Columns} FROM v1_entries WHERE version=@v ORDER BY path_key";
    private const string V1SeqOrderSql = "SELECT seq, path, path_key FROM v1_entries WHERE version=@v ORDER BY seq";
    private const string V1EmptyDirsSql = "SELECT path FROM v1_empty_dirs WHERE version=@v ORDER BY seq";
    private const string V1UnrecoverableSql = "SELECT path FROM v1_unrecoverable WHERE version=@v ORDER BY seq";
    private const string CopyIssuesSql = "INSERT OR IGNORE INTO import_issues (version, path, issue) SELECT version, path, issue FROM v1_import_issues WHERE version=@v";
    private const string InsertOrderSql = "INSERT INTO entry_order (version, seq, path) VALUES (@v, @seq, @path)";
    private const string MarkDoneSql = "INSERT INTO upgrade_done (version) VALUES (@v)";

    public static async Task RunAsync(VersionCatalog catalog, IProgress<CatalogUpgradeProgress>? progress, CancellationToken ct)
    {
        var connection = catalog.Connection;
        if (!CatalogSql.HasTable(connection, "v1_entries"))
        {
            // First time in: put the old tables aside and lay the v2 schema beside them. One transaction: a kill
            // here leaves either the old layout or the whole in-progress layout, never half.
            await using var begin = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = begin;
                command.CommandText = BeginSql;
                await command.ExecuteNonQueryAsync(ct);
            }
            CatalogSql.EnsureSchema(connection, begin);
            await begin.CommitAsync(ct);
        }

        var total = await ScalarAsync(connection, TotalRowsSql, ct);
        var done = await ScalarAsync(connection, DoneRowsSql, ct);
        foreach (var (version, identity, count) in await PendingAsync(connection, ct))
        {
            ct.ThrowIfCancellationRequested();
            var booked = 0L;
            progress?.Report(new CatalogUpgradeProgress(version, done, total, VersionDone: false));

            await catalog.RunInTransactionAsync(async () =>
            {
                var emptyDirs = await StringsAsync(connection, V1EmptyDirsSql, version, ct);
                var unrecoverable = await StringsAsync(connection, V1UnrecoverableSql, version, ct);
                await catalog.ImportOrderedInTransactionAsync(version, identity, count, V1Entries(connection, version, ct), emptyDirs, unrecoverable,
                    n =>
                    {
                        var landed = Math.Min(n, count);
                        if (landed > booked) { done += landed - booked; booked = landed; }
                        progress?.Report(new CatalogUpgradeProgress(version, done, total, VersionDone: false));
                    }, ct);
                await RecordOrderIfNotPathOrderAsync(connection, version, ct);
                await ExecAsync(connection, CopyIssuesSql, version, ct);
                await ExecAsync(connection, MarkDoneSql, version, ct);
            }, ct);

            done += count - booked;
            progress?.Report(new CatalogUpgradeProgress(version, done, total, VersionDone: true));
        }

        await using (var end = (SqliteTransaction)await connection.BeginTransactionAsync(ct))
        {
            using var command = connection.CreateCommand();
            command.Transaction = end;
            command.CommandText = EndSql;
            await command.ExecuteNonQueryAsync(ct);
            CatalogSql.MarkCurrent(connection, end);
            await end.CommitAsync(ct);
        }

        // Reclaim the old rows' pages. VACUUM needs temp room for a copy of the file; when there is none it fails
        // fast with SQLITE_FULL, and the file stays large but correct.
        try
        {
            using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM";
            await vacuum.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 13 /* SQLITE_FULL */ or 14 /* SQLITE_CANTOPEN */)
        {
            // logged by the store, which knows the path
            catalog.VacuumSkipped = ex.Message;
        }
    }

    /// <summary>A version whose <c>seq</c> order is not its path order (pre-M4 builds) gets an order table, so it
    /// serializes as it came. The check is one pass over the version's rows in seq order.</summary>
    private static async Task RecordOrderIfNotPathOrderAsync(SqliteConnection connection, int version, CancellationToken ct)
    {
        var order = new List<(int Seq, string Path)>();
        var inPathOrder = true;
        byte[]? last = null;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = connection.Transaction();   // see VersionCatalog.Connection / Transaction below
            command.CommandText = V1SeqOrderSql;
            command.Parameters.AddWithValue("@v", version);
            await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var key = (byte[])reader["path_key"];
                if (last is not null && key.AsSpan().SequenceCompareTo(last) < 0)
                    inPathOrder = false;
                last = key;
                order.Add((reader.GetInt32(0), reader.GetString(1)));
            }
        }
        if (inPathOrder)
            return;
        using var insert = connection.CreateCommand();
        insert.Transaction = connection.Transaction();
        insert.CommandText = InsertOrderSql;
        foreach (var (seq, path) in order)
        {
            EntryRowMapper.Set(insert, "@v", version);
            EntryRowMapper.Set(insert, "@seq", seq);
            EntryRowMapper.Set(insert, "@path", path);
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    private static async IAsyncEnumerable<IndexEntry> V1Entries(SqliteConnection connection, int version, [EnumeratorCancellation] CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = connection.Transaction();
        command.CommandText = V1EntriesSql;
        command.Parameters.AddWithValue("@v", version);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return EntryRowMapper.Read(reader);
    }

    private static async Task<List<(int Version, long Identity, int Count)>> PendingAsync(SqliteConnection connection, CancellationToken ct)
    {
        var pending = new List<(int, long, int)>();
        using var command = connection.CreateCommand();
        command.CommandText = PendingVersionsSql;
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            pending.Add((reader.GetInt32(0), reader.GetInt64(1), reader.GetInt32(2)));
        return pending;
    }

    private static async Task<List<string>> StringsAsync(SqliteConnection connection, string sql, int version, CancellationToken ct)
    {
        var values = new List<string>();
        using var command = connection.CreateCommand();
        command.Transaction = connection.Transaction();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@v", version);
        await using var reader = (SqliteDataReader)await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            values.Add(reader.GetString(0));
        return values;
    }

    private static async Task ExecAsync(SqliteConnection connection, string sql, int version, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = connection.Transaction();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@v", version);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }
}
```

This needs four small doors on `VersionCatalog` (add to the plumbing section):

```csharp
    internal SqliteConnection Connection => _connection;

    /// <summary>The upgrade's transaction door: the merge runs inside a transaction the caller opens, one per version.</summary>
    internal async Task RunInTransactionAsync(Func<Task> work, CancellationToken ct)
    {
        await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);
        _transaction = transaction;
        try
        {
            await work();
            await transaction.CommitAsync(ct);
        }
        finally
        {
            _transaction = null;
        }
    }

    /// <summary>The merge without its own transaction, for a caller that already holds one (the upgrade).</summary>
    internal Task ImportOrderedInTransactionAsync(int version, long identity, int entryCount, IAsyncEnumerable<IndexEntry> entries,
        IReadOnlyList<string> emptyDirs, IReadOnlyList<string> unrecoverable, Action<long>? onEntries, CancellationToken ct) =>
        MergeVersionAsync(version, identity, entryCount, entries, emptyDirs, unrecoverable, ordered: true, onEntries, ct);

    /// <summary>Set by <see cref="CatalogUpgrade"/> when the post-conversion VACUUM could not run; the store logs it.</summary>
    internal string? VacuumSkipped { get; set; }
```

and split `ImportCoreAsync` in `VersionCatalog.Import.cs` so the transaction wrapper is separate from the body: rename the body (from `if (await GetVersionAsync…` through `UpsertVersionAsync`) to `private async Task MergeVersionAsync(…)` with the same parameters minus the transaction lines, and make `ImportCoreAsync` = `RunInTransactionAsync(() => MergeVersionAsync(...), ct)`. Add to `CatalogSql` overloads `EnsureSchema(SqliteConnection, SqliteTransaction?)` and `MarkCurrent(SqliteConnection, SqliteTransaction?)` that set `command.Transaction`. Add an extension `internal static SqliteTransaction? Transaction(this SqliteConnection c)` is not available in Microsoft.Data.Sqlite — instead give `CatalogUpgrade` the `VersionCatalog` and have its commands created through a new `internal SqliteCommand VersionCatalog.CreateCommand(string sql)` that assigns the pending transaction (it is `Command(sql)` made internal). Replace every `connection.CreateCommand()` + `command.Transaction = connection.Transaction()` pair above with `catalog.CreateCommand(sql)`.

- [ ] **Step 5: Hook the store**

In `VersionCatalogStore.cs`:

```csharp
    /// <summary>Whether the container's catalog is a format-1 file (or a conversion of one was interrupted) that the
    /// next write open will convert. The backup asks before it opens anything, so the conversion runs on a stage line
    /// of its own; every other opener converts silently.</summary>
    public bool NeedsUpgrade(int accountId, string container)
    {
        var path = PathFor(accountId, container);
        if (!File.Exists(path))
            return false;
        try
        {
            using var connection = new SqliteConnection(CatalogSql.ConnectionString(path, readOnly: true));
            connection.Open();
            return CatalogSql.FormatOf(connection) < CatalogSql.Format
                && (CatalogSql.HasTable(connection, "v1_entries") || CatalogSql.HasTable(connection, "entries"));
        }
        catch (SqliteException)
        {
            return false;   // unreadable: the write open's recovery path is the one that deals with it
        }
    }

    /// <summary>Converts the catalog now, under the write lock, reporting per version. A no-op on a current file.</summary>
    public async Task UpgradeAsync(int accountId, string container, IProgress<CatalogUpgradeProgress>? progress, CancellationToken ct)
    {
        using var held = await LockForWriteAsync(accountId, container, ct);
        await using var _ = await OpenForWriteAsync(held, accountId, container, ct, progress);
    }
```

Give `OpenForWriteAsync` an optional trailing parameter `IProgress<CatalogUpgradeProgress>? upgradeProgress = null` and pass it to `OpenCheckedAsync(path, ct, upgradeProgress)`. In `OpenCheckedAsync`, right after `var catalog = await VersionCatalog.OpenAsync(path, readOnly: false, ct);`:

```csharp
        if (catalog.NeedsUpgrade)
        {
            logger?.LogInformation("Catalog {Path} is in format 1; converting it in place to format {Format}.", path, CatalogSql.Format);
            try
            {
                await CatalogUpgrade.RunAsync(catalog, upgradeProgress, ct);
            }
            catch
            {
                await catalog.DisposeAsync();
                throw;
            }
            if (catalog.VacuumSkipped is { } why)
                logger?.LogWarning("Catalog {Path} was converted but not compacted ({Why}); it is correct and larger than it needs to be.", path, why);
            await catalog.DisposeAsync();
            catalog = await VersionCatalog.OpenAsync(path, readOnly: false, ct);   // a fresh handle on the stamped file
        }
```

A conversion failure other than cancellation must fall into the recovery path. Declare beside `CatalogFormatException`:

```csharp
/// <summary>A format-1 conversion failed for a reason other than cancellation. The store answers it the way it answers
/// a corrupt file: the catalog is a cache, so it is deleted and rebuilt from the cloud on demand.</summary>
public sealed class CatalogUpgradeException(string path, Exception cause)
    : IOException($"Converting catalog '{path}' to the current format failed: {cause.Message}", cause);
```

In `OpenCheckedAsync`, wrap `CatalogUpgrade.RunAsync` in `try { … } catch (Exception ex) when (ex is not OperationCanceledException) { await catalog.DisposeAsync(); throw new CatalogUpgradeException(path, ex); }`. In `OpenForWriteAsync`, beside the existing `catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)`, add `catch (CatalogUpgradeException ex) { return await RecoverAsync(path, ex, ct); }` and widen `RecoverAsync`'s `cause` parameter from `SqliteException` to `Exception` (it only logs it).

Read-only open converts by taking the lock:

```csharp
    public async Task<VersionCatalog> OpenAsync(int accountId, string container, bool readOnly, CancellationToken ct)
    {
        if (!readOnly)
            throw new ArgumentException(…);   // unchanged
        var path = PathFor(accountId, container);
        if (!File.Exists(path))
            throw new FileNotFoundException(…);   // unchanged
        try
        {
            return await VersionCatalog.OpenAsync(path, readOnly: true, ct);
        }
        catch (CatalogFormatException)
        {
            // A reader met a format-1 file. It cannot convert on its own handle; the write path can, and this is
            // the one place a read-only open takes the write lock — never while a caller already holds it (the
            // lock is not reentrant): every read-only opener in the product opens outside its write scopes.
            using var held = await LockForWriteAsync(accountId, container, ct);
            await using (var _ = await OpenForWriteAsync(held, accountId, container, ct)) { }
            return await VersionCatalog.OpenAsync(path, readOnly: true, ct);
        }
    }
```

Audit for that comment's claim — list every `OpenAsync(…, readOnly: true` call site (`grep -rn "readOnly: true" backend/src`) and confirm none sits inside a `using var held = await …LockForWriteAsync` scope. At the time of writing: `RetentionCleaner.cs:338, 415`, `BackupRepairer.cs:199`, `VersionCatalogs.cs:58, 109`, `DeferredRepairs.cs:65`, `RestoreOrchestrator.cs:150`, `BackupConfigEndpoints.cs:1458, 1495`, `BackupOrchestrator.cs:850, 851`, `BackupChecker.cs:245, 405, 556`. Record the audit's result in the commit message.

`IVersionCatalogs` gains:

```csharp
    /// <summary>Whether the container's catalog is a format-1 file the next write open converts
    /// (<see cref="VersionCatalogStore.NeedsUpgrade"/>). The backup asks before it opens anything, so the conversion
    /// runs on its own stage line. Defaults to false for doubles that keep no file.</summary>
    bool NeedsUpgrade(int accountId, string container) => false;

    /// <summary>Converts the catalog now, reporting per version — see <see cref="VersionCatalogStore.UpgradeAsync"/>.</summary>
    Task UpgradeAsync(int accountId, string container, IProgress<CatalogUpgradeProgress>? progress, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>One reading from a catalog conversion: the version under conversion, rows landed so far over all
/// versions, the rows the whole history declares, and whether this reading closes the version.</summary>
public readonly record struct CatalogUpgradeProgress(int Version, long RowsDone, long RowsTotal, bool VersionDone);
```

`VersionCatalogs` passes both through to `catalogs`.

- [ ] **Step 6: Run the tests**

Run: `cd backend && dotnet test --filter "FullyQualifiedName~CatalogUpgradeTests|FullyQualifiedName~CatalogV2Tests|FullyQualifiedName~VersionCatalogStoreTests|FullyQualifiedName~VersionCatalogsMigrationTests"`
Expected: all pass. The resume test's cancellation lands between version 2's `VersionDone` report and version 3's transaction; `RunAsync` checks the token at the top of each version.

- [ ] **Step 7: Full suite with Azurite, then commit and merge**

Same suite command as Task 2 Step 8. Then:

```bash
git add -A
git commit -m "feat(catalog): format-1 catalogs convert in place on the first write open, one version per transaction, resumably

Audit of read-only opens under a held write lock: none (list the call sites checked).

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git checkout main && git merge --no-ff catalog-v2-upgrade -m "Merge catalog-v2-upgrade: in-place conversion of format-1 catalogs" && git branch -d catalog-v2-upgrade
```

(Create the branch `catalog-v2-upgrade` at the start of this task.)

---

### Task 6: The Upgrading catalog stage, back end and front end

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs` (enum at ~line 128; the block before "The catalog's full-file quick_check, when it is owed")
- Create: `backend/src/AzureStorageBackup.Api/Services/CatalogUpgradeAccounting.cs`
- Modify: `frontend/src/api/backupConfigs.ts:29-52`, `frontend/src/lib/stageLines.ts` (units, labels, the entries `done` branch), `frontend/src/lib/windDownControls.ts:70-115`, `frontend/src/pages/BackupConfigsPage.tsx:2302-2308`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/BackupRunStateTests.cs`, `backend/tests/AzureStorageBackup.Api.Tests/BackupOrchestratorTests.cs`, `frontend/src/lib/stageLines.test.ts`, `frontend/src/lib/windDownControls.test.ts`

**Interfaces:**
- Consumes: `IVersionCatalogs.NeedsUpgrade`, `IVersionCatalogs.UpgradeAsync`, `CatalogUpgradeProgress` (Task 5).
- Produces: `BackupStage.UpgradingCatalog` (value 1; every later member shifts by one, mirrored in `frontend/src/api/backupConfigs.ts`).

- [ ] **Step 1: Write the failing tests**

Backend, in `BackupRunStateTests.cs`:

```csharp
    [Fact]
    public void Upgrading_the_catalog_sits_between_scanning_and_the_version_load_and_is_not_wrapping_up()
    {
        Assert.Equal(1, (int)BackupStage.UpgradingCatalog);
        Assert.True(BackupStage.UpgradingCatalog < BackupStage.LoadingVersions);
        Assert.False(At(BackupStage.UpgradingCatalog).WrappingUp);
    }
```

(`At(...)` is the file's existing helper that builds a `BackupRunState` at a stage.)

In `BackupOrchestratorTests.cs`, beside `The_Catalog_Check_Has_Its_Own_Figureless_Stage_When_Damage_Was_Seen`:

```csharp
    /// <summary>A format-1 catalog is converted on the run's first touch of it, under its own stage — counted in
    /// entries, one version at a time — before the check and the version load, which would otherwise convert it
    /// silently inside their own write open.</summary>
    [SkippableFact]
    public async Task A_Format_1_Catalog_Is_Upgraded_On_Its_Own_Stage_Before_Anything_Else_Opens_It()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var account = AzuriteAccount();
        var name = RandomName("orchup-");
        var root = Path.Combine(_temp, "catalogs");
        var store = new VersionCatalogStore(root);
        var (orchestrator, _, factory) = Build(catalogStore: store);
        var container = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
        try
        {
            WriteText("a.txt", "alpha");
            var first = new List<BackupProgress>();
            await orchestrator.RunAsync(Request(account, name), new SyncProgress(first));   // version 1, a format-2 catalog
            Assert.DoesNotContain(first, p => p.Stage == BackupStage.UpgradingCatalog);

            // Rewrite the catalog as a format-1 file holding the same version, the way 2026.9.13.1 left it.
            var history = new List<VersionIndex>();
            await using (var catalog = await store.OpenAsync(account.Id, name, readOnly: true, CancellationToken.None))
            {
                using var ms = new MemoryStream();
                await catalog.SerializeVersionAsync(1, ms, null, CancellationToken.None);
                ms.Position = 0;
                using var reader = new IndexStreamReader(ms);
                history.Add(new VersionIndex { Version = 1, Entries = [.. reader.Entries()], EmptyDirs = [.. reader.ReadEmptyDirs()], UnrecoverablePaths = [.. reader.ReadUnrecoverable()] });
            }
            var identity = (await store.OpenAsync(account.Id, name, readOnly: true, CancellationToken.None).ContinueWith(t => t.Result)).ListVersionsAsync(CancellationToken.None).Result[0].Identity;
            await store.RemoveContainerAsync(account.Id, name, CancellationToken.None);
            await LegacyCatalogFixture.WriteAsync(store.PathFor(account.Id, name), history, identity);

            WriteText("b.txt", "beta");
            var second = new List<BackupProgress>();
            await orchestrator.RunAsync(Request(account, name), new SyncProgress(second));

            var stages = second.Select(p => p.Stage).Distinct().ToList();
            var upgrading = stages.IndexOf(BackupStage.UpgradingCatalog);
            Assert.True(upgrading >= 0, "the conversion reports its own stage");
            Assert.True(upgrading < stages.IndexOf(BackupStage.LoadingVersions));
            var detail = second.Select(p => p.Detail).Last(d => d is { Stage: "UpgradingCatalog" })!;
            Assert.Equal(1, detail.Total);
            Assert.Equal(1, detail.Processed);
            Assert.Equal(detail.WorkTotal, detail.WorkDone);
            Assert.True(detail.WorkTotal > 0);

            var third = new List<BackupProgress>();
            await orchestrator.RunAsync(Request(account, name), new SyncProgress(third));
            Assert.DoesNotContain(third, p => p.Stage == BackupStage.UpgradingCatalog);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
```

Simplify the identity line to two statements (open read-only, list, take `[0].Identity`, dispose) — the `ContinueWith` above is only there to keep the snippet short; write it as ordinary awaits.

Frontend, in `stageLines.test.ts`:

```ts
describe('the catalog upgrade stage', () => {
  test('counts versions on the counts line and entries on the done line, like Loading versions', () => {
    const lines = stageLines(
      progress({ stage: 'UpgradingCatalog', processed: 3, total: 14, workTotal: 5_948_795, workDone: 1_250_000, workPercent: 21, currentItem: 'version 4' }),
    )
    expect(lines.label).toBe('Upgrading catalog')
    expect(lines.counts).toBe('3 of 14 versions')
    expect(lines.done).toBe('1,250,000 / 5,948,795 entries (21%)')
    expect(lines.speed).toBe('')
  })
})
```

In `windDownControls.test.ts`, inside `describe('loading the version history')`:

```ts
  test('the catalog upgrade greys Pause the same way, with a reason of its own', () => {
    const c = windDownControls(undefined, false, 'UpgradingCatalog')
    expect(c.canPause).toBe(false)
    expect(c.canActOnGate).toBe(true)
    expect(c.canStop).toBe(true)
    expect(c.pauseHint).toContain('upgrading its catalog')
  })
```

- [ ] **Step 2: Run them to verify they fail**

Backend: `cd backend && dotnet test --filter "FullyQualifiedName~Upgrading_the_catalog_sits_between"` — build error, no such enum member.
Frontend: `cd frontend && npx vitest run src/lib/stageLines.test.ts src/lib/windDownControls.test.ts` — label falls back to the raw token; type error on `'UpgradingCatalog'`.

- [ ] **Step 3: Backend implementation**

Enum (`BackupOrchestrator.cs`, after `Scanning,`):

```csharp
    /// <summary>A format-1 catalog being converted in place to the current format, one version at a time, before
    /// anything else opens it (see docs/storage-format.md, "Converting a format-1 catalog"). Its own stage because
    /// it runs once per container after the upgrade and is as long as the history is big; inside another stage's
    /// write open it would read as that stage hanging.</summary>
    UpgradingCatalog,
```

`CatalogUpgradeAccounting.cs`:

```csharp
namespace AzureStorageBackup.Api.Services;

/// <summary>Books a catalog conversion into the "Upgrading catalog" stage: the counts line is versions, the done line
/// is entries (the whole history's declared rows, landed as they go), the item line names the version under
/// conversion. Synchronous, like <see cref="VersionLoadAccounting"/>: a Progress&lt;T&gt; would post to the pool and a
/// conversion that finishes in milliseconds would report after the stage closed.</summary>
internal sealed class CatalogUpgradeAccounting(StageTracker tracker) : IProgress<CatalogUpgradeProgress>
{
    private bool _declared;
    private long _booked;
    private int? _current;

    public void Report(CatalogUpgradeProgress value)
    {
        if (!_declared)
        {
            tracker.DeclareWork(value.RowsTotal);
            _declared = true;
        }
        if (value.RowsDone > _booked)
        {
            tracker.AdvanceWork(value.RowsDone - _booked);
            _booked = value.RowsDone;
        }
        if (_current != value.Version)
        {
            _current = value.Version;
            tracker.Touch($"version {value.Version}");
        }
        if (value.VersionDone)
            tracker.Advance(0, work: 0);
    }
}
```

Orchestrator, immediately before the comment "The catalog's full-file quick_check, when it is owed":

```csharp
        // A format-1 catalog is converted before anything opens it. Every write open converts on its own (a check
        // or a restore that comes first does it silently); the backup is where it is expected, so it stands under
        // its own name with the history's entries as its workload. Pause is greyed as for the version load — one
        // transaction per version, nothing to park in — and Suspend or Stop end the run between versions; the
        // conversion resumes from the first version not yet in at the next open.
        if (catalogs.NeedsUpgrade(request.Account.Id, request.Container))
        {
            progress?.Report(new BackupProgress(BackupStage.UpgradingCatalog, 0, 0, 0, 0));
            using var upgrading = new StageTracker("UpgradingCatalog", info.Versions.Count, d =>
                progress?.Report(new BackupProgress(BackupStage.UpgradingCatalog, 0, 0, 0, 0) { Detail = d }));
            var accounting = new CatalogUpgradeAccounting(upgrading);
            using (control?.Gate.BeginWork())
                await BeforeUploadAsync(async t =>
                {
                    await catalogs.UpgradeAsync(request.Account.Id, request.Container, accounting, t);
                    return 0;
                });
            upgrading.Complete();
        }
```

- [ ] **Step 4: Frontend implementation**

`backupConfigs.ts`: insert `UpgradingCatalog: 1,` after `Scanning: 0,` and renumber `LoadingVersions: 2, CheckingCatalog: 3, Diffing: 4, Uploading: 5, WritingIndex: 6, UpdatingCatalog: 7, CleaningUp: 8, Completed: 9`; add `[BackupStage.UpgradingCatalog]: 'Upgrading catalog',` to `backupStageLabels`.

`stageLines.ts`: `STAGE_UNITS` gains `UpgradingCatalog: 'versions',` (with a comment: the counts line is versions, the done line is entries, as for Loading versions); `STAGE_LABELS` gains `UpgradingCatalog: 'Upgrading catalog',`; the `done` branch condition becomes `detail.stage === 'LoadingVersions' || detail.stage === 'UpdatingCatalog' || detail.stage === 'UpgradingCatalog'`.

`windDownControls.ts`: `export type CatalogPass = 'LoadingVersions' | 'CheckingCatalog' | 'UpgradingCatalog'` and a third hint branch:

```ts
          : canActOnGate && catalogPass === 'UpgradingCatalog'
            ? 'The backup is upgrading its catalog to the current format and cannot pause until the diff starts. Suspend or Stop end it now; the upgrade resumes from the last finished version next time.'
            : undefined
```

`BackupConfigsPage.tsx`: the `catalogPass` prop mapping gains `: p.stage === BackupStage.UpgradingCatalog ? 'UpgradingCatalog'`.

- [ ] **Step 5: Run everything**

Backend: `cd backend && dotnet test --filter "FullyQualifiedName~BackupRunStateTests"`; with Azurite: `dotnet test --filter "FullyQualifiedName~BackupOrchestratorTests"`.
Frontend: `cd frontend && npx vitest run && npx tsc -b && npx oxlint`.
Expected: all green.

- [ ] **Step 6: Commit and merge**

```bash
git checkout -b catalog-v2-stage
git add -A
git commit -m "feat(progress): the catalog conversion stands on its own stage, Upgrading catalog, before anything opens the file

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git checkout main && git merge --no-ff catalog-v2-stage -m "Merge catalog-v2-stage: the Upgrading catalog stage" && git branch -d catalog-v2-stage
```

---

### Task 7: Retire the bracket from the run's import; fold the docs

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs` (`ImportIntoCatalogAsync`)
- Modify: `backend/tests/AzureStorageBackup.Api.Tests/BackupProgressDetailTests.cs` (the comment on the index assertion)
- Modify: `docs/storage-format.md`, `docs/progress-display.md`, `docs/history.md`, `docs/backup-engine.md`
- Delete: `docs/catalog-format-v2.md`, `docs/catalog-format-v2-plan.md`

- [ ] **Step 1: Remove the bracket from `ImportIntoCatalogAsync`**

Delete the `bulk` decision, the `DropGlobalIndexesAsync` call, the `RebuildGlobalIndexesAsync` block and its "rebuilding content indexes" label; keep the row booking. Replace the removed comment with:

```csharp
                    // On format 2 a version inserts only its changes into the content-keyed indexes, so the run's
                    // own import takes no bracket. EnsureVersionsAsync keeps one for a migration of several
                    // missing versions (VersionCatalog.PrefersRebuild).
```

In `BackupProgressDetailTests`, change the comment on the `GlobalIndexNames.Count` assertion to say the indexes are never taken down on the run's path.

- [ ] **Step 2: Run the suite with Azurite**

Same command as Task 2 Step 8. Expected: `Failed: 0`.

- [ ] **Step 3: Fold the documentation**

In `docs/storage-format.md`, section "The catalog": replace the description of `entries` and its indexes with the v2 model (the spec's "The model", "Indexes", "Writing a version", "Reading", "Retention", "Repair patches", "Legacy order" sections, in the document's own voice), and add "Converting a format-1 catalog" from the spec's "Migration" section. Move the spec's "Why" measurements into the rationale block. Remove "One version at a time is decided by size" (the bracket rule now applies to `EnsureVersionsAsync` only — say so in the migration paragraph).

In `docs/progress-display.md`, add the stage row:

```
| Upgrading catalog | versions | a format-1 catalog converted in place, one version per transaction, before anything else opens it; the done line counts the history's entries as they land, like Loading versions. Pause greyed, Suspend and Stop end the run between versions and the conversion resumes next time |
```

In `docs/backup-engine.md` § 7 Commit, replace "every entry of the new version goes in … minutes of random B-tree inserts" with one sentence: the import merges the version against the rows current at its predecessor and writes only the changes.

In `docs/history.md`, add a row dated with the merge:

```
| <the merge date, MM-DD> | Catalog format 2: one `entries` row per path per change over a version interval, an integer key so the eight indexes carry a pointer instead of a path copy, imports as a path-ordered merge that writes only the changes, in-place conversion of format-1 files on their own stage | [storage-format.md](storage-format.md) |
```

Delete `docs/catalog-format-v2.md` and `docs/catalog-format-v2-plan.md`.

- [ ] **Step 4: Commit and merge**

```bash
git checkout -b catalog-v2-docs
git add -A
git commit -m "docs: catalog format 2 described where the running system is described; the spec and plan fold into it

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
git checkout main && git merge --no-ff catalog-v2-docs -m "Merge catalog-v2-docs" && git branch -d catalog-v2-docs
git push origin main
```

The release (version bump, CI, image) is a separate step the user triggers.
