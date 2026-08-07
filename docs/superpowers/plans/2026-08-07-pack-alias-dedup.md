# 同一轮备份内跨箱打包成员去重 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让同一轮备份里内容相同、路径不同的小文件只装一次箱——后到者直接指向先到者在包里的成员，不压、不传、不装箱。

**Architecture:** diff 单线程在装箱决定点维护一张"内容身份 → leader 路径"的表（`PackAliasTable`）；命中的后到者登记为别名、不入箱。等所有消费者 join 之后，按 leader 的**最终态**统一回填 `StorageRef`。判断只看最终态，因此不需要任何新的并发原语，消费者侧（`ProcessPackAsync` / `RecordPack` / `CompressPackTolerantAsync`）一行不改。leader 走岔（内容在压缩窗口里变过、读不开、改走单文件 blob）时别名判为悬空，重新跑一遍。

**Tech Stack:** C# / .NET, xUnit, Azurite（集成测试）, 7-Zip CLI

**Spec:** `docs/superpowers/specs/2026-08-07-pack-alias-dedup-design.md`

## Global Constraints

- **判据四项严格相等**：`fullHash` + `length` + `headHash` + `tailHash`。任一项不同或缺失即不参与去重。不开兼容口子——判据要么是四项要么不是。
- **对既有备份纯只读**：老索引一个字节不动；写下的引用形状必须是 `{Kind="pack", Ref=packId, EntryName=leaderPath}`，与 `RecordPack` 从前写的逐字节相同；索引 schema 不变，无新字段。
- **消费者侧不改**：`ProcessPackAsync`、`RecordPack`、`CompressPackTolerantAsync`、`UploadStagedPackAsync` 一行都不改。
- **不追溯**：历史版本里已有的重复不合并。
- **进度计数零进零出**：别名不 `Enqueue` 不 `ReportItem`；悬空重跑的 `onItem` 传 `static _ => { }`。绝不手动补分母。
- 代码注释用中文，与该文件既有风格一致（讲清"为什么"，不复述"是什么"）。UI 文案一律英文——本次改动不涉及 UI。
- 测试命令统一用：`dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj`
- 集成测试需要 Azurite，否则会静默跳过：`npx azurite --skipApiVersionCheck`（后台起）。

---

### Task 1: `PackAliasTable` 与内容身份的共用拼法

纯新增 + 一处可见性放宽，不接线，零行为变化。

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/PackAliasTable.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Services/LocalDedupResolver.cs:195-196`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/PackAliasTableTests.cs`

**Interfaces:**
- Consumes: `LocalDedupResolver.ContentKey`（本任务把它从 private 提为 public static）
- Produces:
  - `public sealed record PlannedAlias(string Path, long Length, string FullHash, string HeadHash, string TailHash)`
  - `public sealed class PackAliasTable`
    - `public bool TryClaim(string? fullHash, long length, string? headHash, string? tailHash, PlannedAlias candidate)` — 返回 `true` 表示本轮已有 leader、`candidate` 已登记为它的别名，调用方**不要**入箱；返回 `false` 表示这是第一份（或四项不全），调用方照旧入箱。
    - `public IReadOnlyDictionary<string, List<PlannedAlias>> AliasesByLeader { get; }` — 只含**真有别名**的 leader。

- [ ] **Step 1: 写失败的测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/PackAliasTableTests.cs`：

```csharp
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 本轮内跨箱打包成员去重的表结构。判据与 <see cref="LocalDedupResolver.TryFindPackMember"/>
/// 一致：fullHash + 长度 + head + tail 四项严格相等，缺失也算不等。
/// </summary>
public sealed class PackAliasTableTests
{
    private static PlannedAlias Alias(string path) => new(path, 100, "xxh128:aa", "xxh128:hh", "xxh128:tt");

