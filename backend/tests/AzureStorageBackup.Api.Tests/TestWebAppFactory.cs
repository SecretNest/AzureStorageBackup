using AzureStorageBackup.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Factory for integration tests: points AppDbContext at a SQLite database in a **temp file owned exclusively by each host**,
/// isolated from the real database. Reusable by all the endpoint tests.
/// </summary>
public class TestWebAppFactory : WebApplicationFactory<Program>
{
    // A compression temp area of its own per test host (otherwise, when xUnit parallelizes across classes, the StagingArea singletons of
    // several hosts share compress/staged under the default /tmp/azurestoragebackup: same content → same compressed output name → hosts collide, making integration tests flaky under concurrency).
    private readonly string _tempPath = Path.Combine(Path.GetTempPath(), "asb-test-" + Guid.NewGuid().ToString("N"));

    // A file database, not `DataSource=:memory:`. An in-memory database only lives on the connection that opened it, so every DbContext
    // would have to share **one and the same** SqliteConnection — and every DbContext EF builds registers user functions on that
    // connection, which falls over with SQLite Error 5 the moment anyone else's statements are still running on it
    // ('unable to delete/modify user-function due to active statements').
    // Any background job touching the database at the same time as the test main thread runs into this, showing up as randomly failing integration tests.
    // A file database lets each DbContext open its own connection so nobody blocks anybody, and it is closer to production too (production is file-backed SQLite).
    // In a directory of its own rather than loose in /tmp, because the database file's directory is also where the
    // app puts everything that has to live beside it — the backup journals, and the version-index cache. Sharing
    // those between parallel test hosts means one host's cached index answering another's read.
    private readonly string _dataPath = Path.Combine(
        Path.GetTempPath(), "asb-test-data-" + Guid.NewGuid().ToString("N"));

    private string _dbPath => Path.Combine(_dataPath, "app.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Do not start the resident scheduler in tests, so background triggers cannot interfere
        builder.UseSetting("Scheduler:Enabled", "false");
        // Isolate the compression temp area (parallel test hosts share no disk)
        builder.UseSetting("Backup:TempPath", _tempPath);
        // The connection string, not just the DbContext registration below: Program.cs derives the journal root and the
        // version-index cache root from it, and left at the default those would land in one shared directory.
        builder.UseSetting("ConnectionStrings:Sqlite", $"DataSource={_dbPath}");

        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor is not null)
                services.Remove(descriptor);

            services.AddDbContext<AppDbContext>(o => o.UseSqlite($"DataSource={_dbPath}"));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            // The connection pool clings to the file handles: deleting outright fails on Windows and leaves -wal/-shm behind on Linux.
            SqliteConnection.ClearAllPools();
            // The whole data directory, which takes the -wal/-shm files, the journals and the index cache with it.
            try { Directory.Delete(_dataPath, recursive: true); } catch { /* best effort */ }
            try { Directory.Delete(_tempPath, recursive: true); } catch { /* best effort */ }
        }
    }
}
