using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 生命周期的最后一环：**从已有 storage 导入到全新环境**。
/// 云端备份由一台「旧机器」（独立的本地数据库 + 编排器）建立；承载 HTTP 的宿主从未见过这个 container——
/// 无 BackupConfig、无 CachedVersionIndex、无 LocalBackupState。经 <c>POST /api/backup-configs/import</c>
/// 导入后，断言能列出全部版本、本地权威状态被回填，并能把任一版本逐字节还原出来。
/// 加密与不加密都覆盖：两者的信息文件走不同的 blob 名（IndexBlobName vs EncryptedIndexBlobName）。
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackupImportLifecycleTests : IClassFixture<TestWebAppFactory>, IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string AzuriteEndpoint = "http://127.0.0.1:10000/devstoreaccount1";

    private static readonly DateTime MtimeBase = new(2021, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly TestWebAppFactory _factory;
    private readonly HttpClient _client;
    private readonly string _base;
    private readonly string _src;
    private readonly string _temp;
    private int _mtimeSeq;

    public BackupImportLifecycleTests(TestWebAppFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _base = Path.Combine(Path.GetTempPath(), "asb-imp-" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(_base, "src");
        _temp = Path.Combine(_base, "temp");
        Directory.CreateDirectory(_src);
    }

    public void Dispose()
    {
        _client.Dispose();
        try { Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;
    private static string RandomName(string p) => p + Guid.NewGuid().ToString("N")[..8];

    // ───────────────────────── 源树与快照 ─────────────────────────

    private void Write(string rel, byte[] content)
    {
        var full = Path.Combine(_src, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        File.SetLastWriteTimeUtc(full, MtimeBase.AddMinutes(++_mtimeSeq));
    }

    private void WriteText(string rel, string text) => Write(rel, Encoding.UTF8.GetBytes(text));

    private static byte[] Rand(int size, int seed)
    {
        var buf = new byte[size];
        new Random(seed).NextBytes(buf);
        return buf;
    }

    private Dictionary<string, byte[]> Snapshot()
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(_src, "*", SearchOption.AllDirectories))
            map[Rel(_src, f)] = File.ReadAllBytes(f);
        return map;
    }

    private static string Rel(string root, string full) =>
        Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');

    private static void AssertTreeEquals(Dictionary<string, byte[]> expected, string target, string label)
    {
        var actual = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Rel(target, f), StringComparer.Ordinal);

        Assert.Equal(expected.Keys.Order(), actual.Keys.Order()); // 目录结构一致
        foreach (var (rel, bytes) in expected)
        {
            var got = File.ReadAllBytes(actual[rel]);
            Assert.True(bytes.AsSpan().SequenceEqual(got),
                $"{label}: restored content differs for {rel} ({bytes.Length} vs {got.Length} bytes)");
        }
    }

    // ───────────────────────── 「旧机器」：在云端造出一个多版本备份 ─────────────────────────

    /// <summary>
    /// 用**独立的**本地数据库跑真实编排器写出云端备份，随后丢弃该数据库——
    /// 于是承载 HTTP 的宿主对这个 container 一无所知，导入面对的是真正的空环境。
    /// </summary>
    private async Task SeedCloudBackupAsync(string container, string? password, Action beforeSecondRun)
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();

        var blobFactory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(blobFactory, new SevenZipArchiveCodec());
        var hasher = new FileHasher();
        var tracked = new TrackedInfoStore(store, new LocalBackupStateStore(db));
        var indexCache = new LocalIndexCache(db, store);
        var staging = new StagingArea(
            Path.Combine(_temp, "compress"), Path.Combine(_temp, "staged"), () => 200_000_000);
        var orchestrator = new BackupOrchestrator(
            new LocalFileScanner(), new BackupDiffer(hasher), new GroupingPlanner(),
            new SevenZipCompressor(), new BlobUploader(blobFactory), blobFactory, store, staging,
            new RetentionCleaner(blobFactory, store, new RetentionEvaluator(), indexCache: indexCache, trackedInfo: tracked),
            hasher, indexCache: indexCache, trackedInfo: tracked);

        var account = new Account
        {
            Id = 1,
            Name = "azurite",
            BlobEndpoint = AzuriteEndpoint,
            AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
            Region = AzureRegion.Global,
        };

        BackupRequest Request() => new()
        {
            Account = account,
            Container = container,
            LocalRoot = _src,
            Name = "imported-fixture",
            Description = "created on another machine",
            Password = password,
            Options = new BackupEngineOptions { Plan = new PlanOptions { SingleFileThresholdBytes = 20_000 } },
        };

        var v1 = await orchestrator.RunAsync(Request());
        Assert.Equal(1, v1.Version);
        beforeSecondRun();
        var v2 = await orchestrator.RunAsync(Request());
        Assert.Equal(2, v2.Version);
    }

    // ───────────────────────── 导入 → 列版本 → 还原 ─────────────────────────

    [SkippableTheory]
    [InlineData("import pass phrase")] // 加密：密码经请求体明文进来，落库必须是密文，还原时在咽喉处解密
    [InlineData(null)]
    public async Task Import_Into_Empty_Environment_Lists_All_Versions_And_Restores_Them_Byte_For_Byte(string? password)
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var container = RandomName(password is null ? "implife-" : "implifeenc-");
        var blobFactory = new BlobClientFactory(TestSecrets.Reader);
        var azurite = new Account
        {
            BlobEndpoint = AzuriteEndpoint,
            AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
            Region = AzureRegion.Global,
        };
        var cc = blobFactory.CreateServiceClient(azurite).GetBlobContainerClient(container);

        try
        {
            // 「旧机器」造出两个版本的云端备份。
            Write("docs/one.txt", Rand(3000, 31));
            Write("docs/two.txt", Rand(3000, 32));
            Write("media/blob.bin", Rand(40_000, 33)); // ≥20K → 单文件 data blob
            WriteText("top.txt", "first revision");
            var snap1 = Snapshot();

            Dictionary<string, byte[]>? snap2 = null;
            await SeedCloudBackupAsync(container, password, () =>
            {
                Write("docs/two.txt", Rand(3000, 132));   // 改
                WriteText("docs/three.txt", "brand new"); // 增
                snap2 = Snapshot();
            });
            Assert.NotNull(snap2);

            // 云端确实按加密/非加密写了不同的信息文件 blob 名。
            Assert.True(await cc.GetBlobClient(password is null
                ? BackupDiscovery.IndexBlobName
                : BackupDiscovery.EncryptedIndexBlobName).ExistsAsync());

            // ─── 空环境：宿主只知道账户，对该 container 一无所知 ───
            var account = await (await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
                "azurite", null, AzuriteEndpoint, AzureRegion.Global, AzuriteKey,
                false, ProxyMode.Independent, null, null, null, null)))
                .Content.ReadFromJsonAsync<AccountResponse>();
            Assert.NotNull(account);
            await AssertLocalEnvironmentEmptyAsync(account!.Id, container);

            // ─── 导入 ───
            var response = await _client.PostAsJsonAsync("/api/backup-configs/import",
                new ImportRequest(account.Id, container, password));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var imported = await response.Content.ReadFromJsonAsync<BackupConfigResponse>();
            Assert.NotNull(imported);
            Assert.Equal("imported-fixture", imported!.Name);          // 配置从信息文件恢复
            Assert.Equal("created on another machine", imported.Description);
            Assert.Equal(container, imported.ContainerName);
            Assert.Equal(_src, imported.LocalRoot);                     // sourceRootHint
            Assert.Equal(password is not null, imported.HasPassword);

            // 本地权威状态与全部版本索引都已回填（之后备份/还原平时不再下载云端索引）。
            await AssertLocalStateSeededAsync(account.Id, container, expectedVersions: 2);

            // ─── 列出全部版本 ───
            var versions = await _client.GetFromJsonAsync<List<VersionRow>>(
                $"/api/backup-configs/{imported.Id}/versions");
            Assert.NotNull(versions);
            Assert.Equal([1, 2], versions!.Select(v => v.version));
            Assert.Equal(snap1.Count, versions[0].files);
            Assert.Equal(snap2!.Count, versions[1].files);

            // ─── 从导入的备份还原（两个版本都要逐字节正确）───
            await RestoreAndAssertAsync(imported.Id, version: 1, snap1, "v1");
            await RestoreAndAssertAsync(imported.Id, version: null, snap2!, "latest"); // 缺省=最新
        }
        finally
        {
            await cc.DeleteIfExistsAsync();
        }
    }

    /// <summary>导入前：该 container 在宿主本地没有任何痕迹——这正是「空环境」的定义。</summary>
    private async Task AssertLocalEnvironmentEmptyAsync(int accountId, string container)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.BackupConfigs.Where(c => c.ContainerName == container).ToListAsync());
        Assert.Empty(await db.CachedVersionIndexes
            .Where(c => c.AccountId == accountId && c.Container == container).ToListAsync());
        Assert.Empty(await db.LocalBackupStates
            .Where(s => s.AccountId == accountId && s.Container == container).ToListAsync());
    }

    /// <summary>导入后：本地权威状态被云端信息文件回填，且每个版本的索引都进了本地缓存。</summary>
    private async Task AssertLocalStateSeededAsync(int accountId, string container, int expectedVersions)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var state = await db.LocalBackupStates
            .SingleOrDefaultAsync(s => s.AccountId == accountId && s.Container == container);
        Assert.NotNull(state);
        Assert.NotEmpty(state!.InfoBytes);
        Assert.NotEmpty(state.ETag); // 有 ETag 才能做后续的条件写

        var cached = await db.CachedVersionIndexes
            .Where(c => c.AccountId == accountId && c.Container == container)
            .Select(c => c.Version).ToListAsync();
        Assert.Equal(Enumerable.Range(1, expectedVersions), cached.Order());
    }

    /// <summary>经 HTTP 还原到一个全新空目录，并与快照逐字节比对。</summary>
    private async Task RestoreAndAssertAsync(int configId, int? version, Dictionary<string, byte[]> expected, string label)
    {
        var target = Path.Combine(_base, "restore", label);

        var start = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{configId}/restore", new RestoreRequestBody(target, version));
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

        var run = await PollUntilDoneAsync<RestoreRunRow>(
            $"/api/backup-configs/{configId}/restore", s => s.status != "Running");
        Assert.NotNull(run);
        Assert.Equal("Completed", run!.status);
        Assert.Null(run.error);
        if (version is { } v)
            Assert.Equal(v, run.version);
        Assert.Equal(expected.Count, run.restoredFiles);

        AssertTreeEquals(expected, target, label);
    }

    private async Task<T?> PollUntilDoneAsync<T>(string url, Func<T, bool> done) where T : class
    {
        for (var i = 0; i < 600; i++) // 宽松：并发集成测试在少核机器上会拖慢后台 job
        {
            var s = await (await _client.GetAsync(url)).Content.ReadFromJsonAsync<T>();
            if (s is not null && done(s))
                return s;
            await Task.Delay(200);
        }
        return null;
    }

    // 与后端 camelCase JSON 对应
    private sealed record VersionRow(int version, long files, long bytes, long changedFiles);

    private sealed record RestoreRunRow(string status, int? version, int? restoredFiles, int? skippedFiles, string? error);
}
