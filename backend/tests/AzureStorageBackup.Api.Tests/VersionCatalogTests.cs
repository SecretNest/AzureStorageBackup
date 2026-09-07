using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using static AzureStorageBackup.Api.Tests.IndexAssert;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The catalog owns the facts every other flow reads (dedup, browsing, retention, serialization back to the cloud),
/// so these tests pin the <em>semantics</em> the in-memory maps had — which version wins a lookup, which row survives
/// a duplicate path, what order entries come back in — rather than just "the SQL parses".
/// </summary>
public sealed class VersionCatalogTests : IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "asb-catalog-tests", Guid.NewGuid().ToString("N"));

    public VersionCatalogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leaked handle must not fail a test that already passed */ }
    }

    private string DbPath => System.IO.Path.Combine(_dir, "catalog.db");

    private Task<VersionCatalog> OpenAsync() => VersionCatalog.OpenAsync(DbPath, readOnly: false, CancellationToken.None);

    // ---- Test 1: the round trip that keeps the cloud format frozen -------------------------------------------

    [Fact]
    public async Task Import_then_serialize_is_byte_identical()
    {
        var index = IndexSamples.Sample();
        // Not path-sorted on purpose: "b.bin" is written first but sorts last, so a serializer that ordered rows by
        // path instead of by seq would produce different bytes here.
        index.Entries.Add(Entry("b.bin", 5));
        index.Entries.Add(Entry("a.bin", 6));
        index.Entries.Add(Entry("a/c.bin", 7));
        var expected = IndexSerializer.SerializeIndex(index);

        await using var catalog = await OpenAsync();
        using (var reader = new IndexStreamReader(new MemoryStream(expected)))
            await catalog.ImportVersionAsync(index.Version, identity: 42, reader, CancellationToken.None);

        var info = await catalog.GetVersionAsync(index.Version, CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal(42, info.Identity);
        Assert.Equal(index.Entries.Count, info.EntryCount);
        Assert.Equal([info], await catalog.ListVersionsAsync(CancellationToken.None));

        using var written = new MemoryStream();
        await catalog.SerializeVersionAsync(index.Version, written, patches: null, CancellationToken.None);
        Assert.Equal(expected, written.ToArray());
    }

    // ---- Test 2: a duplicate path cannot be stored twice -----------------------------------------------------

    [Fact]
    public async Task Duplicate_path_keeps_first_and_records_issue()
    {
        var index = new VersionIndex { Version = 1, Entries = [Entry("dup.txt", 111), Entry("other.txt", 5), Entry("dup.txt", 222)] };

        await using var catalog = await OpenAsync();
        using (var reader = new IndexStreamReader(new MemoryStream(IndexSerializer.SerializeIndex(index))))
            await catalog.ImportVersionAsync(1, identity: 1, reader, CancellationToken.None);

        var kept = await catalog.GetEntryAsync(1, "dup.txt", CancellationToken.None);
        Assert.NotNull(kept);
        Assert.Equal(111, kept.Length);
        Assert.Equal(2, (await catalog.GetVersionAsync(1, CancellationToken.None))!.EntryCount);
        Assert.Equal(["dup.txt|duplicate"], Issues(1));
    }

    // ---- Test 3: browsing one level at a time ----------------------------------------------------------------

    [Fact]
    public async Task Children_lists_files_and_dirs_one_level()
    {
        await using var catalog = await OpenAsync();
        await ImportAsync(catalog, 1, [Entry("a/b.txt", 1), Entry("a/c/d.txt", 2), Entry("e.txt", 3)], emptyDirs: ["a/empty"]);

        var root = await catalog.ChildrenAsync(1, "", CancellationToken.None);
        Assert.Equal(["a", "e.txt"], root.Select(c => c.Name).Order(StringComparer.Ordinal));
        var a = Assert.Single(root, c => c.Name == "a");
        Assert.True(a.IsDir);
        Assert.True(a.HasChildren);
        Assert.Null(a.Entry);
        var e = Assert.Single(root, c => c.Name == "e.txt");
        Assert.False(e.IsDir);
        Assert.False(e.HasChildren);
        Assert.Equal(3, e.Entry!.Length);

        var under = await catalog.ChildrenAsync(1, "a", CancellationToken.None);
        Assert.Equal(["b.txt", "c", "empty"], under.Select(c => c.Name).Order(StringComparer.Ordinal));
        Assert.True(Assert.Single(under, c => c.Name == "c").HasChildren);
        Assert.True(Assert.Single(under, c => c.Name == "empty").IsDir);
        Assert.False(Assert.Single(under, c => c.Name == "empty").HasChildren);
        Assert.False(Assert.Single(under, c => c.Name == "b.txt").IsDir);
    }

    // ---- Tests 4..6: the dedup lookups the in-memory resolver used to answer ----------------------------------

    [Fact]
    public async Task FindBlobByContent_prefers_the_latest_version_and_skips_unrecoverable()
    {
        await using var catalog = await OpenAsync();
        await ImportAsync(catalog, 1, [Entry("f.txt", 10, "H", Blob("data/x"))]);
        await ImportAsync(catalog, 2, [Entry("f.txt", 10, "H", Blob("data/y", raw: true, volumes: 2, sizes: [7, 3]))]);

        var hit = await catalog.FindBlobByContentAsync("H", 10, "H:head", "H:tail", CancellationToken.None);
        Assert.NotNull(hit);
        Assert.Equal("data/y", hit.Ref);
        Assert.True(hit.Raw);
        Assert.Equal(2, hit.Volumes);
        Assert.Equal([7L, 3L], hit.VolumeSizes);

        await catalog.ApplyPatchesAsync([new CatalogPatch(2, "f.txt", null, true, null)], CancellationToken.None);
        var healed = await catalog.FindBlobByContentAsync("H", 10, "H:head", "H:tail", CancellationToken.None);
        Assert.Equal("data/x", healed!.Ref);
        Assert.False(healed.Raw);

        // All four fields are the identity: a missing tail is "different content", not "unknown".
        Assert.Null(await catalog.FindBlobByContentAsync("H", 10, "H:head", null, CancellationToken.None));
        Assert.Null(await catalog.FindBlobByContentAsync("nope", 10, "H:head", "H:tail", CancellationToken.None));
    }

    [Fact]
    public async Task FindRefOwner_precedence()
    {
        await using var catalog = await OpenAsync();
        await ImportAsync(catalog, 1, [Entry("r.txt", 1, "A", Blob("data/r")), Entry("m.txt", 11, "M1", Blob("data/m"))]);
        await ImportAsync(catalog, 2, [Entry("d.txt", 2, "D2", Blob("data/d")), Entry("m.txt", 12, "M2", Blob("data/m"))],
            unrecoverable: ["d.txt", "m.txt"]);
        await ImportAsync(catalog, 3, [Entry("r.txt", 3, "C", Blob("data/r"))]);
        await ImportAsync(catalog, 4, [Entry("d.txt", 4, "D4", Blob("data/d"))], unrecoverable: ["d.txt"]);

        // Two healthy rows → the latest version, as the in-memory map's last write won.
        Assert.Equal(new CatalogRefOwner("C", 3, "C:head", "C:tail", false),
            await catalog.FindRefOwnerAsync("data/r", CancellationToken.None));
        // Only damaged rows → the earliest, as TryAdd kept the first.
        Assert.Equal(new CatalogRefOwner("D2", 2, "D2:head", "D2:tail", true),
            await catalog.FindRefOwnerAsync("data/d", CancellationToken.None));
        // A healthy row always beats a damaged one, whatever the version order.
        Assert.Equal(new CatalogRefOwner("M1", 11, "M1:head", "M1:tail", false),
            await catalog.FindRefOwnerAsync("data/m", CancellationToken.None));

        Assert.Null(await catalog.FindRefOwnerAsync("data/unknown", CancellationToken.None));
    }

    [Fact]
    public async Task FindPackMember_takes_the_first_version()
    {
        await using var catalog = await OpenAsync();
        await ImportAsync(catalog, 1, [Entry("old.txt", 5, "P", Pack("p0001", "old.txt"))]);
        await ImportAsync(catalog, 2, [Entry("new.txt", 5, "P", Pack("p0002", "new.txt"))]);
        await ImportAsync(catalog, 3, [Entry("named.txt", 6, "Q", Pack("p0003", entryName: null))]);

        Assert.Equal(new CatalogPackMember("p0001", "old.txt", "P:tail"),
            await catalog.FindPackMemberAsync("P", 5, "P:head", CancellationToken.None));
        // No entry_name recorded → the member sits inside the archive under the entry's own path.
        Assert.Equal(new CatalogPackMember("p0003", "named.txt", "Q:tail"),
            await catalog.FindPackMemberAsync("Q", 6, "Q:head", CancellationToken.None));
        Assert.Null(await catalog.FindPackMemberAsync("P", 5, "wrong:head", CancellationToken.None));
    }

    // ---- Test 7: what retention may delete -------------------------------------------------------------------

    [Fact]
    public async Task RefsOnlyIn_returns_refs_no_retained_version_uses()
    {
        await using var catalog = await OpenAsync();
        await ImportAsync(catalog, 1, [Entry("a.txt", 1, "A", Blob("data/a")), Entry("s.txt", 2, "S", Blob("data/shared")),
            Entry("p.txt", 3, "P", Pack("p0001", "p.txt"))]);
        await ImportAsync(catalog, 2, [Entry("b.txt", 4, "B", Blob("data/b")), Entry("s.txt", 2, "S", Blob("data/shared"))]);

        Assert.Equal(["data/a"], await catalog.RefsOnlyInAsync([1], "blob", CancellationToken.None));
        Assert.Equal(["p0001"], await catalog.RefsOnlyInAsync([1], "pack", CancellationToken.None));
        Assert.Equal(["data/b"], await catalog.RefsOnlyInAsync([2], "blob", CancellationToken.None));
        Assert.Equal(["data/a", "data/b", "data/shared"], await catalog.RefsOnlyInAsync([1, 2], "blob", CancellationToken.None));
        Assert.Empty(await catalog.RefsOnlyInAsync([], "blob", CancellationToken.None));
    }

    // ---- Test 8: retiring a version leaves nothing behind ----------------------------------------------------

    [Fact]
    public async Task RemoveVersion_drops_every_table()
    {
        var index = new VersionIndex
        {
            Version = 1,
            Entries = [Entry("a/b.txt", 1), Entry("gone.txt", 2), Entry("a/b.txt", 3)],
            EmptyDirs = ["a/empty"],
            UnrecoverablePaths = ["gone.txt"],
        };

        await using var catalog = await OpenAsync();
        using (var reader = new IndexStreamReader(new MemoryStream(IndexSerializer.SerializeIndex(index))))
            await catalog.ImportVersionAsync(1, identity: 1, reader, CancellationToken.None);
        foreach (var table in Tables)
            Assert.True(CountRows(table, 1) > 0, $"{table} should have rows before the version is removed");

        await catalog.RemoveVersionAsync(1, CancellationToken.None);

        Assert.Empty(await catalog.ListVersionsAsync(CancellationToken.None));
        Assert.Null(await catalog.GetVersionAsync(1, CancellationToken.None));
        foreach (var table in Tables)
            Assert.Equal(0, CountRows(table, 1));
    }

    // ---- Test 9: repair's patches ----------------------------------------------------------------------------

    [Fact]
    public async Task ApplyPatches_updates_flag_table_and_serialization()
    {
        var unreadableAt = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.FromHours(-5));
        await using var catalog = await OpenAsync();
        await ImportAsync(catalog, 1, [Entry("a.txt", 1, "A", Blob("data/a")), Entry("b.txt", 2, "B", Blob("data/b"))]);

        // Patches handed to the serializer alone already change the bytes: repair writes the fixed index to the cloud
        // first and only then tells the catalog about it.
        var patches = new[] { new CatalogPatch(1, "b.txt", unreadableAt, true, new StorageRef { Kind = "blob", Ref = "data/b2", Volumes = 2 }) };
        Assert.Equal(["b.txt"], ReadBack(await SerializeAsync(catalog, 1, patches)).UnrecoverablePaths);
        Assert.Empty(await catalog.UnrecoverableAsync(1, CancellationToken.None));

        await catalog.ApplyPatchesAsync(patches, CancellationToken.None);
        var applied = ReadBack(await SerializeAsync(catalog, 1, null));
        Assert.Equal(["b.txt"], applied.UnrecoverablePaths);
        var patched = applied.Entries.Single(e => e.Path == "b.txt");
        Assert.Equal(unreadableAt, patched.UnreadableAt);
        Assert.Equal("data/b2", patched.Storage!.Ref);
        Assert.Equal(2, patched.Storage.Volumes);
        Assert.Equal(["b.txt"], await catalog.UnrecoverableAsync(1, CancellationToken.None));
        Assert.Equal([("b.txt", unreadableAt)], await catalog.UnreadableAsync(1, CancellationToken.None));

        await catalog.ApplyPatchesAsync([new CatalogPatch(1, "b.txt", null, false, null)], CancellationToken.None);
        Assert.Empty(ReadBack(await SerializeAsync(catalog, 1, null)).UnrecoverablePaths);
        Assert.Empty(await catalog.UnrecoverableAsync(1, CancellationToken.None));
    }

    // ---- Tests 10/11: the reports a check draws off the catalog -----------------------------------------------

    [Fact]
    public async Task CaseCollisions_finds_paths_differing_only_in_case()
    {
        await using var catalog = await OpenAsync();
        await ImportAsync(catalog, 1, [Entry("A.txt", 1), Entry("a.txt", 2), Entry("b.txt", 3), Entry("d/e", 4), Entry("D/e", 5)]);

        // Grouped by the folded path, and ordinal within a group, so the two halves of a collision are adjacent.
        Assert.Equal([("A.txt", 1), ("a.txt", 1), ("D/e", 1), ("d/e", 1)],
            await catalog.CaseCollisionsAsync(1, CancellationToken.None));
    }

    [Fact]
    public async Task Stats_counts_files_and_bytes()
    {
        var entries = new[] { Entry("a.txt", 10), Entry("b.txt", 20), Entry("c/d.txt", 30) };
        await using var catalog = await OpenAsync();
        await ImportAsync(catalog, 1, entries);

        Assert.Equal(((long)entries.Length, entries.Sum(e => e.Length)), await catalog.StatsAsync(1, CancellationToken.None));
        Assert.Equal((0L, 0L), await catalog.StatsAsync(99, CancellationToken.None));
    }

    // ---- Ordering: the diff walks both sides in the same order ------------------------------------------------

    [Fact]
    public async Task Entries_are_ordered_by_ordinal_path()
    {
        // "z\U0001F600" (a surrogate pair, D83D DE00 in UTF-16) and "z￿" (EF BF BF in UTF-8) disagree between
        // SQLite's BINARY (UTF-8 byte) order and .NET's ordinal (UTF-16 code unit) order: UTF-8 sorts the surrogate
        // pair after U+FFFF, ordinal sorts it before. A version containing both is the case that catches an
        // EntriesAsync still ordering by the TEXT column instead of the ordinal path_key.
        await using var catalog = await OpenAsync();
        await ImportAsync(catalog, 1,
            [Entry("b", 1), Entry("a/x", 2), Entry("A", 3), Entry("a-x", 4), Entry("z\U0001F600", 5), Entry("z￿", 6)]);

        var paths = new List<string>();
        await foreach (var e in catalog.EntriesAsync(1, CancellationToken.None))
            paths.Add(e.Path);

        Assert.Equal(paths.Order(StringComparer.Ordinal), paths);
    }

    // ---- The remaining readers ---------------------------------------------------------------------------------

    [Fact]
    public async Task Entries_stream_by_directory_by_storage_and_by_path_list()
    {
        await using var catalog = await OpenAsync();
        await ImportAsync(catalog, 1,
        [
            Entry("z.txt", 1, "Z", Pack("p0002", "z.txt")),
            Entry("d/b.txt", 2, "B", Blob("data/b")),
            Entry("d/a.txt", 3, "A", Blob("data/a")),
            Entry("d", 4, "D", Pack("p0001", "d")),
            Entry("dd/x.txt", 5, "X", Blob("data/x")),
        ]);

        // A path boundary, not a string prefix: "d" itself is in, "dd/x.txt" is not. Source order (seq) throughout.
        Assert.Equal(["d/b.txt", "d/a.txt", "d"], await Collect(catalog.EntriesUnderAsync(1, "d", CancellationToken.None)));
        Assert.Equal(["z.txt", "d/b.txt", "d/a.txt", "d", "dd/x.txt"], await Collect(catalog.EntriesUnderAsync(1, "", CancellationToken.None)));

        // Grouped by (kind, ref) so a consumer can finish one blob or pack at a time without buffering the version.
        Assert.Equal(["d/a.txt", "d/b.txt", "dd/x.txt", "d", "z.txt"], await Collect(catalog.EntriesByStorageAsync(1, CancellationToken.None)));

        var at = await catalog.EntriesAtAsync(1, ["d/a.txt", "missing.txt", "z.txt"], CancellationToken.None);
        Assert.Equal(["d/a.txt", "z.txt"], at.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Empty(await catalog.EntriesAtAsync(1, [], CancellationToken.None));
    }

    [Fact]
    public async Task Maintenance_queries_span_every_version()
    {
        await using var catalog = await OpenAsync();
        await ImportAsync(catalog, 1, [Entry("m.txt", 1, "H1", Pack("p1", "m.txt")), Entry("f.txt", 2, "F", Blob("data/f"))],
            unrecoverable: ["f.txt"]);
        await ImportAsync(catalog, 2, [Entry("m.txt", 3, "H2", Pack("p1", "m.txt")), Entry("g.txt", 4, "G", Blob("data/g"))],
            unrecoverable: ["g.txt"]);

        Assert.Equal(["data/f", "data/g", "p1"], await Collect(catalog.DistinctRefsAsync(CancellationToken.None)));

        // The latest version's copy of a member wins: its length and hash are what compaction has to weigh.
        Assert.Equal([("p1", "m.txt", 3L, "H2")], await CollectTuples(catalog.LivePackMembersAsync(CancellationToken.None)));

        var referencing = new List<(int, string)>();
        await foreach (var (v, e) in catalog.EntriesReferencingAsync("data/f", CancellationToken.None))
            referencing.Add((v, e.Path));
        Assert.Equal([(1, "f.txt")], referencing);

        var members = new List<(int, string)>();
        await foreach (var (v, e) in catalog.PackMembersAsync("p1", CancellationToken.None))
            members.Add((v, e.Path));
        Assert.Equal([(1, "m.txt"), (2, "m.txt")], members);

        Assert.Equal(["f.txt", "g.txt"], await Collect(catalog.UnrecoverableAnyVersionAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task Probes_answer_for_missing_versions_entries_and_refs()
    {
        await using var catalog = await OpenAsync();
        await ImportAsync(catalog, 1, [Entry("a.txt", 10, "A", Blob("data/a")), Entry("bad.txt", 20, "B", Blob("data/bad"))],
            emptyDirs: ["e1", "e2"], unrecoverable: ["bad.txt"]);

        Assert.True(await catalog.HeadSeenAsync(10, "A:head", CancellationToken.None));
        Assert.False(await catalog.HeadSeenAsync(20, "B:head", CancellationToken.None));  // damaged content is not "seen"
        Assert.False(await catalog.HeadSeenAsync(11, "A:head", CancellationToken.None));
        Assert.True(await catalog.IsDamagedRefAsync("data/bad", CancellationToken.None));
        Assert.False(await catalog.IsDamagedRefAsync("data/a", CancellationToken.None));

        Assert.Equal(["e1", "e2"], await catalog.EmptyDirsAsync(1, CancellationToken.None));
        Assert.Null(await catalog.GetEntryAsync(1, "nope.txt", CancellationToken.None));
        Assert.Null(await catalog.GetEntryAsync(9, "a.txt", CancellationToken.None));
        Assert.Null(await catalog.GetVersionAsync(9, CancellationToken.None));
        Assert.Empty(await catalog.ChildrenAsync(9, "", CancellationToken.None));
        AssertSameEntry(
            await catalog.GetEntryAsync(1, "a.txt", CancellationToken.None) ?? throw new InvalidOperationException("a.txt is missing"),
            (await catalog.EntriesAtAsync(1, ["a.txt"], CancellationToken.None)).Single());

        await Assert.ThrowsAsync<InvalidOperationException>(() => SerializeAsync(catalog, 9, null));
    }

    [Fact]
    public async Task Importing_a_version_again_replaces_its_rows()
    {
        await using var catalog = await OpenAsync();
        await ImportAsync(catalog, 1, [Entry("old.txt", 1), Entry("kept.txt", 2)], emptyDirs: ["a/gone"], unrecoverable: ["old.txt"]);
        await ImportAsync(catalog, 1, [Entry("kept.txt", 3)], emptyDirs: [], unrecoverable: []);

        Assert.Equal(1, (await catalog.GetVersionAsync(1, CancellationToken.None))!.EntryCount);
        Assert.Null(await catalog.GetEntryAsync(1, "old.txt", CancellationToken.None));
        Assert.Equal(3, (await catalog.GetEntryAsync(1, "kept.txt", CancellationToken.None))!.Length);
        Assert.Empty(await catalog.EmptyDirsAsync(1, CancellationToken.None));
        Assert.Empty(await catalog.UnrecoverableAsync(1, CancellationToken.None));
        Assert.Equal(0, CountRows("dirs", 1));
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private static readonly string[] Tables = ["entries", "dirs", "empty_dirs", "unrecoverable", "import_issues"];

    private static IndexEntry Entry(string path, long length, string? hash = null, StorageRef? storage = null) => new()
    {
        Path = path,
        Kind = "file",
        Length = length,
        Mtime = DateTimeOffset.UnixEpoch.AddSeconds(length),
        Permissions = "0644",
        HeadHash = hash is null ? null : hash + ":head",
        TailHash = hash is null ? null : hash + ":tail",
        FullHash = hash,
        Storage = storage,
    };

    private static StorageRef Blob(string @ref, bool raw = false, int volumes = 1, List<long>? sizes = null) =>
        new() { Kind = "blob", Ref = @ref, Raw = raw, Volumes = volumes, VolumeSizes = sizes ?? [] };

    private static StorageRef Pack(string packId, string? entryName) =>
        new() { Kind = "pack", Ref = packId, EntryName = entryName };

    private static Task ImportAsync(VersionCatalog catalog, int version, IReadOnlyList<IndexEntry> entries,
        IReadOnlyList<string>? emptyDirs = null, IReadOnlyList<string>? unrecoverable = null) =>
        catalog.ImportVersionAsync(version, version, entries.Count, Stream(entries), emptyDirs ?? [], unrecoverable ?? [], CancellationToken.None);

    private static async IAsyncEnumerable<IndexEntry> Stream(IEnumerable<IndexEntry> entries)
    {
        await Task.CompletedTask;
        foreach (var e in entries)
            yield return e;
    }

    private static async Task<byte[]> SerializeAsync(VersionCatalog catalog, int version, IReadOnlyList<CatalogPatch>? patches)
    {
        using var ms = new MemoryStream();
        await catalog.SerializeVersionAsync(version, ms, patches, CancellationToken.None);
        return ms.ToArray();
    }

    private static VersionIndex ReadBack(byte[] bytes) => IndexSerializer.DeserializeIndex(bytes);

    private static async Task<List<string>> Collect(IAsyncEnumerable<IndexEntry> entries)
    {
        var paths = new List<string>();
        await foreach (var e in entries)
            paths.Add(e.Path);
        return paths;
    }

    private static async Task<List<string>> Collect(IAsyncEnumerable<string> values)
    {
        var list = new List<string>();
        await foreach (var v in values)
            list.Add(v);
        return list;
    }

    private static async Task<List<(string, string, long, string)>> CollectTuples(
        IAsyncEnumerable<(string PackId, string EntryName, long Length, string FullHash)> rows)
    {
        var list = new List<(string, string, long, string)>();
        await foreach (var r in rows)
            list.Add((r.PackId, r.EntryName, r.Length, r.FullHash));
        return list;
    }

    /// <summary>Reads the tables the public API deliberately does not expose (row counts, import issues) through a second connection.</summary>
    private long CountRows(string table, int version) => Scalar<long>($"SELECT COUNT(*) FROM {table} WHERE version=@v", version);

    private IReadOnlyList<string> Issues(int version)
    {
        using var connection = new SqliteConnection(CatalogSql.ConnectionString(DbPath, readOnly: true));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path || '|' || issue FROM import_issues WHERE version=@v ORDER BY path, issue";
        command.Parameters.AddWithValue("@v", version);
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.GetString(0));
        return rows;
    }

    private T Scalar<T>(string sql, int version)
    {
        using var connection = new SqliteConnection(CatalogSql.ConnectionString(DbPath, readOnly: true));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@v", version);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }
}
