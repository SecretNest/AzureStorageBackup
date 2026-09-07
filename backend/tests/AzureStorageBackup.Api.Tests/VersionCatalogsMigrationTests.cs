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
        [.. logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log))
            .Where(c => c.GetArguments()[0] is LogLevel.Warning)];

    // ---- Test 1: a catalog hit touches nothing else --------------------------------------------------------------

    [Fact]
    public async Task Ensure_uses_the_catalog_when_present_and_touches_nothing_else()
    {
        using var db = NewDb();
        var infoStore = Substitute.For<IBackupInfoStore>();
        var catalogs = TestCatalogs.New(db, infoStore);
        var sample = IndexSamples.Sample();
        var bytes = IndexSerializer.SerializeIndex(sample);

        await using (var catalog = await catalogs.OpenAsync(AccountId, Container, readOnly: false, CancellationToken.None))
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
        var bytes = IndexSerializer.SerializeIndex(sample);
        await files.WriteAsync(AccountId, Container, 1, Identity, bytes, CancellationToken.None);

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
        var bytes = IndexSerializer.SerializeIndex(sample);
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
        await files.WriteAsync(AccountId, Container, 1, staleIdentity, bytes, CancellationToken.None);

        await catalogs.EnsureVersionAsync(TestAccount, Container, Version(), Identity, password: null, CancellationToken.None);

        await using var catalog = await catalogs.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None);
        await AssertCatalogHasSampleAsync(catalog, 1, sample);

        // The stale file is untouched: still there, and still readable under its own (old) identity — proof nothing
        // overwrote or deleted it, only that it was skipped.
        Assert.True(File.Exists(files.PathFor(AccountId, Container, 1)));
        Assert.NotNull(await files.ReadAsync(AccountId, Container, 1, staleIdentity, CancellationToken.None));
        Assert.Null(await files.ReadAsync(AccountId, Container, 1, Identity, CancellationToken.None));
    }

    // ---- Test 4: a legacy CachedVersionIndexes row is imported and dropped -------------------------------------------

    [Fact]
    public async Task Ensure_imports_a_legacy_row_and_drops_it()
    {
        using var db = NewDb();
        var infoStore = Substitute.For<IBackupInfoStore>();
        var catalogs = TestCatalogs.New(db, infoStore);
        var sample = IndexSamples.Sample();
        var bytes = IndexSerializer.SerializeIndex(sample);

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
        var bytes = IndexSerializer.SerializeIndex(sample);
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

    // ---- Test 7: removing a container drops the catalog and every legacy row ------------------------------------------

    [Fact]
    public async Task RemoveContainer_removes_catalog_and_legacy_rows()
    {
        using var db = NewDb();
        var infoStore = Substitute.For<IBackupInfoStore>();
        var sample = IndexSamples.Sample();
        var bytes = IndexSerializer.SerializeIndex(sample);
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
        await files.WriteAsync(AccountId, Container, 2, Identity, bytes, CancellationToken.None);
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
        var catalogs = new VersionCatalogs(TestCatalogs.NewStore(), files, db, infoStore, logger);

        var sample = IndexSamples.Sample();
        var fullBytes = IndexSerializer.SerializeIndex(sample);
        // format(1) + version(4) + entryCount(4): a header IndexStreamReader parses fine, claiming entries that
        // are not there — exactly what a file truncated mid-write would look like.
        var corrupt = fullBytes[..9];
        await files.WriteAsync(AccountId, Container, 1, Identity, corrupt, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalogs.EnsureVersionAsync(TestAccount, Container, Version(), Identity, password: null, CancellationToken.None));

        Assert.False(File.Exists(files.PathFor(AccountId, Container, 1)));
        Assert.Single(WarningCalls(logger));

        await using var catalog = await catalogs.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None);
        Assert.Null(await catalog.GetVersionAsync(1, CancellationToken.None));
    }
}
