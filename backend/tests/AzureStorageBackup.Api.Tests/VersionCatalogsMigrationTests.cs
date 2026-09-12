using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// <see cref="VersionCatalogs"/> is the lazy migration chain: catalog hit → the version's <c>.idx</c> file → the
/// legacy <c>CachedVersionIndexes</c> row → the cloud. Each test picks the source that should answer a given
/// <see cref="VersionCatalogs.EnsureVersionAsync"/> call and asserts that source (and only that source) was used,
/// and that whatever it consumed on the way (a <c>.idx</c> file, a legacy row) is gone afterward.
/// <para>
/// The last section covers <see cref="VersionIndexFileStore"/> on its own — the reader the chain's second link goes
/// through, and the two deletes retention and config removal call. Nothing writes those files any more, so the
/// class has no test of its own left to belong to.
/// </para>
/// </summary>
public sealed class VersionCatalogsMigrationTests
{
    private const int AccountId = 3;
    private const string Container = "photos";
    private const long Identity = 20260907L;

    private static readonly Account TestAccount = new() { Id = AccountId };

    private static AppDbContext NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static BackupVersion Version(int version = 1) => new()
    {
        Version = version,
        IndexBlob = $"idx/{version}",
        IndexVolumes = 1,
        CreatedAt = DateTimeOffset.UtcNow,
        Stats = new VersionStats(0, 0, 0, 0),
    };

    /// <summary>Round-trips the catalog's stored version back out and compares it entry-by-entry against the
    /// sample that should have gone in — stronger than an entry count, since a wrong-but-same-length import would
    /// still pass a count check.</summary>
    private static async Task AssertCatalogHasSampleAsync(VersionCatalog catalog, int version, VersionIndex sample)
    {
        using var ms = new MemoryStream();
        await catalog.SerializeVersionAsync(version, ms, patches: null, CancellationToken.None);
        ms.Position = 0;
        using var reader = new IndexStreamReader(ms);
        var actual = reader.Entries().ToList();
        Assert.Equal(sample.Entries.Count, actual.Count);
        for (var i = 0; i < sample.Entries.Count; i++)
            IndexAssert.AssertSameEntry(sample.Entries[i], actual[i]);
    }

    private static List<NSubstitute.Core.ICall> WarningCalls(ILogger<VersionCatalogs> logger) =>
        LogCalls(logger, LogLevel.Warning);

    private static List<NSubstitute.Core.ICall> ErrorCalls(ILogger<VersionCatalogs> logger) =>
        LogCalls(logger, LogLevel.Error);

