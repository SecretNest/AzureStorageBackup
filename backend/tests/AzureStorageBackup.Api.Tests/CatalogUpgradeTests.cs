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

    /// <summary>How many of the content-keyed indexes the file has, read off a half-converted file that no
    /// <see cref="VersionCatalog"/> would open (it is unstamped, so a read-only open converts it instead).</summary>
    private static async Task<long> GlobalIndexesOnDiskAsync(string path)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(CatalogSql.ConnectionString(path, readOnly: true));
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name IN ('entries_content', 'entries_ref', 'entries_head')";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Every index the file declares, as (name, table) pairs — read off <c>sqlite_master</c>, so an index
    /// that ended up on the wrong table, or under a name nothing creates, shows up as a difference.</summary>
    private static async Task<List<(string Name, string Table)>> IndexesOnDiskAsync(string path)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(CatalogSql.ConnectionString(path, readOnly: true));
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, tbl_name FROM sqlite_master WHERE type='index' AND sql IS NOT NULL ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync();
        var found = new List<(string, string)>();
        while (await reader.ReadAsync())
            found.Add((reader.GetString(0), reader.GetString(1)));
        return found;
    }

    /// <summary>The columns an index is keyed by, in order: the v1 <c>entries_path_key</c> is <c>(version, path_key)</c>
    /// and the v2 one is <c>(path_key)</c>, so this tells a borrowed name from the real thing.</summary>
    private static async Task<List<string>> IndexColumnsAsync(string path, string index)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(CatalogSql.ConnectionString(path, readOnly: true));
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_index_info('{index}') ORDER BY seqno";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));
        return columns;
    }

    /// <summary>The plan SQLite makes for a statement, as one line — <c>EXPLAIN QUERY PLAN</c>'s detail column.</summary>
    private static async Task<string> PlanForAsync(string path, string sql)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(CatalogSql.ConnectionString(path, readOnly: true));
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        command.Parameters.AddWithValue("@v", 1);
        await using var reader = await command.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
            lines.Add(reader.GetString(reader.GetOrdinal("detail")));
        return string.Join(" | ", lines);
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
        // a: one row; b: [1,2) [2,∞); c: [1,3) [3,4) (the mark starts a row); total 5 — the converted file is the
        // shape a run's own imports would have left, not eleven rows of the format it came from.
        Assert.Equal(5, await CatalogV2Tests.CountAsync(catalog, "SELECT COUNT(*) FROM entries"));
        var rows = await catalog.HistoryRowsAsync(CancellationToken.None);   // versions' declared counts survive
        Assert.Equal(history.Sum(v => v.Entries.Count), rows);
    }

    /// <summary>The conversion reads every version of the old table twice, once in path order and once in seq order.
    /// Both reads have to come off a format-1 index: without one, each is a primary-key search plus a temp B-tree
    /// over the whole version — the path-ordered one carrying the entire entry payload — and a sorter that runs out
    /// of temp room raises a plain SqliteException, which the store answers by deleting the catalog and downloading
    /// the history again.</summary>
    [Fact]
    public async Task The_conversion_reads_the_old_table_through_the_indexes_it_kept()
    {
        var store = new VersionCatalogStore(_root);
        var path = store.PathFor(AccountId, Container);
        await LegacyCatalogFixture.WriteAsync(path, History());

        // Stop mid-conversion, so the file still holds the old tables the reads below are planned against.
        using var cts = new CancellationTokenSource();
        var stopAfterFirst = new InlineProgress<CatalogUpgradeProgress>(r => { if (r.VersionDone && r.Version == 1) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpgradeAsync(AccountId, Container, stopAfterFirst, cts.Token));

        var byPath = await PlanForAsync(path, CatalogUpgrade.V1EntriesSql);
        Assert.Contains("USING INDEX entries_path_key", byPath, StringComparison.Ordinal);
        Assert.DoesNotContain("TEMP B-TREE", byPath, StringComparison.Ordinal);

        var bySeq = await PlanForAsync(path, CatalogUpgrade.V1SeqOrderSql);
        Assert.Contains("USING INDEX entries_seq", bySeq, StringComparison.Ordinal);
        Assert.DoesNotContain("TEMP B-TREE", bySeq, StringComparison.Ordinal);

        // And the other side of the same merge: the new table's covering cursor, which walks the rows current at the
        // version being converted in path_key order. The real entries_path_key's name is still on the old table
        // while the conversion runs, which is why the new table carries the same index under a name of its own.
        var covering = await PlanForAsync(path,
            "SELECT id, path_key FROM entries WHERE version_from <= @v AND version_to > @v ORDER BY path_key");
        Assert.DoesNotContain("TEMP B-TREE", covering, StringComparison.Ordinal);
    }

    /// <summary>A converted file is indexed exactly like one this build created from scratch. The conversion borrows
    /// the name <c>entries_path_key</c> for the old table for as long as it reads it, and a
    /// <c>CREATE INDEX IF NOT EXISTS</c> under a borrowed name is a silent no-op — so "the schema pass ran" is not
    /// evidence that the index is there, or that it is keyed the way format 2 needs.</summary>
    [Fact]
    public async Task A_converted_catalog_carries_exactly_the_indexes_a_fresh_one_does()
    {
        var store = new VersionCatalogStore(_root);
        var path = store.PathFor(AccountId, Container);
        await LegacyCatalogFixture.WriteAsync(path, History());
        await store.UpgradeAsync(AccountId, Container, progress: null, CancellationToken.None);

        var fresh = Path.Combine(_root, "fresh", "catalog.db");
        Directory.CreateDirectory(Path.GetDirectoryName(fresh)!);
        await (await VersionCatalog.OpenAsync(fresh, readOnly: false, CancellationToken.None)).DisposeAsync();

        var converted = await IndexesOnDiskAsync(path);
        Assert.Equal(await IndexesOnDiskAsync(fresh), converted);
        Assert.Contains(("entries_path_key", "entries"), converted);
        Assert.Contains(("entries_parent", "entries"), converted);
        Assert.Contains(("dirs_parent", "dirs"), converted);
        Assert.Equal(["path_key"], await IndexColumnsAsync(path, "entries_path_key"));       // not the v1 (version, path_key)
        Assert.Equal(["parent", "path_key"], await IndexColumnsAsync(path, "entries_parent"));
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
        // Mid-conversion the content-keyed indexes are down: a version converting with them live inserts its rows
        // at random into three trees spanning the whole history (CatalogSql.GlobalIndexNames).
        Assert.Equal(0, await GlobalIndexesOnDiskAsync(path));

        var resumed = new List<CatalogUpgradeProgress>();
        await new VersionCatalogStore(_root).UpgradeAsync(AccountId, Container, new InlineProgress<CatalogUpgradeProgress>(resumed.Add), CancellationToken.None);
        Assert.DoesNotContain(resumed, r => r.Version <= 2 && !r.VersionDone);  // no rows re-imported for 1 and 2
        // The counts line's two numbers are the whole history's, not this run's: the loop iterates versions 3 and 4
        // only, so a stage left to count readings would finish at "2 of 4" — or, told the history's size by the
        // info file, at "2 of 10" against 100% of the rows.
        Assert.All(resumed, r => Assert.Equal(history.Count, r.VersionsTotal));
        Assert.Equal(2, resumed[0].VersionsDone);                               // 1 and 2 were already in
        Assert.Equal(history.Count, resumed[^1].VersionsDone);                  // and the last reading has them all
        await using var catalog = await VersionCatalog.OpenAsync(path, readOnly: true, CancellationToken.None);
        foreach (var index in history)
            Assert.Equal(LegacyIndexSerializer.SerializeIndex(index), await SerializeAsync(catalog, index.Version));
        // The conversion runs with the content-keyed indexes down and sorts them back at the end; the resumed run
        // is the one that finishes, so it is the one that owes them.
        Assert.Equal(CatalogSql.GlobalIndexNames.Count, await catalog.GlobalIndexCountAsync(CancellationToken.None));
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