    [Fact]
    public void First_Occurrence_Becomes_Leader_And_Is_Not_An_Alias()
    {
        var table = new PackAliasTable();

        // 第一份内容：调用方照旧入箱。
        Assert.False(table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", Alias("a/x.txt")));
        Assert.Empty(table.AliasesByLeader);
    }

    [Fact]
    public void Second_Occurrence_Of_Same_Content_Becomes_An_Alias_Of_The_First()
    {
        var table = new PackAliasTable();
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", Alias("a/x.txt"));

        Assert.True(table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", Alias("c/z.txt")));

        var (leader, aliases) = Assert.Single(table.AliasesByLeader);
        Assert.Equal("a/x.txt", leader);
        Assert.Equal(["c/z.txt"], aliases.Select(a => a.Path));
    }

    [Fact]
    public void Many_Aliases_All_Hang_On_The_Same_Leader()
    {
        var table = new PackAliasTable();
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", Alias("a/x.txt"));
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", Alias("b/y.txt"));
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", Alias("c/z.txt"));

        var (leader, aliases) = Assert.Single(table.AliasesByLeader);
        Assert.Equal("a/x.txt", leader);
        Assert.Equal(["b/y.txt", "c/z.txt"], aliases.Select(a => a.Path));
    }

    // 四项各差一项：都不该合并。判错的后果是索引指向别人的内容、还原出错数据。
    [Theory]
    [InlineData("xxh128:bb", 100L, "xxh128:hh", "xxh128:tt")]  // fullHash 不同
    [InlineData("xxh128:aa", 101L, "xxh128:hh", "xxh128:tt")]  // 长度不同
    [InlineData("xxh128:aa", 100L, "xxh128:zz", "xxh128:tt")]  // head 不同
    [InlineData("xxh128:aa", 100L, "xxh128:hh", "xxh128:zz")]  // tail 不同
    public void Any_Differing_Component_Prevents_Aliasing(
        string full, long length, string head, string tail)
    {
        var table = new PackAliasTable();
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", Alias("a/x.txt"));

        Assert.False(table.TryClaim(full, length, head, tail, Alias("c/z.txt")));
        Assert.Empty(table.AliasesByLeader);
    }

    // 缺项即不参与——老索引里那些没有尾部的成员就是这么被挡在外面的，
    // 代价只是那份内容会被再存一次，而这正是我们要的方向。
    [Theory]
    [InlineData(null, "xxh128:hh", "xxh128:tt")]
    [InlineData("xxh128:aa", null, "xxh128:tt")]
    [InlineData("xxh128:aa", "xxh128:hh", null)]
    public void A_Missing_Component_Never_Participates(string? full, string? head, string? tail)
    {
        var table = new PackAliasTable();

        // 既不登记为 leader……
        Assert.False(table.TryClaim(full, 100, head, tail, Alias("a/x.txt")));
        // ……第二次同样缺项的也不会认出它来。
        Assert.False(table.TryClaim(full, 100, head, tail, Alias("c/z.txt")));
        Assert.Empty(table.AliasesByLeader);
    }

    [Fact]
    public void A_Leader_Without_Aliases_Does_Not_Occupy_A_List()
    {
        var table = new PackAliasTable();
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", Alias("a/x.txt"));
        table.TryClaim("xxh128:bb", 100, "xxh128:hh", "xxh128:tt", Alias("b/y.txt"));
        table.TryClaim("xxh128:aa", 100, "xxh128:hh", "xxh128:tt", Alias("c/z.txt"));

        // 只有真有别名的 leader 才进这张表：一次首备有几十万个 leader，
        // 给每个都建一个空 List 是白占几十 MB。
        Assert.Equal(["a/x.txt"], table.AliasesByLeader.Keys);
    }
}
```

- [ ] **Step 2: 跑测试确认它失败**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter "FullyQualifiedName~PackAliasTableTests"
```

Expected: 编译失败，`error CS0246: The type or namespace name 'PackAliasTable' could not be found`

- [ ] **Step 3: 把 `ContentKey` 提为 public static**

在 `backend/src/AzureStorageBackup.Api/Services/LocalDedupResolver.cs`，把第 195-196 行：

```csharp
    private static string ContentKey(string fullHash, long length, string? head, string? tail) =>
        $"{fullHash}\n{length}\n{head}\n{tail}";
```

替换为：

```csharp
    /// <summary>内容身份的拼法：fullHash + 长度 + head + tail 四项。
    /// 公开是为了让本轮内的打包成员去重（<see cref="PackAliasTable"/>）用**同一个**拼法——
    /// 两条路各拼各的，迟早会在某一次改动里悄悄走岔，而走岔的后果是索引指向别人的内容。</summary>
    public static string ContentKey(string fullHash, long length, string? head, string? tail) =>
        $"{fullHash}\n{length}\n{head}\n{tail}";
```

- [ ] **Step 4: 写 `PackAliasTable`**

创建 `backend/src/AzureStorageBackup.Api/Services/PackAliasTable.cs`：

```csharp
namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 一个不入箱的后到者：内容与某个 leader 相同，索引条目将直接指向 leader 在包里的那个成员。
/// <para>
/// 带齐四项内容身份，是为了 leader 走岔时能把它当作一个普通待处理文件重新跑一遍
/// （见编排器收尾处的悬空重跑）——那时手上必须有它自己的长度与 hash。
/// </para>
/// </summary>
public sealed record PlannedAlias(
    string Path, long Length, string FullHash, string HeadHash, string TailHash);

/// <summary>
/// 同一轮备份内、跨箱的打包成员去重。
/// <para>
/// 打包的小文件从前只有两条去重：同一箱内靠 7z 的 solid 归档（字典跨成员匹配），跨版本靠
/// <see cref="LocalDedupResolver.TryFindPackMember"/>。缺的是**本轮之内、跨箱**那一段——
/// 不同箱之间压缩不共享字典，同一份内容会实打实地存两遍，而 <c>_packMembers</c> 只从历史
/// 版本索引构建，本轮新封的箱不进那张表。
/// </para>
/// <para>
/// 这张表只登记"谁是第一份"，**不**登记"它最后存到哪儿去了"——那要等消费者全部收工才知道
/// （leader 可能在压缩窗口里被改写、可能读不开、可能变大到改走单文件 blob）。回填因此放在
/// 收尾统一做，判断只看最终态。代价是别名要多等一会儿，换来的是这里一个并发原语都不需要。
/// </para>
/// <para>
/// diff 单线程独占，不加锁——与编排器里的 <c>dirPending</c>/<c>crossPending</c> 同一条约束。
/// </para>
/// </summary>
public sealed class PackAliasTable
{
    // 四项内容身份 → 第一个见到这份内容的路径。首次备份时每个变更小文件各占一条
    // （约 150 字节），20 万个约 30 MB——与装箱本身的在途状态同一个量级，可以接受。
    private readonly Dictionary<string, string> _leaderByContent = new(StringComparer.Ordinal);

    // leader 路径 → 挂在它身上的别名。**只有真有别名的 leader 才建列表**：
    // 一次首备有几十万个 leader，给每个都建一个空 List 是白占几十 MB。
    private readonly Dictionary<string, List<PlannedAlias>> _aliasesByLeader = new(StringComparer.Ordinal);

    /// <summary>只含真有别名的 leader。收尾回填遍历它。</summary>
    public IReadOnlyDictionary<string, List<PlannedAlias>> AliasesByLeader => _aliasesByLeader;

    /// <summary>
    /// 本轮这份内容是不是已经有 leader 了。
    /// <para>
    /// 返回 <c>true</c>：已有，<paramref name="candidate"/> 已登记为那个 leader 的别名，
    /// 调用方**不要**入箱。返回 <c>false</c>：这是第一份（或四项不全，不参与去重），照旧入箱。
    /// </para>
    /// <para>
    /// 四项**严格**相等，缺失也算不等——与 <see cref="LocalDedupResolver.TryFindPackMember"/>
    /// 同一套判据。同样是"这份内容是不是已经有了"的判断，两条路各有一套标准是说不通的：
    /// 判错就让索引指向别人的内容、还原时出来错误数据。
    /// </para>
    /// </summary>
    public bool TryClaim(
        string? fullHash, long length, string? headHash, string? tailHash, PlannedAlias candidate)
    {
        if (fullHash is null || headHash is null || tailHash is null)
            return false;

        var key = LocalDedupResolver.ContentKey(fullHash, length, headHash, tailHash);
        if (_leaderByContent.TryGetValue(key, out var leader))
        {
            if (!_aliasesByLeader.TryGetValue(leader, out var list))
                _aliasesByLeader[leader] = list = [];
            list.Add(candidate);
            return true;
        }

        _leaderByContent[key] = candidate.Path;
        return false;
    }
}
```

- [ ] **Step 5: 跑测试确认通过**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter "FullyQualifiedName~PackAliasTableTests"
```

Expected: PASS，8 passed（3 个 `[Fact]` + `Many_Aliases` + `A_Leader_Without_Aliases` + 4 个 `Any_Differing_Component` + 3 个 `A_Missing_Component`，共 13 个用例）

- [ ] **Step 6: 跑全量测试确认没碰坏别的**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: 全绿（Azurite 没起时集成测试会 skip，这一步只要没有 failed）

- [ ] **Step 7: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/PackAliasTable.cs \
        backend/src/AzureStorageBackup.Api/Services/LocalDedupResolver.cs \
        backend/tests/AzureStorageBackup.Api.Tests/PackAliasTableTests.cs
git commit -m "feat(dedup): 本轮内跨箱打包成员去重的表结构

判据与 TryFindPackMember 同一套四项，ContentKey 提为 public 共用拼法——
两条路各拼各的迟早会走岔，而走岔的后果是索引指向别人的内容。

只有真有别名的 leader 才建列表：一次首备有几十万个 leader。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: 接线——装箱决定点、收尾回填、悬空重跑

核心实现。**不可再拆**：只接线不回填会让别名条目丢掉 storage（备份数据丢失），中间态不安全。

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`（三处：表的声明、装箱决定点、收尾）
- Test: `backend/tests/AzureStorageBackup.Api.Tests/PackAliasDedupTests.cs`（新建）

**Interfaces:**
- Consumes: Task 1 的 `PackAliasTable.TryClaim(...)`、`PackAliasTable.AliasesByLeader`、`PlannedAlias`
- Produces: 无新公开 API（编排器内部接线）

- [ ] **Step 1: 写失败的集成测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/PackAliasDedupTests.cs`：

```csharp
using System.Net.Sockets;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 同一轮备份内、跨箱的打包成员去重。跨版本那一路由 <see cref="PackMemberDedupTests"/> 覆盖；
/// 这里要钉的是**同一轮**：本轮新封的箱之间，同内容只该装一次。
/// <para>
/// 装箱用 MaxPackMembers = 1 逼成一箱一个成员，跨箱因此是确定的，不必猜装箱结果。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class PackAliasDedupTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private readonly string _src;
    private readonly string _dst;
    private readonly string _temp;
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private int _mtimeSeq;
    private static readonly DateTime MtimeBase = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public PackAliasDedupTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "asb-packalias-" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(baseDir, "src");
        _dst = Path.Combine(baseDir, "dst");
        _temp = Path.Combine(baseDir, "temp");
        Directory.CreateDirectory(_src);
        Directory.CreateDirectory(_dst);
        Directory.CreateDirectory(_temp);

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        try { Directory.Delete(Path.GetDirectoryName(_src)!, recursive: true); } catch { /* best effort */ }
    }

