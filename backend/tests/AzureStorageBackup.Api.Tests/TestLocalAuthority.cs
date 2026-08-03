using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 备份的本地权威接线（索引缓存 + 本地状态）在测试里的样板。
/// <para>
/// <see cref="BackupOrchestrator"/> 要求这两样东西——去重、上一版本索引、信息文件一律走本地，
/// 备份路径上不发任何云端 HEAD。生产由 DI 供给（<c>Program.cs</c>），测试则每处都要自己接：
/// 一个内存 SQLite、一个 <see cref="LocalIndexCache"/>、一个 <see cref="TrackedInfoStore"/>。
/// 三十多个构造点各抄一遍这段样板不值当，何况其中大半根本不关心本地权威，只是要把编排器造出来。
/// </para>
/// <para>
/// **刻意不实现 <see cref="IDisposable"/>**：编排器常常是由某个 <c>Make…()</c> 工厂方法造好返回的，
/// 接线的持有者与使用者不是同一处，谁来 Dispose 说不清楚——而 <c>DataSource=:memory:</c> 的库
/// 就活在那条连接上，早关一步后面全炸。测试进程短命，几十条连接留给进程退出回收即可。
/// </para>
/// </summary>
internal sealed class TestLocalAuthority
{
    /// <summary>自带一个内存库。</summary>
    internal TestLocalAuthority(IBackupInfoStore store)
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        Db.Database.EnsureCreated();
        (IndexCache, Tracked) = Wire(Db, store);
    }

    /// <summary>复用测试类自己已经有的库——需要编排器与 checker/repairer 看到同一份本地状态时用。</summary>
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
