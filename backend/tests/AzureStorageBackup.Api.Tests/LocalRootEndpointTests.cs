using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

public class LocalRootEndpointTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>, IDisposable
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly IServiceProvider _services = factory.Services;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lre-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private async Task<int> CreateAccountAsync()
    {
        var req = new AccountRequest(
            Name: "acct-" + Guid.NewGuid().ToString("N")[..6],
            Description: null,
            BlobEndpoint: "https://example.blob.core.windows.net",
            Region: AzureRegion.Global,
            AccountKey: "dGVzdGtleQ==",
            UseProxy: false,
            ProxyMode: ProxyMode.Independent,
            ProxyHost: null, ProxyPort: null, ProxyUsername: null, ProxyPassword: null);
        var res = await _client.PostAsJsonAsync("/api/accounts", req);
        var account = await res.Content.ReadFromJsonAsync<AccountResponse>();
        return account!.Id;
    }

    // Azurite 的 well-known 账户与密钥（与 BackupConfigEndpointsTests 一致）。
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string AzuriteEndpoint = "http://127.0.0.1:10000/devstoreaccount1";

    /// <summary>凡是会真正走到 LoadBaselineAsync 的「确实没有基线」测试，都必须用这个而不是
    /// CreateAccountAsync：那个用的是个解析不到的假域名，TrackedInfoStore.LoadAsync 在没有本地状态时
    /// 会落到云端回填，假域名下这一步是真的网络异常（几十秒超时），会被新代码识别成
    /// BaselineUnreadable 而不是 NoBaseline —— 这不是本次要测的东西。Azurite 上 container
    /// 确实不存在时，ExistsAsync 干净地返回 false，无本地状态、无云端信息文件，是真正的「没有」。</summary>
    private async Task<int> CreateAzuriteAccountAsync()
    {
        var req = new AccountRequest(
            Name: "azurite-" + Guid.NewGuid().ToString("N")[..6], Description: null,
            BlobEndpoint: AzuriteEndpoint, Region: AzureRegion.Global, AccountKey: AzuriteKey,
            UseProxy: false, ProxyMode: ProxyMode.Independent,
            ProxyHost: null, ProxyPort: null, ProxyUsername: null, ProxyPassword: null);
        var res = await _client.PostAsJsonAsync("/api/accounts", req);
        var account = await res.Content.ReadFromJsonAsync<AccountResponse>();
        return account!.Id;
    }

    /// <summary>建一条配置，直接落库（绕开创建端点对本地根存在性的校验）。</summary>
    private async Task<int> CreateConfigAsync(int accountId, string localRoot)
    {
        using var scope = _services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        var created = await svc.CreateAsync(new BackupConfig
        {
            AccountId = accountId,
            ContainerName = "c" + Guid.NewGuid().ToString("N")[..8],
            Name = "photos",
            LocalRoot = localRoot,
            IndexTier = StorageTier.Hot,
            DataTier = StorageTier.Cool,
        });
        return created.Id;
    }

    private async Task<string> ContainerOfAsync(int configId)
    {
        using var scope = _services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        return (await svc.GetAsync(configId))!.ContainerName;
    }

    /// <summary>这条备份在操作日志里的来源键。全仓形如 "{op}:{accountId}/{container}"
    /// （OperationLogService.cs:91-96）——测试自己也照这个形状拼，才能盯住端点没写成别的。</summary>
    private async Task<string> SourceKeyOfAsync(int configId)
    {
        using var scope = _services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        var config = (await svc.GetAsync(configId))!;
        return $"backup:{config.AccountId}/{config.ContainerName}";
    }

    private List<LogEntry> LogsOf(string source)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return [.. db.LogEntries.Where(e => e.Source == source)];
    }

    private async Task<string> LocalRootOfAsync(int configId)
    {
        using var scope = _services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        return (await svc.GetAsync(configId))!.LocalRoot;
    }

    /// <summary>直接写本地权威信息文件（TrackedInfoStore.LoadAsync 命中本地则不读云端），
    /// 与 BackupConfigEndpointsTests.SeedLocalInfo 同一条路数。返回 identityTicks，供 SeedIndex 用。</summary>
    private long SeedLocalInfo(int accountId, string container, List<BackupVersion> versions)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var info = new BackupInfoFile
        {
            Backup = new BackupMeta { Name = "seed", CreatedAt = createdAt, Encrypted = false },
            Versions = versions,
        };
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.LocalBackupStates.Add(new LocalBackupState
        {
            AccountId = accountId, Container = container,
            InfoBytes = IndexSerializer.SerializeInfoFile(info), ETag = "seed-etag",
        });
        db.SaveChanges();
        return createdAt.UtcTicks;
    }

    /// <summary>本地信息文件写成一段合法性检不过的字节——format 字节 99 大于当前支持的最新 format，
    /// IndexSerializer.DeserializeInfoFile 会在读完第一个字节后立刻抛 NotSupportedException。
    /// 用来在测试里稳定复现「有历史但索引读不出来」（BaselineUnreadable），不依赖加密/云端失败这些
    /// 更难摆布的失败面。</summary>
    private void SeedCorruptLocalInfo(int accountId, string container)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.LocalBackupStates.Add(new LocalBackupState
        {
            AccountId = accountId, Container = container,
            InfoBytes = [99], ETag = "seed-etag",
        });
        db.SaveChanges();
    }

    private void SeedIndex(int accountId, string container, int version, long identityTicks, VersionIndex index)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.CachedVersionIndexes.Add(new CachedVersionIndex
        {
            AccountId = accountId, Container = container, Version = version,
            IdentityTicks = identityTicks, Bytes = IndexSerializer.SerializeIndex(index),
        });
        db.SaveChanges();
    }

    /// <summary>建一条「基线与新根完全对不上」的配置：新根是个空目录，基线索引里唯一的文件在那儿
    /// 一个都找不到，抽样匹配率 0% → Rejected，需要 force 才能写。供 force 闸门测试复用。
    /// <paramref name="localRoot"/> 传 null 表示用 _dir 当当前根；传 "" 则模拟导入时没拿到
    /// SourceRootHint 的那种配置。</summary>
    private async Task<(int Id, string Target)> SeedMismatchingBaselineAsync(string? localRoot = null)
    {
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var accountId = await CreateAccountAsync();
        var id = await CreateConfigAsync(accountId, localRoot ?? _dir);
        var container = await ContainerOfAsync(id);

        var identityTicks = SeedLocalInfo(accountId, container,
        [
            new BackupVersion
            {
                Version = 1, CreatedAt = DateTimeOffset.UtcNow, IndexBlob = "v1.index",
                Stats = new VersionStats(1, 5, 1, 5),
            },
        ]);
        SeedIndex(accountId, container, 1, identityTicks, new VersionIndex
        {
            Version = 1,
            Entries =
            [
                new IndexEntry
                {
                    Path = "a.txt", Kind = "file", Length = 5, Mtime = DateTimeOffset.UtcNow, Permissions = "644",
                    Storage = new StorageRef { Kind = "blob", Ref = "data/abc" },
                },
            ],
        });

        return (id, target);
    }

    /// <summary>建一条「有历史但索引读不出来」的配置（见 SeedCorruptLocalInfo）。</summary>
    private async Task<(int Id, string Target)> SeedUnreadableBaselineAsync()
    {
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var accountId = await CreateAccountAsync();
        var id = await CreateConfigAsync(accountId, _dir);
        var container = await ContainerOfAsync(id);

        SeedCorruptLocalInfo(accountId, container);

        return (id, target);
    }

    [Fact]
    public async Task Preview_Rejects_A_Relative_Path()
    {
        Directory.CreateDirectory(_dir);
        var id = await CreateConfigAsync(await CreateAccountAsync(), _dir);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root/preview", new { newRoot = "relative/path" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Preview_Rejects_An_Empty_Path()
    {
        Directory.CreateDirectory(_dir);
        var id = await CreateConfigAsync(await CreateAccountAsync(), _dir);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root/preview", new { newRoot = "" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Preview_Rejects_A_Path_That_Does_Not_Exist()
    {
        Directory.CreateDirectory(_dir);
        var id = await CreateConfigAsync(await CreateAccountAsync(), _dir);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root/preview",
            new { newRoot = Path.Combine(_dir, "nope") });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Preview_Rejects_A_Path_That_Is_A_File()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "afile");
        await File.WriteAllTextAsync(file, "x");
        var id = await CreateConfigAsync(await CreateAccountAsync(), _dir);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root/preview", new { newRoot = file });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Preview_Reports_NoBaseline_When_The_Backup_Has_No_Versions()
    {
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var id = await CreateConfigAsync(await CreateAzuriteAccountAsync(), _dir);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root/preview", new { newRoot = target });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<LocalRootPreviewResponse>();
        Assert.Equal(nameof(LocalRootVerdict.NoBaseline), body!.Verdict);
        Assert.NotNull(body.Reason);
    }

    /// <summary>preview 是纯查询：跑完之后配置必须一字未动。</summary>
    [Fact]
    public async Task Preview_Does_Not_Change_Anything()
    {
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var id = await CreateConfigAsync(await CreateAccountAsync(), _dir);

        await _client.PostAsJsonAsync($"/api/backup-configs/{id}/local-root/preview", new { newRoot = target });

        using var scope = _services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        var config = await svc.GetAsync(id);
        Assert.Equal(_dir, config!.LocalRoot);
    }

    [Fact]
    public async Task Apply_Moves_The_Root_When_There_Is_No_Baseline()
    {
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var id = await CreateConfigAsync(await CreateAzuriteAccountAsync(), _dir);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = false });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal(target, body!.LocalRoot);
    }

    /// <summary>导入时没拿到 SourceRootHint 的配置，根是空串——它必须能被补上。</summary>
    [Fact]
    public async Task Apply_Fills_In_An_Empty_Root_Left_Behind_By_Import()
    {
        Directory.CreateDirectory(_dir);
        var id = await CreateConfigAsync(await CreateAzuriteAccountAsync(), localRoot: "");

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = _dir, force = false });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal(_dir, body!.LocalRoot);
    }

    [Fact]
    public async Task Apply_Is_Refused_While_The_Backup_Is_Busy()
    {
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var accountId = await CreateAccountAsync();
        var id = await CreateConfigAsync(accountId, _dir);

        string container;
        using (var scope = _services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
            container = (await svc.GetAsync(id))!.ContainerName;
        }

        var busy = _services.GetRequiredService<BackupBusyTracker>();
        Assert.True(busy.TryAcquire(accountId, container, "BackingUp"));
        try
        {
            var res = await _client.PostAsJsonAsync(
                $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = false });

            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);

            using var scope = _services.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
            Assert.Equal(_dir, (await svc.GetAsync(id))!.LocalRoot);   // 未落库
        }
        finally
        {
            busy.Release(accountId, container);
        }
    }

    /// <summary>force 闸门是这整个功能的安全依据：NeedsConfirm/Rejected 不带 force 必须被拒、
    /// 库里的 LocalRoot 必须一字未动。之前 10 个测试全走 NoBaseline 分支（needsForce 恒为 false），
    /// 这条闸门从未被真正执行过——写反一个布尔值也会全绿。</summary>
    [Fact]
    public async Task Apply_Refuses_A_Mismatching_Baseline_Without_Force()
    {
        var (id, target) = await SeedMismatchingBaselineAsync();

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = false });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("local_root_mismatch", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            nameof(LocalRootVerdict.Rejected),
            doc.RootElement.GetProperty("preview").GetProperty("verdict").GetString());

        Assert.Equal(_dir, await LocalRootOfAsync(id));   // 未落库
    }

    /// <summary>同一个不匹配的基线，这次带 force:true —— 必须真的写进去，闸门的另一半。</summary>
    [Fact]
    public async Task Apply_Writes_A_Mismatching_Baseline_When_Forced()
    {
        var (id, target) = await SeedMismatchingBaselineAsync();

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = true });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal(target, body!.LocalRoot);
        Assert.Equal(target, await LocalRootOfAsync(id));
    }

    /// <summary>
    /// 导入时没拿到 SourceRootHint 的配置根是空串，可它的版本索引在导入当下就整批落进了
    /// 本地缓存（BackupConfigEndpoints.cs:110-127）。从前"当前根为空"会把整段比对短路成
    /// NoBaseline 免检放行——偏偏这正是用户最可能在猜挂载点的场合，最不该免检。
    /// 现在能不能比对只看基线在不在：填错目录照样要被拦下。
    /// </summary>
    [Fact]
    public async Task An_Imported_Backup_With_No_Root_Is_Still_Checked_Against_Its_Index()
    {
        var (id, target) = await SeedMismatchingBaselineAsync(localRoot: "");

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = false });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(
            nameof(LocalRootVerdict.Rejected),
            doc.RootElement.GetProperty("preview").GetProperty("verdict").GetString());

        Assert.Equal("", await LocalRootOfAsync(id));   // 未落库
    }

    /// <summary>索引读不出来（Finding 1）：preview 必须报 BaselineUnreadable，而不是伪装成
    /// NoBaseline 直接放行——Reason 里要能看到底层异常消息，NAS 用户没有命令行，这是唯一的诊断。</summary>
    [Fact]
    public async Task Preview_Reports_BaselineUnreadable_When_The_Local_Index_Is_Corrupt()
    {
        var (id, target) = await SeedUnreadableBaselineAsync();

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root/preview", new { newRoot = target });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<LocalRootPreviewResponse>();
        Assert.Equal(nameof(LocalRootVerdict.BaselineUnreadable), body!.Verdict);
        Assert.Contains("could not be read", body.Reason);
        Assert.Contains("newer than supported", body.Reason);   // 底层异常消息确实透传出来了
    }

    /// <summary>BaselineUnreadable 也走 force 闸门：不带 force 必须被拒、库里未落地。</summary>
    [Fact]
    public async Task Apply_Refuses_An_Unreadable_Baseline_Without_Force()
    {
        var (id, target) = await SeedUnreadableBaselineAsync();

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = false });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("local_root_mismatch", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            nameof(LocalRootVerdict.BaselineUnreadable),
            doc.RootElement.GetProperty("preview").GetProperty("verdict").GetString());

        Assert.Equal(_dir, await LocalRootOfAsync(id));   // 未落库
    }

    /// <summary>...带 force:true 则必须真的写进去。</summary>
    [Fact]
    public async Task Apply_Writes_Through_An_Unreadable_Baseline_When_Forced()
    {
        var (id, target) = await SeedUnreadableBaselineAsync();

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = true });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal(target, body!.LocalRoot);
        Assert.Equal(target, await LocalRootOfAsync(id));
    }

    /// <summary>
    /// 审计日志必须挂在 "backup:{accountId}/{container}" 这个来源上。写成裸 "backup" 的后果有两条，
    /// 都不会有人当场发现：DeleteForContainerAsync 按 ":{accountId}/{container}" 后缀清理，
    /// 于是这条 Warning 级（长存）记录在备份被删之后仍然赖在库里；QueryAsync 按来源精确相等过滤，
    /// 于是"这个备份都发生过什么"的日志视图里，换根这件大事根本看不见。
    ///
    /// 顺带钉住无基线时的措辞：一条都没抽样，就不能渲染成 "0/0 sampled entries matched"
    /// ——那读起来像"全都对不上"，恰恰是相反的意思。
    /// </summary>
    [Fact]
    public async Task Apply_Logs_An_Audit_Entry_Under_This_Backups_Source_Key()
    {
        Directory.CreateDirectory(_dir);
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(target);
        var id = await CreateConfigAsync(await CreateAzuriteAccountAsync(), _dir);
        var source = await SourceKeyOfAsync(id);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = false });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var entry = Assert.Single(LogsOf(source));
        Assert.Equal(OperationLogLevel.Warning, entry.Level);
        Assert.False(entry.Ephemeral);              // 审计：长存，保留至删除备份
        Assert.Contains(target, entry.Message);
        Assert.Contains(nameof(LocalRootVerdict.NoBaseline), entry.Message);
        Assert.DoesNotContain("sampled", entry.Message);
    }

    /// <summary>真抽过样的那条路径，样本计数照旧要写进日志；强制过的也要留痕。</summary>
    [Fact]
    public async Task Apply_Logs_The_Sample_Counts_When_A_Comparison_Actually_Ran()
    {
        var (id, target) = await SeedMismatchingBaselineAsync();
        var source = await SourceKeyOfAsync(id);

        var res = await _client.PostAsJsonAsync(
            $"/api/backup-configs/{id}/local-root", new { newRoot = target, force = true });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var entry = Assert.Single(LogsOf(source));
        Assert.Contains("0/1 sampled entries matched", entry.Message);
        Assert.Contains("forced", entry.Message);
    }

    [Fact]
    public async Task Unknown_Config_Is_A_404()
    {
        Directory.CreateDirectory(_dir);

        var res = await _client.PostAsJsonAsync(
            "/api/backup-configs/999999/local-root/preview", new { newRoot = _dir });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