    private static Account AzuriteAccount() => new()
    {
        Name = "azurite",
        BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
        AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
        Region = AzureRegion.Global,
    };

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;
    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    private void Write(string rel, string content)
    {
        var full = Path.Combine(_src, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        File.SetLastWriteTimeUtc(full, MtimeBase.AddMinutes(++_mtimeSeq));
    }

    private (BackupOrchestrator Backup, RestoreOrchestrator Restore, IBackupInfoStore Store) Build(
        IFileCompressor? compressor = null)
    {
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(factory, new SevenZipArchiveCodec());
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var indexCache = new LocalIndexCache(_db, store);
        var tracked = new TrackedInfoStore(store, new LocalBackupStateStore(_db));
        var backup = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(new FileHasher()), new GroupingPlanner(),
            compressor ?? new SevenZipCompressor(), new BlobUploader(factory), factory, store, staging,
            new RetentionCleaner(factory, store, new RetentionEvaluator(), null, indexCache, tracked),
            new FileHasher(), indexCache: indexCache, trackedInfo: tracked);
        var restore = new RestoreOrchestrator(
            factory, store, new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "restore"));
        return (backup, restore, store);
    }

    /// <summary>阈值给足让所有文件走 pack 路径；一箱只装一个成员，跨箱因此是确定的。</summary>
    private BackupRequest Request(Account account, string container) => new()
    {
        Account = account,
        Container = container,
        LocalRoot = _src,
        Name = "packalias",
        Options = new BackupEngineOptions
        {
            Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000, MaxPackMembers = 1 },
        },
    };

    private static async Task<int> CountPacksAsync(Azure.Storage.Blobs.BlobContainerClient cc)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var b in cc.GetBlobsAsync(BlobTraits.None, BlobStates.None, "packs/", CancellationToken.None))
            ids.Add(b.Name);
        return ids.Count;
    }

    /// <summary>
    /// T1 + T2：**同一轮**里三个小文件、其中两个同内容，一箱只装一个成员。
    /// 去重生效时只该有两个包（不是三个），第二条条目指向第一条那个成员，而且两条都还原得回来。
    /// </summary>
    [SkippableFact]
    public async Task Same_Content_In_Different_Packs_Is_Stored_Once_Within_One_Run()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packalias-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            // 不可压缩的内容：万一真的装了两箱，体积差异藏不住。
            var payload = string.Concat(Enumerable.Range(0, 400).Select(i => ((char)('a' + i % 26)).ToString()));
            Write("a/first.txt", payload);              // leader（ordinal 路径序最先）
            Write("b/other.txt", "something else entirely");
            Write("c/second.txt", payload);             // 别名

            var run = await backup.RunAsync(Request(account, name));

            // 三个文件、一箱一个成员：没有去重就是 3 个包，有去重是 2 个。
            Assert.Equal(2, await CountPacksAsync(cc));

            // T8：别名仍然是一个**变更文件**，只是不占一个包。记账口径不能因为去重而漏掉它——
            // 它在索引里实实在在有一条条目，用户也确实新加了这个文件。
            Assert.Equal(3, run.ChangedFiles);

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var first = v1.Entries.Single(e => e.Path == "a/first.txt");
            var second = v1.Entries.Single(e => e.Path == "c/second.txt");

            // 引用形状必须与 RecordPack 从前写的逐字节相同：Kind=pack + 同一个 Ref + leader 的 EntryName。
            Assert.Equal("pack", second.Storage!.Kind);
            Assert.Equal(first.Storage!.Ref, second.Storage.Ref);
            Assert.Equal("a/first.txt", second.Storage.EntryName);

            // 两条都要还原到**自己**的路径上。
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "a", "first.txt")));
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "c", "second.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>
    /// 内容不同就绝不能合并——哪怕长度一样。这一条是去重判据的反向保险：
    /// 判错的后果是索引指向别人的内容、还原出来是错数据。
    /// </summary>
    [SkippableFact]
    public async Task Different_Content_Of_The_Same_Length_Is_Never_Merged()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packaliasdiff-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            Write("a/first.txt", new string('x', 300));
            Write("c/second.txt", new string('y', 300));   // 同长度、不同内容

            await backup.RunAsync(Request(account, name));

            Assert.Equal(2, await CountPacksAsync(cc));   // 各装各的

            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(new string('x', 300), await File.ReadAllTextAsync(Path.Combine(_dst, "a", "first.txt")));
            Assert.Equal(new string('y', 300), await File.ReadAllTextAsync(Path.Combine(_dst, "c", "second.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }
}
```

- [ ] **Step 2: 跑测试确认它失败**

先确保 Azurite 在跑：

```bash
(npx azurite --skipApiVersionCheck --silent &) ; sleep 5
```

再跑：

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter "FullyQualifiedName~PackAliasDedupTests"
```

Expected: `Same_Content_In_Different_Packs_Is_Stored_Once_Within_One_Run` FAIL，`Assert.Equal() Failure: Expected: 2, Actual: 3`（同内容各装了一箱）。`Different_Content_Of_The_Same_Length_Is_Never_Merged` 应当已经 PASS（那是既有行为，作为反向保险先立在这里）。

如果两个都 skip，说明 Azurite 没起来——必须先解决，不能当成通过。

- [ ] **Step 3: 声明别名表**

在 `backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs`，找到第 501-503 行这段（"装箱的在途状态"的声明处）：

```csharp
        // 装箱的在途状态。diff 单线程按扫描顺序推进，所以这些都不需要加锁。
        var dirPending = new Dictionary<string, List<PlannedFile>>(StringComparer.Ordinal);
```

在这两行之间插入一行（让别名表与其它 diff 单线程状态待在一起，那条"不需要加锁"的注释同时罩住它）：

```csharp
        // 装箱的在途状态。diff 单线程按扫描顺序推进，所以这些都不需要加锁。
        // 本轮内跨箱的打包成员去重：同内容的后到者不入箱，只挂在首个之下，收尾统一回填。
        var aliasTable = new PackAliasTable();
        var dirPending = new Dictionary<string, List<PlannedFile>>(StringComparer.Ordinal);
```

- [ ] **Step 4: 在装箱决定点加一档**

在同一文件，找到第 577-581 行这段（跨版本 pack 命中之后的收尾）：

```csharp
                storageByPath[c.Path] = new StorageRef
                {
                    Kind = "pack", Ref = priorMember.PackId, EntryName = priorMember.EntryName,
                };
                // 之后走的是与"这一条没有变更内容"完全相同的既有路径：目录计数照常递减、封箱时机
                // 不受影响、不占上传槽位也不必销账。
                file = null;
            }
```

紧跟在这个 `}` 之后、`switch (klass.Category)` 之前，插入：

```csharp

            // 本轮内、跨箱的成员去重。上面那一档查的是**既有版本**的包（_packMembers 只从历史索引
            // 构建），本轮新封的箱不在其中——于是首次备份、或一次新增大量重复小文件时，同内容一旦
            // 被分进不同的箱就实打实地各存一份（不同箱之间压缩不共享字典，省不下来）。
            //
            // 后到者不入箱，只挂在首个之下；它最终指向哪个包要等消费者收工才知道（leader 可能在
            // 压缩窗口里被改写、可能读不开、可能变大到改走单文件 blob），所以回填放在收尾统一做。
            // 判断只看最终态，这里因此一个并发原语都不需要。
            //
            // 顺序上不会与上面那一档打架：leader 若命中既有包，后来的同内容文件用同一张表、同一套
            // 四项判据也会命中，根本走不到这里。所以进这张表的 leader 一定是"本轮新装箱的"。
            if (file is not null && klass.Category != FileCategory.SingleFile
                && file.FullHash is { } aliasHash && c.HeadHash is { } aliasHead && c.TailHash is { } aliasTail
                && aliasTable.TryClaim(aliasHash, file.Length, aliasHead, aliasTail,
                    new PlannedAlias(c.Path, file.Length, aliasHash, aliasHead, aliasTail)))
            {
                // 与上面那一档收场完全相同：走"这一条没有变更"的既有路径。
                // storageByPath 留到收尾回填——现在还不知道 leader 会落在哪个包上。
                file = null;
            }
```

- [ ] **Step 5: 收尾回填 + 悬空重跑**

在同一文件，找到第 714-721 行这段：

```csharp
        await Task.WhenAll(consumers);
        // 与扫描/差分同理：不强制产出终态，最后一批传完的字节就永远发布不出去——
        // 节流会把它们压在最后一个窗口里，而那之后不再有任何一次上报。
        uploadTracker.Complete();
```

在 `await Task.WhenAll(consumers);` 与 `uploadTracker.Complete();` 之间插入：

```csharp

        // 本轮内跨箱去重的收尾：把挂在各 leader 身上的别名回填成与 leader 相同的 StorageRef。
        // 放在这里而不是命中当时，是因为判断只看**最终态**——leader 会不会在压缩窗口里被改写、
        // 会不会读不开、会不会变大到改走单文件 blob，只有消费者全部收工才知道。于是装箱侧
        // 一个并发原语都不需要，也不存在"diff 刚挂上一个别名、消费者已经把 leader 判死"的竞态。
        var orphanAliases = new List<PlannedFile>();
        foreach (var (leaderPath, aliases) in aliasTable.AliasesByLeader)
        {
            // 三个否决条件对应 leader 走岔的三条真实路径：
            //   overrides 有它            → 内容在压缩窗口里变过，写下的是新 hash；
            //   postDiffUnreadable 有它   → 第二次也读不开，就地降级、不产生任何 blob；
            //   storage 不是 pack 或缺失  → 变大到超阈值改走了单文件 blob，或整组一起读不开。
            // 任一命中，别名的内容就已经**不等于** leader 最终存下去的那份了——绝不能指过去，
            // 那会让索引指向别人的内容，还原出来是错数据。
            var leaderStorage = storageByPath.GetValueOrDefault(leaderPath);
            if (leaderStorage is { Kind: "pack" }
                && !overrides.ContainsKey(leaderPath)
                && !postDiffUnreadable.ContainsKey(leaderPath))
            {
                // 整个 StorageRef 原样复制：Ref 与 EntryName 都是 leader 的，形状与 RecordPack
                // 从前写的逐字节相同，保留清理/死重压实/还原/检查因此都不必改。
                foreach (var a in aliases)
                    storageByPath[a.Path] = leaderStorage;
            }
            else
            {
                orphanAliases.AddRange(aliases.Select(a => new PlannedFile(a.Path, a.Length, a.FullHash)));
            }
        }

        // 悬空别名：leader 走岔了，但它们自己好好的，不该被连累。重新跑一遍，第一个自然成为新
        // leader。它们之间不再互相去重——这条路要求 leader 恰好在压缩窗口内被改写或读不开，本来
        // 就罕见，罕见路径上多存几份，换收尾逻辑保持线性。
        //
        // onItem 传 static _ => { }：Enqueue 是"一个 WorkItem 一次"，而 ProcessPackAsync 内部按
        // GroupIsFull 拆出几组是它自己决定的，外部无法预先申报对应的次数，手动补分母只会算错。
        // 零进零出，配对天然平衡。先例见上面 changed 成员改走单文件 blob 那一处。
        //
        // storeOnly 按**别名自己的路径**算，与装箱时同一个写法：规则按路径匹配，别名和 leader
        // 分属不同目录时压法完全可能不同，而一箱只能有一种压法。
        foreach (var side in orphanAliases.ToLookup(
                     f => packOptions.DontCompress?.MatchesFileOrAncestorDir(f.Path) ?? false))
        {
            await ProcessPackAsync(request, [.. side], side.Key, addressing, localResolver, info,
                storageByPath, tailByPath, overrides, postDiffUnreadable, uploadScope, static _ => { },
                uploadTracker, state, ct);
        }
```

- [ ] **Step 6: 跑新测试确认通过**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter "FullyQualifiedName~PackAliasDedupTests"
```

Expected: PASS，2 passed

- [ ] **Step 7: 跑全量测试**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: 全绿，0 failed。特别注意 `PackMemberDedupTests`（跨版本去重）、`BackupOrchestratorTests`、`UnreadablePackMemberTests`、`StagedReleaseTests` 都必须仍然通过——它们覆盖的正是被这次改动包围的那几条路径。

- [ ] **Step 8: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/BackupOrchestrator.cs \
        backend/tests/AzureStorageBackup.Api.Tests/PackAliasDedupTests.cs
git commit -m "feat(dedup): 同一轮备份内跨箱复用打包成员

同内容的后到者不入箱，只挂在首个之下；等消费者全部 join 之后按 leader
的最终态统一回填 StorageRef。判断只看最终态，因此装箱侧不需要任何新的
并发原语，消费者侧一行未改。

leader 走岔（压缩窗口内被改写、读不开、改走单文件 blob）时别名判为悬空，
重新跑一遍——那时它的内容已经不等于 leader 存下去的那份，指过去就是让
索引指向别人的内容。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: 钉住悬空回炉（T6）

这是本特性两根支柱之一：**别名不会指向错内容**。

**Files:**
- Modify: `backend/tests/AzureStorageBackup.Api.Tests/PackAliasDedupTests.cs`

**Interfaces:**
- Consumes: Task 2 的收尾悬空重跑路径；`IFileCompressor`
- Produces: 无

- [ ] **Step 1: 写测试**

在 `PackAliasDedupTests.cs` 的类尾（最后一个 `}` 之前）追加：

```csharp

    /// <summary>
    /// T6：leader 在压缩窗口里被改写 → 它被踢出那一箱、以新 hash 重新处理，于是它最终存下去的
    /// 内容**不再等于**别名的内容。这时别名绝不能指过去（那会让索引指向别人的内容、还原出错
    /// 数据），必须自己被重新备份一遍。
    /// <para>
    /// 两个文件都还原成各自应有的内容，就是这条红线守住了。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task An_Alias_Is_Rebuilt_When_Its_Leader_Changes_During_Compression()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var account = AzuriteAccount();
        var name = RandomName("packaliasorphan-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            var payload = string.Concat(Enumerable.Range(0, 400).Select(i => ((char)('a' + i % 26)).ToString()));
            const string mutated = "leader got rewritten while it was being compressed";
            Write("a/first.txt", payload);       // leader
            Write("c/second.txt", payload);      // 别名

            // 压缩之后把 leader 的内容换掉：重校验会发现它变了，把它踢出那一箱、以新 hash 重处理。
            var (backup, restore, store) = Build(
                new MutatingAfterCompressCompressor(new SevenZipCompressor(), _src, "a/first.txt", mutated));

            await backup.RunAsync(Request(account, name));

            var info = await store.ReadInfoAsync(account, name, null);
            var v1 = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            var first = v1.Entries.Single(e => e.Path == "a/first.txt");
            var second = v1.Entries.Single(e => e.Path == "c/second.txt");

            // 两条条目的内容身份必须已经分道扬镳——别名绝不能还挂在 leader 那份新内容上。
            Assert.NotEqual(first.FullHash, second.FullHash);

            // 决定性的一条：还原出来的必须各是各的内容。
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(mutated, await File.ReadAllTextAsync(Path.Combine(_dst, "a", "first.txt")));
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "c", "second.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }

    /// <summary>压缩**之后**改写目标成员的内容，模拟"文件在处理中变化"（§9、PRD 特别说明 D）。
    /// 分组路径先 hash 后压，所以挂在 CompressAsync 之后，重校验据此发现内容变了。
    /// 与 BackupOrchestratorTests.MutatingCompressor 同一套手法，只覆盖这里用得到的那一半。</summary>
    private sealed class MutatingAfterCompressCompressor(
        IFileCompressor inner, string rootPath, string relPath, string newContent) : IFileCompressor
    {
        private int _fired;

        public async Task<CompressionResult> CompressAsync(
            CompressionRequest request, CancellationToken ct = default)
        {
            var result = await inner.CompressAsync(request, ct);
            if (request.Entries.Contains(relPath) && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                var full = Path.Combine(rootPath, relPath.Replace('/', Path.DirectorySeparatorChar));
                File.WriteAllText(full, newContent);
                File.SetLastWriteTimeUtc(full, File.GetLastWriteTimeUtc(full).AddSeconds(7));
            }
            return result;
        }

        public Task<CompressionResult> CompressStreamAsync(
            StreamCompressionRequest request, Func<Stream, CancellationToken, Task<long>> writeSource,
            CancellationToken ct = default)
            => inner.CompressStreamAsync(request, writeSource, ct);

        public Task ExtractAsync(
            string firstVolumePath, string outputDir, string? password, CancellationToken ct = default)
            => inner.ExtractAsync(firstVolumePath, outputDir, password, ct);

        public Task<IReadOnlyList<ArchiveEntry>> ListEntriesAsync(
            string firstVolumePath, string? password, CancellationToken ct = default)
            => inner.ListEntriesAsync(firstVolumePath, password, ct);

        public Task<long> ExtractToStreamAsync(
            string firstVolumePath, string? entryName, string? password, Stream destination,
            CancellationToken ct = default)
            => inner.ExtractToStreamAsync(firstVolumePath, entryName, password, destination, ct);
    }
```

**注意：** `IFileCompressor` 的成员集合以 `backend/src/AzureStorageBackup.Api/Services/` 下该接口的当前定义为准。若编译报缺成员，照 `BackupOrchestratorTests.cs:87` 的 `MutatingCompressor` 补齐同样的透传实现——那个类是完整的参照。

- [ ] **Step 2: 跑测试**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter "FullyQualifiedName~PackAliasDedupTests"
```

Expected: PASS，3 passed

若 `An_Alias_Is_Rebuilt_When_Its_Leader_Changes_During_Compression` 失败在 `c/second.txt` 还原成了 `mutated`，说明回填的三个否决条件没生效——回到 Task 2 Step 5 检查 `overrides.ContainsKey(leaderPath)` 那一条。

- [ ] **Step 3: 提交**

```bash
git add backend/tests/AzureStorageBackup.Api.Tests/PackAliasDedupTests.cs
git commit -m "test(dedup): 钉住 leader 走岔时别名不指向错内容

leader 在压缩窗口里被改写后以新 hash 重处理，别名的内容已经不等于它
存下去的那份——必须自己重新备份，而不是复制一个指向别人内容的引用。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: 钉住保留清理与还原链路（T3、T4）

另一根支柱：**别名不会被错删**。T3 是整个特性最容易被将来某次重构悄悄踩坏的地方。

**Files:**
- Modify: `backend/tests/AzureStorageBackup.Api.Tests/PackAliasDedupTests.cs`

**Interfaces:**
- Consumes: Task 2 的回填路径；`RetentionOptions`（保留策略的配置形状以 `BackupEngineOptions` 当前定义为准，参照 `PackMemberDedupTests.Retention_Keeps_A_Pack_Still_Referenced_Through_Dedup` 里 `keepOne` 的写法）
- Produces: 无

- [ ] **Step 1: 写测试**

在 `PackAliasDedupTests.cs` 类尾追加：

```csharp

    /// <summary>
    /// T3——本特性最要紧的一条。leader 那个**路径**的文件被删掉之后，别名仍然要能还原。
    /// <para>
    /// 那时 liveByPack 里那个 entryName 由别名条目独自提供（RetentionCleaner 按 EntryName 归组，
    /// 不按 fullHash），所以包不删、成员不死、解压目录里照样取得到。这条链每一环都对，别名才
    /// 活得下来——而它极容易被将来某次"顺手改成按 hash 归组"的重构悄悄踩断。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task An_Alias_Survives_After_Its_Leader_Path_Is_Deleted()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, restore, store) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packaliasdel-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        // 只保留最新一个版本：v1 退役，包只能靠 v2 里那条别名条目钉住。
        var keepOne = Request(account, name) with
        {
            Options = new BackupEngineOptions
            {
                Plan = new PlanOptions { SingleFileThresholdBytes = 5_000_000, MaxPackMembers = 1 },
                Retention = new RetentionPolicy { Mode = RetentionMode.VersionOnly, MaxVersions = 1 },
            },
        };

        try
        {
            var payload = string.Concat(Enumerable.Range(0, 400).Select(i => ((char)('a' + i % 26)).ToString()));
            Write("a/first.txt", payload);       // leader
            Write("c/second.txt", payload);      // 别名
            await backup.RunAsync(keepOne);

            var packsAfterV1 = await CountPacksAsync(cc);
            Assert.Equal(1, packsAfterV1);       // 同内容只装了一箱

            // v2：把 leader 那个路径删掉。包里那个成员此后只被别名条目引用着。
            File.Delete(Path.Combine(_src, "a", "first.txt"));
            await backup.RunAsync(keepOne);

            // 包一个都不能少——删了就等于把 c/second.txt 的数据删了。
            Assert.Equal(packsAfterV1, await CountPacksAsync(cc));

            var info = await store.ReadInfoAsync(account, name, null);
            var v2 = await store.ReadIndexAsync(account, name, info!.Versions[^1].IndexBlob, null);
            Assert.DoesNotContain(v2.Entries, e => e.Path == "a/first.txt");
            var second = v2.Entries.Single(e => e.Path == "c/second.txt");
            // 成员名仍是**最初**那个已经不存在的路径——还原要按它去归档里取。
            Assert.Equal("a/first.txt", second.Storage!.EntryName);

            // 决定性的一条：内容还在，还原得回来。
            await restore.RunAsync(new RestoreRequest
            {
                Account = account, Container = name, TargetRoot = _dst,
            });
            Assert.Equal(payload, await File.ReadAllTextAsync(Path.Combine(_dst, "c", "second.txt")));
            Assert.False(File.Exists(Path.Combine(_dst, "a", "first.txt")));
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }
```

- [ ] **Step 2: 跑测试**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter "FullyQualifiedName~PackAliasDedupTests"
```

Expected: PASS，4 passed

- [ ] **Step 3: 提交**

```bash
git add backend/tests/AzureStorageBackup.Api.Tests/PackAliasDedupTests.cs
git commit -m "test(dedup): 钉住 leader 路径被删后别名仍能还原

包只被别名条目钉住时保留清理不能删它——liveByPack 按 EntryName 归组
而不是按 fullHash，这条链断了别名的数据就没了。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: 钉住死重压实与检查（T5、T7），补文档

**Files:**
- Modify: `backend/tests/AzureStorageBackup.Api.Tests/PackAliasDedupTests.cs`
- Modify: `docs/backup-feature-design.md`（若其中有描述打包去重范围的段落）

**Interfaces:**
- Consumes: Task 2 的回填路径；`BackupChecker`（构造方式参照 `BackupCheckerTests`）
- Produces: 无

- [ ] **Step 1: 写检查测试（T7）**

在 `PackAliasDedupTests.cs` 类尾追加：

```csharp

    /// <summary>
    /// T7：两条条目指向同一个 pack 成员，检查必须把两条都判健康。
    /// <para>
    /// BackupChecker 逐条查 actual[entryName]，两条查的是同一项、内容当然相同；而
    /// "归档吐出的成员数 == 列举出的成员数" 那条前提也不受影响——别名不进归档。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Check_Reports_Both_Entries_Healthy()
    {
        Skip.IfNot(AzuriteReachable() && SevenZip(), "Azurite/7-Zip unavailable");

        var (backup, _, _) = Build();
        var account = AzuriteAccount();
        var name = RandomName("packaliaschk-");
        var factory = new BlobClientFactory(TestSecrets.Reader);
        var cc = factory.CreateServiceClient(account).GetBlobContainerClient(name);
        await cc.CreateIfNotExistsAsync();

        try
        {
            var payload = string.Concat(Enumerable.Range(0, 400).Select(i => ((char)('a' + i % 26)).ToString()));
            Write("a/first.txt", payload);
            Write("c/second.txt", payload);
            await backup.RunAsync(Request(account, name));

            var checker = new BackupChecker(
                factory, new BackupInfoStore(factory, new SevenZipArchiveCodec()),
                new SevenZipCompressor(), new FileHasher(), Path.Combine(_temp, "check"));
            var report = await checker.CheckAsync(account, name, null, null, new CheckOptions());

            // 一条损坏都不该有，两条条目都要出现在结论里。
            Assert.True(report.Ok);
            Assert.Empty(report.CorruptedPaths);
            Assert.Contains(report.Findings, f => f.Path == "a/first.txt");
            Assert.Contains(report.Findings, f => f.Path == "c/second.txt");
        }
        finally { await cc.DeleteIfExistsAsync(); }
    }
```

- [ ] **Step 2: 跑测试**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj --filter "FullyQualifiedName~PackAliasDedupTests"
```

Expected: PASS，5 passed

- [ ] **Step 3: 死重压实的口径（T5）**

先确认现有 `DeadWeightCompactorTests` 是否已覆盖"多条条目指向同一 entryName"。跑：

```bash
grep -n "EntryName" backend/tests/AzureStorageBackup.Api.Tests/DeadWeightCompactorTests.cs
```

若已有等价覆盖，跳过本步并在提交信息里说明；若没有，在 `DeadWeightCompactorTests.cs` 里加一个纯计算的用例：构造一个 `PackInfo`（`OriginalBytes` = 两个实际成员之和）与一个 `liveByPack`（其中一个 entryName 由两条条目提供），断言 `deadBytes` 既不为负、也不因别名而虚低。具体构造方式照该文件既有用例的写法。

- [ ] **Step 4: 跑全量测试**

```bash
dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj
```

Expected: 全绿，0 failed

- [ ] **Step 5: 补设计文档**

在 `docs/backup-feature-design.md` 里找到描述打包成员去重范围的段落（用 `grep -n "pack" docs/backup-feature-design.md` 定位）。若存在"打包成员只做跨版本去重"一类的表述，改成同时覆盖本轮内跨箱，并链到
`docs/superpowers/specs/2026-08-07-pack-alias-dedup-design.md`。若没有这样的段落，跳过本步。

- [ ] **Step 6: 提交**

```bash
git add backend/tests/AzureStorageBackup.Api.Tests/ docs/
git commit -m "test(dedup): 钉住检查与死重压实对别名条目的口径

两条条目指向同一 pack 成员时检查须双双判健康；OriginalBytes 只算实际
成员、liveBytes 按 EntryName 去重，deadBytes 算不出负数。

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## Spec 测试项 → 任务映射

| Spec | 落在哪 | 说明 |
|---|---|---|
| 单测（四项判据、缺项、leader 列表） | Task 1 | |
| T1 跨箱生效 | Task 2 | |
| T2 两条路径各自还原 | Task 2 | 与 T1 同一个用例 |
| T3 leader 路径被删后别名仍能还原 | Task 4 | **支柱之一** |
| T4 保留清理不错删 | Task 4 | 与 T3 合并：`keepOne` 让 v1 退役，包只剩别名钉住，比分开测更严 |
| T5 死重压实口径 | Task 5 Step 3 | 先查既有覆盖，没有才补 |
| T6 悬空回炉 | Task 3 | **支柱之一** |
| T7 Check 通过 | Task 5 | |
| T8 进度计数收敛 | Task 2（`ChangedFiles` 断言）+ Task 2 Step 7（全量回归） | 别名零进零出是**构造上**成立的（不 `Enqueue` 不 `ReportItem`），所以这里钉的是"记账没把别名漏掉"；进度条本身的回归由 `BackupProgressDetailTests` 等既有测试覆盖 |

## 完成标准

- `dotnet test backend/tests/AzureStorageBackup.Api.Tests/AzureStorageBackup.Api.Tests.csproj` 全绿，且 Azurite 在跑（否则集成测试静默跳过，等于什么都没验证）
- 消费者侧四个方法（`ProcessPackAsync`、`RecordPack`、`CompressPackTolerantAsync`、`UploadStagedPackAsync`）的 diff 为空
- `RetentionCleaner` / `DeadWeightCompactor` / `RestoreOrchestrator` / `BackupChecker` / `BackupRepairer` 的 diff 为空
- 索引 schema 无新字段

## 已知不做

- 不追溯合并历史版本里已有的重复
- 不为未变更条目补算 `tailHash`
- 不给悬空别名之间再做一层去重
- 不改单文件 blob 那条路
