using AzureStorageBackup.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The application must open its database with Microsoft.Data.Sqlite's connection pooling switched off, whatever
/// the configured connection string says.
/// <para>
/// Seen on the NAS on 2026-09-07 with two backups running: queries failing inside <c>SqliteConnection.Open()</c>
/// with <c>SQLite Error 5: 'not an error'</c> and, interleaved, the generic <c>'database is locked'</c> text. Both
/// wordings come out of the same place: <c>ThrowExceptionForRC</c> reads the handle's error code and then its
/// message, and they only disagree when another thread changes the handle's error state between the two reads.
/// The pool is how a second thread gets onto a handle this thread just opened. When an outer connection is
/// garbage-collected without being closed, the pool reclaims its handle as "leaked" and pushes it straight
/// back — the outer object is gone, so the step that unregisters EF's user functions is skipped — and the next
/// <c>Open()</c> re-registers those functions on a handle whose old statements are still being finalized on the
/// finalizer thread. Without a pool a leaked handle is simply finalized on its own and never handed to anyone.
/// </para>
/// <para>
/// Measured locally: opening a DbContext and running one query costs 0.33 ms pooled and 1.1 ms unpooled. The
/// heaviest opener in this application is the per-7z-process priority read, which is nowhere near that rate.
/// </para>
/// <para>
/// Pinned on the effective connection of the hosted DbContext rather than on a config file: the Dockerfile's
/// environment variable overrides appsettings, and the guarantee has to hold for whatever string arrives.
/// </para>
/// </summary>
public sealed class SqliteConnectionPoolingTests
{
    [Fact]
    public void The_hosted_DbContext_opens_its_database_without_connection_pooling()
    {
        using var factory = new TestWebAppFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var options = new SqliteConnectionStringBuilder(db.Database.GetDbConnection().ConnectionString);
        Assert.False(options.Pooling);
    }
}
