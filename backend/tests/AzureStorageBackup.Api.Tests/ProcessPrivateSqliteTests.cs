using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The catalogs and the run's work database are opened through SQLite's <c>unix-excl</c> VFS so that no
/// <c>-shm</c> file — and none of the byte-range locks on it that a QNAP kernel refused for no visible reason — is
/// ever involved. These tests pin the two things that matter: the file is really absent while connections are open
/// and writing, and connections inside the process still share the database concurrently, which is the property
/// WAL was chosen for in the first place.
/// </summary>
public sealed class ProcessPrivateSqliteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "asb-excl-" + Guid.NewGuid().ToString("N"));

    public ProcessPrivateSqliteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Data_source_is_a_uri_naming_the_vfs_and_escapes_only_uri_syntax()
    {
        Skip.If(OperatingSystem.IsWindows(), "unix-excl is a Unix VFS.");

        Assert.Equal("file:/data/x/catalog.db?vfs=unix-excl", ProcessPrivateSqlite.DataSource("/data/x/catalog.db"));
        Assert.Equal(
            "file:/d/a%25b%3Fc%23d/catalog.db?vfs=unix-excl",
            ProcessPrivateSqlite.DataSource("/d/a%b?c#d/catalog.db"));
    }

    [Fact]
    public async Task A_catalog_being_written_has_no_shm_file_and_a_concurrent_reader_still_sees_it()
    {
        Skip.If(OperatingSystem.IsWindows(), "unix-excl is a Unix VFS.");

        var path = Path.Combine(_root, "catalog.db");
        await using var writer = await VersionCatalog.OpenAsync(path, readOnly: false, CancellationToken.None);
        await writer.ImportVersionAsync(1, identity: 1, 300, Entries(300), emptyDirs: [], unrecoverable: [], CancellationToken.None);

        // WAL is on (the -wal file is what makes readers independent of the writer)…
        Assert.True(File.Exists(path + "-wal"), "the WAL file should exist while the writer is open");
        // …but the shared-memory index is in the process's heap: nothing to lock on disk.
        Assert.False(File.Exists(path + "-shm"), "no -shm file may exist under unix-excl");

        // A second connection in the same process — the shape of the UI reading while a run imports — opens as a
        // reader (no create, no schema, no quick_check) while the writer is still open, and reads the committed rows.
        await using var reader = await VersionCatalog.OpenAsync(path, readOnly: true, CancellationToken.None);
        var versions = await reader.ListVersionsAsync(CancellationToken.None);
        Assert.Single(versions);
        Assert.Equal(300, versions[0].EntryCount);

        // And a write that lands while the reader is open is visible to the reader's next query.
        await writer.ImportVersionAsync(2, identity: 2, 5, Entries(5), emptyDirs: [], unrecoverable: [], CancellationToken.None);
        Assert.Equal(2, (await reader.ListVersionsAsync(CancellationToken.None)).Count);
        Assert.False(File.Exists(path + "-shm"));
    }

    [Fact]
    public async Task A_missing_catalog_opened_by_a_reader_is_not_created()
    {
        var path = Path.Combine(_root, "absent", "catalog.db");
        await Assert.ThrowsAnyAsync<Exception>(() => VersionCatalog.OpenAsync(path, readOnly: true, CancellationToken.None));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task The_work_database_has_no_shm_file_while_the_writer_and_a_reader_are_open()
    {
        Skip.If(OperatingSystem.IsWindows(), "unix-excl is a Unix VFS.");

        var path = Path.Combine(_root, "work", "run.db");
        await using var work = await RunWorkDb.CreateAsync(path, CancellationToken.None);
        for (var i = 0; i < 50; i++)
            await work.InsertScanAsync(
                new ScanRow($"dir/f{i:D3}", EntryKind.File, i, DateTimeOffset.UnixEpoch, "0644", null, FileCategory.SingleFile, null),
                CancellationToken.None);
        await work.FlushAsync(CancellationToken.None);

        Assert.Equal(50, await work.ScanCountAsync(CancellationToken.None));
        Assert.False(File.Exists(path + "-shm"), "no -shm file may exist under unix-excl");
    }

    private static async IAsyncEnumerable<IndexEntry> Entries(int count)
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
}
