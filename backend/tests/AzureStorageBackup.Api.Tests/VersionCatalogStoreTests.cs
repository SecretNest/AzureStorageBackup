using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// <see cref="VersionCatalogStore"/> is the layer above <see cref="VersionCatalog"/> that knows where a container's
/// file lives, that only one writer may hold it at a time, and that a catalog is a cache — so a file SQLite cannot
/// read is a reason to delete and rebuild it, never a reason to fail the caller.
/// </summary>
public sealed class VersionCatalogStoreTests : IDisposable
{
    private const int AccountId = 7;
    private const string Container = "photos";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "asb-catalogstore-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Builds a catalog with enough rows to spill past the first 4 KiB page — the whole point of the
    /// "corrupt page 2" tests below is a file that is a valid SQLite database (so <c>ApplyPragmas</c>'s own
    /// statements do not already trip over it) but has bad bytes somewhere <c>quick_check</c> has to go looking for,
    /// which a single-page, near-empty catalog cannot offer.</summary>
    private static async Task BuildMultiPageCatalogAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var catalog = await VersionCatalog.OpenAsync(path, readOnly: false, CancellationToken.None);
        const int count = 500;
        await catalog.ImportVersionAsync(1, identity: 1, count, ManyEntries(count), emptyDirs: [], unrecoverable: [], CancellationToken.None);
    }

    private static async IAsyncEnumerable<IndexEntry> ManyEntries(int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return new IndexEntry
            {
                Path = $"dir/file-{i:D5}.bin",
                Kind = "file",
                Length = i,
                Mtime = DateTimeOffset.UnixEpoch,
                Permissions = "0644",
                FullHash = "xxh128:" + i.ToString("D32"),
            };
            await Task.Yield();
        }
    }

    /// <summary>Flips the type byte of the second database page (file offset 4096, the default page size) to an
    /// invalid value. That byte is not "some random corruption that quick_check might or might not notice" — it is
    /// the b-tree page's own type flag, which quick_check reliably reports regardless of which table or index the
    /// page happens to belong to.</summary>
    private static void CorruptPage2(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        Assert.True(stream.Length > 8192, "fixture did not produce a second page to corrupt");
        stream.Seek(4096, SeekOrigin.Begin);
        stream.WriteByte(0x00);
    }

    private static List<NSubstitute.Core.ICall> WarningCalls(ILogger<VersionCatalogStore> logger) =>
        [.. logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log))
            .Where(c => c.GetArguments()[0] is LogLevel.Warning)];

    // ---- Test 1: a fresh open creates the file and an empty schema -------------------------------------------

    [Fact]
    public async Task Open_creates_the_file_and_schema()
    {
        var store = new VersionCatalogStore(_root);

        await using (var catalog = await store.OpenAsync(AccountId, Container, readOnly: false, CancellationToken.None))
        {
            Assert.Empty(await catalog.ListVersionsAsync(CancellationToken.None));
        }

        Assert.True(File.Exists(store.PathFor(AccountId, Container)));
    }

    // ---- Test 2: a reader never resurrects a catalog nobody wrote yet ------------------------------------------

    [Fact]
    public async Task Open_readonly_on_a_missing_file_throws_FileNotFound()
    {
        var store = new VersionCatalogStore(_root);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => store.OpenAsync(AccountId, Container, readOnly: true, CancellationToken.None));

        Assert.False(File.Exists(store.PathFor(AccountId, Container)));
    }

    // ---- Test 3: garbage on disk (not even a database) is a cache miss, not a fatal error -----------------------

    [Fact]
    public async Task Corrupt_file_is_replaced_and_logged()
    {
        var logger = Substitute.For<ILogger<VersionCatalogStore>>();
        var store = new VersionCatalogStore(_root, logger);
        var path = store.PathFor(AccountId, Container);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var garbage = new byte[4096];
        Random.Shared.NextBytes(garbage);
        await File.WriteAllBytesAsync(path, garbage);

        await using (var catalog = await store.OpenAsync(AccountId, Container, readOnly: false, CancellationToken.None))
        {
            Assert.Empty(await catalog.ListVersionsAsync(CancellationToken.None));
        }

        // The file itself is now a valid, empty catalog rather than the 4096 random bytes it was.
        var reopened = await VersionCatalog.OpenAsync(path, readOnly: true, CancellationToken.None);
        await using (reopened)
            Assert.Empty(await reopened.ListVersionsAsync(CancellationToken.None));

        // Inspected as raw calls rather than through the typed Received() overload: ILogger.Log<TState> is generic,
        // and the TState the LogWarning extension actually supplies (an internal formatted-log-values type) does not
        // match an Arg.Any<object>() recorded against a different closed generic method.
        Assert.Single(WarningCalls(logger));
    }

    // ---- Test 3b: a page corrupt deep inside an otherwise well-formed file is caught by quick_check -------------

    [Fact]
    public async Task Corrupt_page_is_detected_by_quick_check_and_replaced()
    {
        var logger = Substitute.For<ILogger<VersionCatalogStore>>();
        var store = new VersionCatalogStore(_root, logger);
        var path = store.PathFor(AccountId, Container);

        // Unlike Test 3's random bytes, this file passes ApplyPragmas' own statements (its header is intact) — only
        // a full quick_check scan finds the damage, which is the path this test exists to exercise.
        await BuildMultiPageCatalogAsync(path);
        CorruptPage2(path);

        await using (var catalog = await store.OpenAsync(AccountId, Container, readOnly: false, CancellationToken.None))
        {
            // Rebuilt empty rather than holding the version the fixture imported before corruption.
            Assert.Empty(await catalog.ListVersionsAsync(CancellationToken.None));
        }

        Assert.Single(WarningCalls(logger));
    }

    // ---- Test 3c: quick_check runs at most once per path per process, not on every write-open -------------------

    [Fact]
    public async Task QuickCheck_runs_once_per_path_per_process()
    {
        var store = new VersionCatalogStore(_root);
        var path = store.PathFor(AccountId, Container);
        await BuildMultiPageCatalogAsync(path);

        // First open through the store: the path is unchecked, so quick_check runs — and, correctly, finds nothing
        // wrong yet — and marks the path checked.
        await using (var catalog = await store.OpenAsync(AccountId, Container, readOnly: false, CancellationToken.None))
            Assert.NotNull(await catalog.GetVersionAsync(1, CancellationToken.None));

        // Corrupt the file directly — not through the store — so its recovery path never runs and the path stays
        // marked "checked" in this store instance.
        CorruptPage2(path);

        // Same store instance: the path is already in the checked set, so quick_check is skipped, and the open
        // succeeds even though the file underneath it is now damaged (neither ApplyPragmas nor EnsureSchema touches
        // the corrupted data page — only a query against it, or quick_check's own full scan, would).
        await using (var catalog = await store.OpenAsync(AccountId, Container, readOnly: false, CancellationToken.None))
            Assert.NotNull(catalog);

        // A fresh store instance starts with an empty checked set: it runs quick_check again against the same path,
        // catches the same corruption, and rebuilds the file.
        var freshStore = new VersionCatalogStore(_root);
        await using (var catalog = await freshStore.OpenAsync(AccountId, Container, readOnly: false, CancellationToken.None))
            Assert.Empty(await catalog.ListVersionsAsync(CancellationToken.None));
    }

    // ---- Test 4: one writer at a time per container -------------------------------------------------------------

    [Fact]
    public async Task LockForWrite_serializes()
    {
        var store = new VersionCatalogStore(_root);

        var first = await store.LockForWriteAsync(AccountId, Container, CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.LockForWriteAsync(AccountId, Container, timeout.Token));

        first.Dispose();

        var second = await store.LockForWriteAsync(AccountId, Container, CancellationToken.None);
        second.Dispose();
    }

    // ---- Test 5: removing a container cleans up the whole WAL trio ----------------------------------------------

    [Fact]
    public async Task RemoveContainer_deletes_wal_and_shm()
    {
        var store = new VersionCatalogStore(_root);
        var path = store.PathFor(AccountId, Container);

        await using (await store.OpenAsync(AccountId, Container, readOnly: false, CancellationToken.None))
        {
            // Opened under WAL, so -wal and -shm exist alongside the main file while the connection is live.
        }

        // WAL siblings can persist on disk after a clean close (checkpointed but not removed), so create them
        // directly to pin exactly what RemoveContainerAsync promises to clean up regardless of what the writer left.
        await File.WriteAllBytesAsync(path + "-wal", [1, 2, 3]);
        await File.WriteAllBytesAsync(path + "-shm", [4, 5, 6]);

        await store.RemoveContainerAsync(AccountId, Container, CancellationToken.None);

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }
}