    private static List<NSubstitute.Core.ICall> LogCalls(ILogger<VersionCatalogs> logger, LogLevel level) =>
        [.. logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log))
            .Where(c => c.GetArguments()[0] is LogLevel actual && actual == level)];

    /// <summary>The cloud half of the migration chain, wired to hand back exactly these bytes as the version's
    /// downloaded index.</summary>
    private static void CloudReturns(IBackupInfoStore infoStore, byte[] bytes) =>
        infoStore.ReadIndexToFileAsync(
                Arg.Any<Account>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                File.WriteAllBytes(ci.ArgAt<string>(5), bytes);
                return Task.CompletedTask;
            });

    // ---- Test 1: a catalog hit touches nothing else --------------------------------------------------------------

    [Fact]
    public async Task Ensure_uses_the_catalog_when_present_and_touches_nothing_else()
    {
        using var db = NewDb();
        var infoStore = Substitute.For<IBackupInfoStore>();
        var catalogs = TestCatalogs.New(db, infoStore);
        var sample = IndexSamples.Sample();
        var bytes = LegacyIndexSerializer.SerializeIndex(sample);

        using (var held = await catalogs.LockForWriteAsync(AccountId, Container, CancellationToken.None))
        await using (var catalog = await catalogs.OpenForWriteAsync(held, AccountId, Container, CancellationToken.None))
        {
            using var reader = new IndexStreamReader(new MemoryStream(bytes));
            await catalog.ImportVersionAsync(1, Identity, reader, CancellationToken.None);
        }

        await catalogs.EnsureVersionAsync(TestAccount, Container, Version(), Identity, password: null, CancellationToken.None);

        await infoStore.DidNotReceive().ReadIndexToFileAsync(
            Arg.Any<Account>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    // ---- Test 2: a matching .idx file is imported and deleted ------------------------------------------------------

    [Fact]
    public async Task Ensure_imports_a_matching_idx_file_and_deletes_it()
    {
        using var db = NewDb();
        var infoStore = Substitute.For<IBackupInfoStore>();
        var files = TestIndexFiles.New();
        var catalogs = TestCatalogs.New(db, infoStore, files);
        var sample = IndexSamples.Sample();
        var bytes = LegacyIndexSerializer.SerializeIndex(sample);
        await TestIndexFiles.WriteAsync(files, AccountId, Container, 1, Identity, bytes, CancellationToken.None);

        await catalogs.EnsureVersionAsync(TestAccount, Container, Version(), Identity, password: null, CancellationToken.None);

        await using var catalog = await catalogs.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None);
        await AssertCatalogHasSampleAsync(catalog, 1, sample);
        Assert.False(File.Exists(files.PathFor(AccountId, Container, 1)));
        await infoStore.DidNotReceive().ReadIndexToFileAsync(
            Arg.Any<Account>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    // ---- Test 3: an .idx file under a stale identity is left alone and the chain falls through ----------------------

    [Fact]
    public async Task Ensure_ignores_an_idx_file_with_the_wrong_identity_and_falls_through()
    {
        using var db = NewDb();
        var infoStore = Substitute.For<IBackupInfoStore>();
        var sample = IndexSamples.Sample();
        var bytes = LegacyIndexSerializer.SerializeIndex(sample);
        infoStore.ReadIndexToFileAsync(
                Arg.Any<Account>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                File.WriteAllBytes(ci.ArgAt<string>(5), bytes);
                return Task.CompletedTask;
            });
        var files = TestIndexFiles.New();
        var catalogs = TestCatalogs.New(db, infoStore, files);

        const long staleIdentity = Identity - 1;
        await TestIndexFiles.WriteAsync(files, AccountId, Container, 1, staleIdentity, bytes, CancellationToken.None);

        await catalogs.EnsureVersionAsync(TestAccount, Container, Version(), Identity, password: null, CancellationToken.None);

        await using var catalog = await catalogs.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None);
        await AssertCatalogHasSampleAsync(catalog, 1, sample);

        // The stale file is untouched: still there, and still openable under its own (old) identity — proof nothing
        // overwrote or deleted it, only that it was skipped.
        Assert.True(File.Exists(files.PathFor(AccountId, Container, 1)));
        await using (var stale = await files.OpenBodyAsync(AccountId, Container, 1, staleIdentity, CancellationToken.None))
            Assert.NotNull(stale);
        Assert.Null(await files.OpenBodyAsync(AccountId, Container, 1, Identity, CancellationToken.None));
    }

    // ---- Test 4: a legacy CachedVersionIndexes row is imported and dropped -------------------------------------------

    [Fact]
    public async Task Ensure_imports_a_legacy_row_and_drops_it()
    {
        using var db = NewDb();
        var infoStore = Substitute.For<IBackupInfoStore>();
        var catalogs = TestCatalogs.New(db, infoStore);
        var sample = IndexSamples.Sample();
        var bytes = LegacyIndexSerializer.SerializeIndex(sample);

        db.CachedVersionIndexes.Add(new CachedVersionIndex
        {
            AccountId = AccountId,
            Container = Container,
            Version = 1,
            IdentityTicks = Identity,
            Bytes = bytes,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await catalogs.EnsureVersionAsync(TestAccount, Container, Version(), Identity, password: null, CancellationToken.None);

        await using var catalog = await catalogs.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None);
        await AssertCatalogHasSampleAsync(catalog, 1, sample);
        Assert.False(await db.CachedVersionIndexes.AnyAsync(
            x => x.AccountId == AccountId && x.Container == Container && x.Version == 1));
        await infoStore.DidNotReceive().ReadIndexToFileAsync(
            Arg.Any<Account>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    // ---- Test 5: nothing local exists, so the chain reaches the cloud --------------------------------------------

    [Fact]
    public async Task Ensure_downloads_from_the_cloud_when_nothing_local_exists()
    {
        using var db = NewDb();
        var infoStore = Substitute.For<IBackupInfoStore>();
        var sample = IndexSamples.Sample();
        var bytes = LegacyIndexSerializer.SerializeIndex(sample);
        infoStore.ReadIndexToFileAsync(
                Arg.Any<Account>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                File.WriteAllBytes(ci.ArgAt<string>(5), bytes);
                return Task.CompletedTask;
            });
        var catalogs = TestCatalogs.New(db, infoStore);

        await catalogs.EnsureVersionAsync(TestAccount, Container, Version(), Identity, password: null, CancellationToken.None);

        await using var catalog = await catalogs.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None);
        await AssertCatalogHasSampleAsync(catalog, 1, sample);
    }

    // ---- Test 6: a failed cloud read leaves no version row --------------------------------------------------------

    [Fact]
    public async Task Ensure_on_a_failed_cloud_read_leaves_no_version_row()
    {
        using var db = NewDb();
        var infoStore = Substitute.For<IBackupInfoStore>();
        infoStore.ReadIndexToFileAsync(
                Arg.Any<Account>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("cloud unavailable"));
        var catalogs = TestCatalogs.New(db, infoStore);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalogs.EnsureVersionAsync(TestAccount, Container, Version(), Identity, password: null, CancellationToken.None));

        await using var catalog = await catalogs.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None);
        Assert.Null(await catalog.GetVersionAsync(1, CancellationToken.None));
    }

    // ---- Bulk: a whole history in one call takes the content-keyed indexes down and puts them back --------------------

    [Fact]
    public async Task EnsureVersions_imports_every_missing_version_and_ends_with_the_global_indexes_in_place()
    {
        using var db = NewDb();
        var infoStore = Substitute.For<IBackupInfoStore>();
        var files = TestIndexFiles.New();
        var catalogs = TestCatalogs.New(db, infoStore, files);
        var sample = IndexSamples.Sample();
        var bytes = LegacyIndexSerializer.SerializeIndex(sample);
        var versions = new List<BackupVersion>();
        for (var v = 1; v <= 3; v++)
        {
            await TestIndexFiles.WriteAsync(files, AccountId, Container, v, Identity, bytes, CancellationToken.None);
            versions.Add(Version(v));
        }

        var reported = new List<VersionLoadProgress>();
        var progress = new InlineProgress<VersionLoadProgress>(reported.Add);
        await catalogs.EnsureVersionsAsync(TestAccount, Container, versions, Identity, password: null, progress, CancellationToken.None);

        await using var catalog = await catalogs.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None);
        for (var v = 1; v <= 3; v++)
        {
            await AssertCatalogHasSampleAsync(catalog, v, sample);
            Assert.False(File.Exists(files.PathFor(AccountId, Container, v)));
        }

        // Three or more missing is the bulk path, and the bulk path is only correct if it rebuilds what it dropped.
        Assert.Equal(CatalogSql.GlobalIndexNames.Count, await catalog.GlobalIndexCountAsync(CancellationToken.None));
        // Every version ends with an Imported event carrying its row count, in the order asked.
        var ends = reported.Where(r => r.Event == VersionLoadEvent.Imported).ToList();
        Assert.Equal([1, 2, 3], ends.Select(r => r.Version));
        Assert.All(ends, r => Assert.Equal(sample.Entries.Count, r.Entries));
        Assert.DoesNotContain(reported, r => r.Event == VersionLoadEvent.Present);
        await infoStore.DidNotReceive().ReadIndexToFileAsync(
            Arg.Any<Account>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureVersions_finds_everything_present_and_reports_the_full_count_without_a_write_open()
    {
        using var db = NewDb();
        var infoStore = Substitute.For<IBackupInfoStore>();
        var files = TestIndexFiles.New();
        var catalogs = TestCatalogs.New(db, infoStore, files);
        var bytes = LegacyIndexSerializer.SerializeIndex(IndexSamples.Sample());
        var versions = new List<BackupVersion> { Version(1), Version(2) };
        foreach (var v in versions)
            await TestIndexFiles.WriteAsync(files, AccountId, Container, v.Version, Identity, bytes, CancellationToken.None);
        await catalogs.EnsureVersionsAsync(TestAccount, Container, versions, Identity, password: null, null, CancellationToken.None);

        var reported = new List<VersionLoadProgress>();
        await catalogs.EnsureVersionsAsync(TestAccount, Container, versions, Identity, password: null, new InlineProgress<VersionLoadProgress>(reported.Add), CancellationToken.None);

        Assert.Equal([1, 2], reported.Select(r => r.Version));
        Assert.All(reported, r => Assert.Equal(VersionLoadEvent.Present, r.Event));
    }

    /// <summary>A migration that dies halfway (here: the second version has no source left anywhere) must not leave
    /// the catalog wrong — only slower. The versions that did import stay; the indexes it dropped are absent until
    /// the next write open, whose schema pass puts them back.</summary>
    [Fact]
    public async Task EnsureVersions_interrupted_keeps_what_imported_and_the_next_write_open_rebuilds_the_indexes()
    {
        using var db = NewDb();
        var infoStore = Substitute.For<IBackupInfoStore>();
        infoStore.ReadIndexToFileAsync(
                Arg.Any<Account>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("cloud unavailable"));
        var files = TestIndexFiles.New();
        var catalogs = TestCatalogs.New(db, infoStore, files);
        var sample = IndexSamples.Sample();
        var bytes = LegacyIndexSerializer.SerializeIndex(sample);
        await TestIndexFiles.WriteAsync(files, AccountId, Container, 1, Identity, bytes, CancellationToken.None);
        // version 2: no .idx, no legacy row, and the cloud throws

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalogs.EnsureVersionsAsync(
            TestAccount, Container, [Version(1), Version(2)], Identity, password: null, null, CancellationToken.None));

        await using (var readOnly = await catalogs.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None))
        {
            await AssertCatalogHasSampleAsync(readOnly, 1, sample);
            Assert.Null(await readOnly.GetVersionAsync(2, CancellationToken.None));
            Assert.Equal(0, await readOnly.GlobalIndexCountAsync(CancellationToken.None));
        }

        using var held = await catalogs.LockForWriteAsync(AccountId, Container, CancellationToken.None);
        await using var writer = await catalogs.OpenForWriteAsync(held, AccountId, Container, CancellationToken.None);
        Assert.Equal(CatalogSql.GlobalIndexNames.Count, await writer.GlobalIndexCountAsync(CancellationToken.None));
    }

    /// <summary>One missing version used to keep the indexes live unconditionally — "the rebuild costs more than
    /// one version's random inserts" — which stopped being true once the history outgrew the cache. Now the one
    /// version takes the bracket by the same rule as the run's own import: at least a hundredth of the history.
    /// Observed the same way as the interrupted migration above: the source fails after the drop, so the missing
    /// indexes are the evidence that the bracket was taken.</summary>
    [Fact]
    public async Task EnsureVersions_brackets_a_single_missing_version_that_is_large_against_the_history()
    {
        using var db = NewDb();
        var infoStore = Substitute.For<IBackupInfoStore>();
        infoStore.ReadIndexToFileAsync(
                Arg.Any<Account>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("cloud unavailable"));
        var files = TestIndexFiles.New();
        var catalogs = TestCatalogs.New(db, infoStore, files);
        var sample = IndexSamples.Sample();
        await TestIndexFiles.WriteAsync(files, AccountId, Container, 1, Identity, LegacyIndexSerializer.SerializeIndex(sample), CancellationToken.None);
        await catalogs.EnsureVersionsAsync(TestAccount, Container, [Version(1)], Identity, password: null, null, CancellationToken.None);

        // Version 2 declares as many rows as the whole history holds, and its only source throws.
        var large = Version(2) with { Stats = new VersionStats(sample.Entries.Count, 0, 0, 0) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalogs.EnsureVersionsAsync(
            TestAccount, Container, [Version(1), large], Identity, password: null, null, CancellationToken.None));

        await using var readOnly = await catalogs.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None);
        Assert.Equal(0, await readOnly.GlobalIndexCountAsync(CancellationToken.None));
    }

    /// <summary>The other side of the rule: a small version against a history it is a tiny fraction of keeps the
    /// indexes live, as one missing version always did.</summary>
    [Fact]
    public async Task EnsureVersions_keeps_the_indexes_live_for_a_single_small_missing_version()
    {
        using var db = NewDb();
        var infoStore = Substitute.For<IBackupInfoStore>();
        infoStore.ReadIndexToFileAsync(
                Arg.Any<Account>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("cloud unavailable"));
        var files = TestIndexFiles.New();
        var catalogs = TestCatalogs.New(db, infoStore, files);
        var sample = IndexSamples.Sample();
        await TestIndexFiles.WriteAsync(files, AccountId, Container, 1, Identity, LegacyIndexSerializer.SerializeIndex(sample), CancellationToken.None);
        await catalogs.EnsureVersionsAsync(TestAccount, Container, [Version(1)], Identity, password: null, null, CancellationToken.None);

        // Declared as a hundredth of the history less one row: the random inserts are the cheaper side.
        var history = sample.Entries.Count;
        var small = Version(2) with { Stats = new VersionStats(Math.Max(0, history / 100 - 1), 0, 0, 0) };
        Assert.False(VersionCatalog.PrefersRebuild(small.Stats.Files, history), "the sample must sit below the rule for this test to mean anything");
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalogs.EnsureVersionsAsync(
            TestAccount, Container, [Version(1), small], Identity, password: null, null, CancellationToken.None));

        await using var readOnly = await catalogs.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None);
        Assert.Equal(CatalogSql.GlobalIndexNames.Count, await readOnly.GlobalIndexCountAsync(CancellationToken.None));
    }

    // ---- Test 7: removing a container drops the catalog and every legacy row ------------------------------------------

    [Fact]
    public async Task RemoveContainer_removes_catalog_and_legacy_rows()
    {
        using var db = NewDb();
        var infoStore = Substitute.For<IBackupInfoStore>();
        var sample = IndexSamples.Sample();
        var bytes = LegacyIndexSerializer.SerializeIndex(sample);
        infoStore.ReadIndexToFileAsync(
                Arg.Any<Account>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                File.WriteAllBytes(ci.ArgAt<string>(5), bytes);
                return Task.CompletedTask;
            });
        var files = TestIndexFiles.New();
        var catalogs = TestCatalogs.New(db, infoStore, files);

        // A version already migrated into the catalog...
        await catalogs.EnsureVersionAsync(TestAccount, Container, Version(1), Identity, password: null, CancellationToken.None);
        // ...an .idx file for a version nobody has asked for yet...
        await TestIndexFiles.WriteAsync(files, AccountId, Container, 2, Identity, bytes, CancellationToken.None);
        // ...and a leftover pre-migration row that predates the .idx file store entirely.
        db.CachedVersionIndexes.Add(new CachedVersionIndex
        {
            AccountId = AccountId,
            Container = Container,
            Version = 3,
            IdentityTicks = Identity,
            Bytes = bytes,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        await catalogs.RemoveContainerAsync(AccountId, Container, CancellationToken.None);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => catalogs.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None));
        Assert.False(File.Exists(files.PathFor(AccountId, Container, 2)));
        Assert.False(await db.CachedVersionIndexes.AnyAsync(x => x.AccountId == AccountId && x.Container == Container));
    }

    // ---- Test 8: a .idx file whose header matches but whose body cannot be parsed is deleted and logged --------------

    [Fact]
    public async Task Ensure_deletes_a_corrupt_idx_file_logs_and_leaves_no_row_when_no_fallback_succeeds()
    {
        using var db = NewDb();
        var infoStore = Substitute.For<IBackupInfoStore>();
        infoStore.ReadIndexToFileAsync(
                Arg.Any<Account>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("cloud unavailable"));
        var files = TestIndexFiles.New();
        var logger = Substitute.For<ILogger<VersionCatalogs>>();
        var catalogs = new VersionCatalogs(TestCatalogs.NewStore(), files, db, infoStore, logger, TestCatalogs.NewTempRoot());

        var sample = IndexSamples.Sample();
        var fullBytes = LegacyIndexSerializer.SerializeIndex(sample);
        // format(1) + version(4) + entryCount(4): a header IndexStreamReader parses fine, claiming entries that
        // are not there — exactly what a file truncated mid-write would look like.
        var corrupt = fullBytes[..9];
        await TestIndexFiles.WriteAsync(files, AccountId, Container, 1, Identity, corrupt, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalogs.EnsureVersionAsync(TestAccount, Container, Version(), Identity, password: null, CancellationToken.None));

        Assert.False(File.Exists(files.PathFor(AccountId, Container, 1)));
        Assert.Single(WarningCalls(logger));

        await using var catalog = await catalogs.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None);
        Assert.Null(await catalog.GetVersionAsync(1, CancellationToken.None));
    }

    // ---- Test 9: a catalog file SQLite cannot read is a cache miss, not a wall ------------------------------------

    [Fact]
    public async Task Ensure_rebuilds_a_catalog_that_is_not_a_database_at_all()
    {
        using var db = NewDb();
        var infoStore = Substitute.For<IBackupInfoStore>();
        var sample = IndexSamples.Sample();
        CloudReturns(infoStore, LegacyIndexSerializer.SerializeIndex(sample));
        var store = TestCatalogs.NewStore();
        var logger = Substitute.For<ILogger<VersionCatalogs>>();
        var catalogs = new VersionCatalogs(
            store, TestIndexFiles.New(), db, infoStore, logger, TestCatalogs.NewTempRoot());

        // 4096 bytes of noise where the catalog should be — a power loss mid-write, a half-restored backup of the
        // cache directory. The read-only probe's pragmas go through it happily and the first SELECT is what raises
        // SQLITE_NOTADB, which used to escape EnsureVersionAsync and fail every backup, check, restore, retention
        // round and UI browse of the container until somebody deleted the file by hand.
        var path = store.PathFor(AccountId, Container);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var garbage = new byte[4096];
        Random.Shared.NextBytes(garbage);
        await File.WriteAllBytesAsync(path, garbage);

        await catalogs.EnsureVersionAsync(TestAccount, Container, Version(), Identity, password: null, CancellationToken.None);

        // The version is in, from the cloud, and the file at that path is a catalog again.
        await using var catalog = await catalogs.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None);
        await AssertCatalogHasSampleAsync(catalog, 1, sample);
        Assert.Single(WarningCalls(logger));
    }

    // ---- Test 10: patches that cannot be written invalidate the version instead of leaving it stale ---------------

    [SkippableFact]
    public async Task Patches_that_cannot_be_applied_drop_the_version_so_the_cloud_is_read_again()
    {
        Skip.If(OperatingSystem.IsWindows(), "The failure is provoked with POSIX file permissions.");

        using var db = NewDb();
        var infoStore = Substitute.For<IBackupInfoStore>();
        var store = TestCatalogs.NewStore();
        var logger = Substitute.For<ILogger<VersionCatalogs>>();
        var catalogs = new VersionCatalogs(
            store, TestIndexFiles.New(), db, infoStore, logger, TestCatalogs.NewTempRoot());

        // What the catalog holds now: the version as it was BEFORE the check marked anything.
        var marked = IndexSamples.Sample();          // carries "gone.txt" as unrecoverable
        var unmarked = marked with { UnrecoverablePaths = [] };
        using (var held = await catalogs.LockForWriteAsync(AccountId, Container, CancellationToken.None))
        await using (var catalog = await catalogs.OpenForWriteAsync(held, AccountId, Container, CancellationToken.None))
        {
            using var reader = new IndexStreamReader(new MemoryStream(LegacyIndexSerializer.SerializeIndex(unmarked)));
            await catalog.ImportVersionAsync(1, Identity, reader, CancellationToken.None);
        }

        // What the cloud holds: the rewritten index, marks and all — the checker uploads it before it ever touches
        // the catalog, so this is the state the local file has to be reconciled to and not the other way round.
        CloudReturns(infoStore, LegacyIndexSerializer.SerializeIndex(marked));

        // …and the catalog file cannot be written. A disk that filled up, a volume remounted read-only: whatever
        // the cause, the patch below fails after the cloud write has already succeeded.
        var path = store.PathFor(AccountId, Container);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        // The thrown type is SQLite's own (a read-only file), so ThrowsAny<Exception> is what fits — the point is
        // that something comes out, not which exception shape.
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => catalogs.ApplyPatchesOrInvalidateAsync(
            AccountId, Container, [new CatalogPatch(1, "gone.txt", null, true, null)], log: null,
            CancellationToken.None));
        Assert.IsNotType<OperationCanceledException>(thrown);

        // Reported, not swallowed…
        Assert.NotEmpty(ErrorCalls(logger));
        // …and the stale rows are gone rather than sitting there under an identity that says "already imported".
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => catalogs.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None));

        // So the very next reader migrates the version back in from the cloud — with the mark on it.
        await catalogs.EnsureVersionAsync(TestAccount, Container, Version(), Identity, password: null, CancellationToken.None);
        await using var rebuilt = await catalogs.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None);
        Assert.Equal(["gone.txt"], await rebuilt.UnrecoverableAsync(1, CancellationToken.None));
    }

    // ---- Test 11: the probe's self-heal still works once the path has already been checked in this process -------

    [Fact]
    public async Task Ensure_rebuilds_a_catalog_that_went_bad_after_a_prior_write_open_in_this_process()
    {
        using var db = NewDb();
        var infoStore = Substitute.For<IBackupInfoStore>();
        var store = TestCatalogs.NewStore();
        var logger = Substitute.For<ILogger<VersionCatalogs>>();
        var catalogs = new VersionCatalogs(
            store, TestIndexFiles.New(), db, infoStore, logger, TestCatalogs.NewTempRoot());

        var sample = IndexSamples.Sample();
        var bytes = LegacyIndexSerializer.SerializeIndex(sample);

        // A write open puts this path in VersionCatalogStore's _checkedPaths, so quick_check runs once and never
        // again for it in this process — the thing the read-only probe's SQLITE_CORRUPT/SQLITE_NOTADB catch has to
        // undo, or the locked write path below opens the damaged file straight through (its own pragmas never touch
        // the damaged page) and the first SELECT throws uncaught instead of self-healing.
        using (var held = await catalogs.LockForWriteAsync(AccountId, Container, CancellationToken.None))
        await using (var catalog = await catalogs.OpenForWriteAsync(held, AccountId, Container, CancellationToken.None))
        {
            using var reader = new IndexStreamReader(new MemoryStream(bytes));
            await catalog.ImportVersionAsync(sample.Version, Identity, reader, CancellationToken.None);
        }

        // One flipped byte at an offset found by scanning a byte at a time for a spot where opening and its own
        // pragmas notice nothing but the first SELECT does. It lands on page 1, inside the stored CREATE TABLE text
        // of the versions table, so it depends on the schema text as written today: an edit to that DDL moves the
        // byte and this test must be re-scanned for a new offset. Open passes because the header is intact, and
        // CREATE TABLE IF NOT EXISTS never re-parses stored DDL for a table whose name is present.
        var path = store.PathFor(AccountId, Container);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(3993, SeekOrigin.Begin);
            var b = (byte)fs.ReadByte();
            fs.Seek(3993, SeekOrigin.Begin);
            fs.WriteByte((byte)(b ^ 0xFF));
        }
        try { File.Delete(path + "-wal"); } catch { /* may not exist */ }
        try { File.Delete(path + "-shm"); } catch { /* may not exist */ }

        CloudReturns(infoStore, bytes);

        await catalogs.EnsureVersionAsync(
            TestAccount, Container, Version(sample.Version), Identity, password: null, CancellationToken.None);

        await using var rebuilt = await catalogs.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None);
        await AssertCatalogHasSampleAsync(rebuilt, sample.Version, sample);
    }

    // ---- The .idx file store itself: what the chain above reads through, and what deletes it ---------------------
    // Moved here when VersionIndexFileStoreTests went away with the writer it was built around. What is left of
    // VersionIndexFileStore is the reader this chain goes through plus the two deletes retention and config removal
    // call, so its cases belong beside the chain that is now its only caller.

    private static byte[] Body(string marker) => System.Text.Encoding.UTF8.GetBytes(marker);

    /// <summary>Reads a body back through <see cref="VersionIndexFileStore.OpenBodyAsync"/> — null means the header
    /// was rejected, which is what every "is a miss" case below asserts.</summary>
    private static async Task<byte[]?> ReadBodyAsync(
        VersionIndexFileStore files, int accountId, string container, int version, long identityTicks)
    {
        await using var body = await files.OpenBodyAsync(accountId, container, version, identityTicks, CancellationToken.None);
        if (body is null)
            return null;
        using var ms = new MemoryStream();
        await body.CopyToAsync(ms, CancellationToken.None);
        return ms.ToArray();
    }

    [Fact]
    public async Task OpenBody_returns_the_body_under_a_matching_identity()
    {
        var files = TestIndexFiles.New();
        await TestIndexFiles.WriteAsync(files, 1, "photos", 3, 100, Body("index-bytes"));

        Assert.Equal(Body("index-bytes"), await ReadBodyAsync(files, 1, "photos", 3, 100));
    }

    [Fact]
    public async Task OpenBody_treats_an_absent_entry_as_a_miss_rather_than_an_error()
    {
        var files = TestIndexFiles.New();

        Assert.Null(await ReadBodyAsync(files, 1, "photos", 3, 100));
        Assert.Null(await ReadBodyAsync(files, 9, "never-written", 1, 0));
    }

    /// <summary>The whole point of the header: a rebuilt container's identity moves, and the old entry must not be
    /// served. Rejecting it costs 24 bytes of reading, where the row this replaced had to load the entire index
    /// first.</summary>
    [Fact]
    public async Task OpenBody_treats_an_identity_mismatch_as_a_miss()
    {
        var files = TestIndexFiles.New();
        await TestIndexFiles.WriteAsync(files, 1, "photos", 3, 100, Body("old"));

        Assert.Null(await ReadBodyAsync(files, 1, "photos", 3, 200));
    }

    /// <summary>A file cut short — a power failure mid-write, a filesystem that lost the tail — must read as a miss
    /// and send the caller on down the chain, not hand over a body that imports as a plausible-looking version
    /// missing half its entries.</summary>
    [Fact]
    public async Task OpenBody_treats_a_truncated_entry_as_a_miss()
    {
        var files = TestIndexFiles.New();
        await TestIndexFiles.WriteAsync(files, 1, "photos", 3, 100, Body("a much longer body than the header"));

        using (var f = new FileStream(files.PathFor(1, "photos", 3), FileMode.Open, FileAccess.Write))
            f.SetLength(f.Length - 5);

        Assert.Null(await ReadBodyAsync(files, 1, "photos", 3, 100));
    }

    [Fact]
    public async Task OpenBody_treats_a_file_that_is_not_ours_as_a_miss()
    {
        var files = TestIndexFiles.New();
        var path = files.PathFor(1, "photos", 3);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "this is not a version index at all");

        Assert.Null(await ReadBodyAsync(files, 1, "photos", 3, 100));
    }

    [Fact]
    public async Task Remove_drops_one_version_and_leaves_the_others()
    {
        var files = TestIndexFiles.New();
        await TestIndexFiles.WriteAsync(files, 1, "photos", 1, 100, Body("v1"));
        await TestIndexFiles.WriteAsync(files, 1, "photos", 2, 100, Body("v2"));

        files.Remove(1, "photos", 1);

        Assert.Null(await ReadBodyAsync(files, 1, "photos", 1, 100));
        Assert.Equal(Body("v2"), await ReadBodyAsync(files, 1, "photos", 2, 100));
    }

    [Fact]
    public void Removing_something_that_is_not_there_is_not_an_error()
    {
        var files = TestIndexFiles.New();
        files.Remove(1, "photos", 1);
        files.RemoveForContainer(1, "photos");
    }

    [Fact]
    public async Task RemoveForContainer_spares_other_containers_and_other_accounts()
    {
        var files = TestIndexFiles.New();
        await TestIndexFiles.WriteAsync(files, 1, "photos", 1, 100, Body("target"));
        await TestIndexFiles.WriteAsync(files, 1, "photos", 2, 100, Body("target"));
        await TestIndexFiles.WriteAsync(files, 1, "docs", 1, 100, Body("other container"));
        await TestIndexFiles.WriteAsync(files, 2, "photos", 1, 100, Body("other account"));

        files.RemoveForContainer(1, "photos");

        Assert.Null(await ReadBodyAsync(files, 1, "photos", 1, 100));
        Assert.Null(await ReadBodyAsync(files, 1, "photos", 2, 100));
        Assert.Equal(Body("other container"), await ReadBodyAsync(files, 1, "docs", 1, 100));
        Assert.Equal(Body("other account"), await ReadBodyAsync(files, 2, "photos", 1, 100));
    }

    /// <summary>
    /// Container names are flattened before they become a path segment. Azure will not hand us a name containing a
    /// separator today, but <see cref="VersionIndexFileStore.RemoveForContainer"/> is a recursive delete, and one
    /// <c>..</c> reaching a path segment would take a sibling container's directory with it.
    /// </summary>
    [Fact]
    public async Task Container_names_cannot_escape_their_own_directory()
    {
        var files = TestIndexFiles.New();
        await TestIndexFiles.WriteAsync(files, 1, "photos", 1, 100, Body("must survive"));
        await TestIndexFiles.WriteAsync(files, 1, "../photos", 1, 100, Body("hostile"));

        files.RemoveForContainer(1, "../photos");

        Assert.Equal(Body("must survive"), await ReadBodyAsync(files, 1, "photos", 1, 100));
        // Both names resolve under the same account directory, so neither can reach the other's siblings.
        var accountDir = Path.GetDirectoryName(Path.GetDirectoryName(files.PathFor(1, "photos", 1))!)!;
        Assert.StartsWith(Path.GetFullPath(accountDir), Path.GetFullPath(files.PathFor(1, "../photos", 1)));
    }
}
