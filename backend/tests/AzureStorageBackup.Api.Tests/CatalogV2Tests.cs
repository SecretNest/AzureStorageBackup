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

    /// <summary>The other half of the removal: retiring the oldest version leaves the newest's open-ended rows
    /// exactly where they were. Only a removal that lowers the newest version can strand a row above it, which is
    /// why that scan is skipped here — and a skip that took the reachable rows with it would show up as a missing
    /// interval below.</summary>
    [Fact]
    public async Task Removing_the_oldest_version_leaves_the_rows_the_newest_still_reaches()
    {
        await using var catalog = await OpenAsync();
        var a1 = Entry("a", 1, "h1");
        var b = Entry("b", 1, "hb");
        var v1 = Version(1, a1, b);
        var v2 = Version(2, a1 with { Length = 2 }, b);
        var v3 = Version(3, a1 with { Length = 3 }, b);
        foreach (var index in new[] { v1, v2, v3 })
            await ImportAsync(catalog, index);

        await catalog.RemoveVersionAsync(1, CancellationToken.None);                    // the oldest

        Assert.Equal(3, await CountAsync(catalog, "SELECT COUNT(*) FROM entries"));    // only a's [1,2) was v1's alone
        // a's [3,∞) and b's [1,∞): open-ended and untouched, since the newest version did not move.
        Assert.Equal(2, await CountAsync(catalog, $"SELECT COUNT(*) FROM entries WHERE version_to = {int.MaxValue}"));
        Assert.Equal(1, await CountAsync(catalog, "SELECT COUNT(*) FROM entries WHERE version_from = 1"));
        Assert.Equal(Bytes(v2), await SerializeAsync(catalog, 2));
        Assert.Equal(Bytes(v3), await SerializeAsync(catalog, 3));
    }

    /// <summary>A patch may not widen the row it lands on. After the middle version goes, the path's first row still
    /// ends where the path changed; isolating version 1 for a patch must leave that end alone, or a later re-import
    /// of version 2 would find the path unchanged and give it version 1's entry.</summary>
    [Fact]
    public async Task A_patch_does_not_stretch_the_row_over_a_removed_version()
    {
        await using var catalog = await OpenAsync();
        var a = Entry("a", 1, "h1");
        foreach (var index in new[] { Version(1, a), Version(2, a with { Length = 2 }), Version(3, a with { Length = 3 }) })
            await ImportAsync(catalog, index);
        await catalog.RemoveVersionAsync(2, CancellationToken.None);
        Assert.Equal(2, await CountAsync(catalog, "SELECT COUNT(*) FROM entries"));    // [1,2) and [3,∞)

        await catalog.ApplyPatchesAsync([new CatalogPatch(1, "a", null, true, null)], CancellationToken.None);

        Assert.Equal(2, await CountAsync(catalog, "SELECT COUNT(*) FROM entries"));    // nothing split
        Assert.Equal(1, await CountAsync(catalog, "SELECT COUNT(*) FROM entries WHERE version_from = 1 AND version_to = 2"));

        // And the re-imported version 2 is its own entry again, not the patched version 1's.
        var v2 = Version(2, a with { Length = 2 });
        await ImportAsync(catalog, v2);
        Assert.Equal(Bytes(v2), await SerializeAsync(catalog, 2));
        Assert.Equal(2, (await catalog.GetEntryAsync(2, "a", CancellationToken.None))!.Length);
        Assert.Equal(["a"], await catalog.UnrecoverableAsync(1, CancellationToken.None));
        Assert.Empty(await catalog.UnrecoverableAsync(2, CancellationToken.None));
    }

    /// <summary>An import that has to be staged only records its order when that order is not the path order: the
    /// order table is a row per entry per version, which is the shape this format exists to stop storing.</summary>
    [Fact]
    public async Task Only_an_import_that_is_out_of_path_order_records_one()
    {
        await using var catalog = await OpenAsync();
        IndexEntry[] entries = [Entry("a", 1, "h1"), Entry("b", 2, "h2"), Entry("c", 3, "h3")];
        await catalog.ImportVersionAsync(1, 1, entries.Length, entries.ToAsyncEnumerable(), [], [], CancellationToken.None);
        Assert.Equal(0, await CountAsync(catalog, "SELECT COUNT(*) FROM entry_order"));

        IndexEntry[] shuffled = [entries[2], entries[0], entries[1]];
        await catalog.ImportVersionAsync(2, 2, shuffled.Length, shuffled.ToAsyncEnumerable(), [], [], CancellationToken.None);
        Assert.Equal(3, await CountAsync(catalog, "SELECT COUNT(*) FROM entry_order WHERE version = 2"));
        Assert.Equal(0, await CountAsync(catalog, "SELECT COUNT(*) FROM entry_order WHERE version = 1"));

        // Both versions serialize in the order they were handed over in.
        Assert.Equal(Bytes(new VersionIndex { Version = 1, Entries = [.. entries] }), await SerializeAsync(catalog, 1));
        Assert.Equal(Bytes(new VersionIndex { Version = 2, Entries = [.. shuffled] }), await SerializeAsync(catalog, 2));
    }

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
}
