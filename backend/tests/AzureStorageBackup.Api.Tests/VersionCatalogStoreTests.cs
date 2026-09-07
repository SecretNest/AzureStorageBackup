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

    // ---- Test 3: garbage on disk is a cache miss, not a fatal error --------------------------------------------

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
        var warnings = logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log))
            .Where(c => c.GetArguments()[0] is LogLevel.Warning)
            .ToList();
        Assert.Single(warnings);
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
        // directly to pin exactly what RemoveContainer promises to clean up regardless of what the writer left.
        await File.WriteAllBytesAsync(path + "-wal", [1, 2, 3]);
        await File.WriteAllBytesAsync(path + "-shm", [4, 5, 6]);

        store.RemoveContainer(AccountId, Container);

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }
}
