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
