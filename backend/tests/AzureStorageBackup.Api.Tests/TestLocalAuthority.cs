using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The boilerplate for a backup's local-authority wiring (index cache + local state) inside tests.
/// <para>
/// <see cref="BackupOrchestrator"/> demands both of these — dedup, the previous version's index and the info file all go local, and
/// the backup path issues no cloud HEAD at all. Production gets them from DI (<c>Program.cs</c>); tests have to wire them up by hand
/// at every site: an in-memory SQLite, a <see cref="LocalIndexCache"/>, and a <see cref="TrackedInfoStore"/>.
/// Copying this boilerplate into thirty-odd construction sites is not worth it, especially since most of them do not care about local authority at all and only want an orchestrator built.
/// </para>
/// <para>
/// **Deliberately does not implement <see cref="IDisposable"/>**: the orchestrator is usually built and handed back by some <c>Make…()</c>
/// factory method, so whoever owns the wiring is not whoever uses it and there is no clear answer to who should Dispose — while a
/// <c>DataSource=:memory:</c> database lives on that one connection, so closing it a step too early blows up everything after. Test processes are short-lived; a few dozen connections can be left for process exit to reclaim.
/// </para>
/// </summary>
internal sealed class TestLocalAuthority
{
    /// <summary>Brings its own in-memory database.</summary>
    internal TestLocalAuthority(IBackupInfoStore store)
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        Db.Database.EnsureCreated();
        (IndexCache, Tracked) = Wire(Db, store);
    }

    /// <summary>Reuses a database the test class already has — for when the orchestrator and the checker/repairer must see the same local state.</summary>
    internal TestLocalAuthority(AppDbContext db, IBackupInfoStore store)
    {
        Db = db;
        (IndexCache, Tracked) = Wire(db, store);
    }

    internal AppDbContext Db { get; }

    internal LocalIndexCache IndexCache { get; }

    internal TrackedInfoStore Tracked { get; }

    private static (LocalIndexCache, TrackedInfoStore) Wire(AppDbContext db, IBackupInfoStore store)
        => (new LocalIndexCache(db, store), new TrackedInfoStore(store, new LocalBackupStateStore(db)));
}
