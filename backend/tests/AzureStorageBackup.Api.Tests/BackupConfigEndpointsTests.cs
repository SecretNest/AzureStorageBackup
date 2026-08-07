using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

public class BackupConfigEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly IServiceProvider _services = factory.Services;

    // AccountId 默认 0：不是一个真实账户，调用方必须显式传入 CreateAccountAsync 建出的账户 id
    // （否则从 P2T7 起会被「Account not found.」拦下）。仅当测试确实要断言这条拒绝时才保留 0/999999。
    private static BackupConfigRequest SampleRequest(string name = "photos", int accountId = 0) => new(
        AccountId: accountId,
        ContainerName: "photos",
        Name: name,
        Description: "family",
        LocalRoot: "/data/photos",
        Password: "s3cret",
        IndexTier: StorageTier.Hot,
        DataTier: StorageTier.Cool,
        IgnoreRules: "*.tmp",
        DontCompressRules: null,
        DontGroupRules: null,
        IncludeSymlinks: false,
        MaxVersions: 50,
        MaxAgeDays: 180,
        RetentionMode: RetentionMode.EitherTriggers,
        SingleFileThresholdBytes: 5_000_000,
        GroupCapBytes: 100_000_000);

    /// <summary>建一个真实账户，供需要通过「Account not found.」闸门的测试使用。</summary>
    private async Task<int> CreateAccountAsync(string name)
    {
        var req = new AccountRequest(
            Name: "acct-" + name + "-" + Guid.NewGuid().ToString("N")[..6],
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

    [Fact]
    public async Task Post_Creates_Config_And_Hides_Password()
    {
        var accountId = await CreateAccountAsync("post-creates");
        var res = await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest(accountId: accountId));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var created = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.True(created!.Id > 0);
        Assert.Equal("photos", created.Name);
        Assert.True(created.HasPassword); // 有加密密码，但不回传明文

        var body = await (await _client.GetAsync($"/api/backup-configs/{created.Id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("s3cret", body);
    }

    [Fact]
    public async Task Post_Without_LocalRoot_Returns_400()
    {
        var accountId = await CreateAccountAsync("no-local-root");
        var res = await _client.PostAsJsonAsync("/api/backup-configs",
            SampleRequest(accountId: accountId) with { LocalRoot = "" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Post_Without_Name_Returns_400()
    {
        var accountId = await CreateAccountAsync("no-name");
        var res = await _client.PostAsJsonAsync("/api/backup-configs",
            SampleRequest(accountId: accountId) with { Name = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("Name is required.", body!["error"]);
    }

    [Fact]
    public async Task Post_With_Nonexistent_Account_Returns_400()
    {
        var res = await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest(accountId: 999_999));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("Account not found.", body!["error"]);
    }

    [Fact]
    public async Task Put_With_Empty_Password_Keeps_Existing()
    {
        var accountId = await CreateAccountAsync("keep-pw");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("keep-pw", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var res = await _client.PutAsJsonAsync($"/api/backup-configs/{created!.Id}",
            SampleRequest("keep-pw", accountId) with { Password = null, Name = "renamed" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var updated = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal("renamed", updated!.Name);
        Assert.True(updated.HasPassword); // 密码保留
    }

    [Fact]
    public async Task Put_With_Blank_Name_Returns_400()
    {
        var accountId = await CreateAccountAsync("put-blank-name");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("put-blank-name", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var res = await _client.PutAsJsonAsync($"/api/backup-configs/{created!.Id}",
            SampleRequest("put-blank-name", accountId) with { Password = null, Name = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("Name is required.", body!["error"]);
    }

    [Fact]
    public async Task Delete_Removes_Config()
    {
        var accountId = await CreateAccountAsync("del");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("del", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/backup-configs/{created!.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/backup-configs/{created.Id}")).StatusCode);
    }

    /// <summary>删除留下的那条审计行，来源键必须带 accountId。它一度是改版前的旧格式
    /// "backup:{container}"：少了 account 维度，两个账户下的同名 container 写出的行完全一样，
    /// 而日志页按来源精确相等过滤，于是这条最该留痕的记录哪个备份的视图里都翻不到。
    /// 顺带钉住它写在清理**之后**——写在前面会被 DeleteForContainerAsync 连自己一起删掉。</summary>
    [Fact]
    public async Task Delete_Records_The_Audit_Line_Under_The_Backups_Own_Source_Key()
    {
        var accountId = await CreateAccountAsync("del-audit");
        var container = "delaudit" + Guid.NewGuid().ToString("N")[..8];
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("del-audit", accountId) with { ContainerName = container }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.DeleteAsync($"/api/backup-configs/{created!.Id}")).StatusCode);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = Assert.Single(db.LogEntries.Where(e => e.Source == $"backup:{accountId}/{container}"));
        Assert.Equal(OperationLogLevel.Warning, entry.Level);
        Assert.False(entry.Ephemeral);          // 审计：长存
        Assert.Contains("deleted", entry.Message);
    }

    /// <summary>删配置不会停掉后台那次运行。放行的话，它会继续跑、继续占着
    /// (account, container) 的忙碌锁，而进度状态是按 config id 存的——配置一删就再也查不到，
    /// 于是同一 container 上新建的备份被"busy"拒掉，状态却显示 BackingUp 且没有任何细节。
    /// 用户真踩到了这个（还顺带勾了「同时删除 container」，那次运行就一直在往一个已不存在的
    /// container 上传）。所以正在忙时必须直接拒掉删除。</summary>
    [Fact]
    public async Task Delete_Is_Refused_While_An_Operation_Is_Running()
    {
        var accountId = await CreateAccountAsync("del-busy");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("del-busy", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var busy = _services.GetRequiredService<BackupBusyTracker>();
        Assert.True(busy.TryAcquire(accountId, created!.ContainerName, "BackingUp"));
        try
        {
            var refused = await _client.DeleteAsync($"/api/backup-configs/{created.Id}");
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var body = await refused.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            Assert.Contains("backing up", body!["error"]);
            // 配置必须还在：拒绝要是"半拒绝"，比不拒还糟。
            Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/api/backup-configs/{created.Id}")).StatusCode);
        }
        finally
        {
            busy.Release(accountId, created.ContainerName);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/backup-configs/{created.Id}")).StatusCode);
    }

    // Azurite 的 well-known 账户与密钥（与其它集成测试一致，见 BackupRunEndpointsTests）。
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string AzuriteEndpoint = "http://127.0.0.1:10000/devstoreaccount1";

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Delete_Config_Optionally_Deletes_Cloud_Container()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var containerName = "del-" + Guid.NewGuid().ToString("N")[..8];

        var accountReq = new AccountRequest("azurite", null, AzuriteEndpoint, AzureRegion.Global,
            AzuriteKey, false, ProxyMode.Independent, null, null, null, null);
        var account = await (await _client.PostAsJsonAsync("/api/accounts", accountReq))
            .Content.ReadFromJsonAsync<AccountResponse>();

        var factoryClient = new BlobClientFactory(TestSecrets.Reader);
        var azuriteAccount = new Account { BlobEndpoint = AzuriteEndpoint, AccountKeyProtected = TestSecrets.Protect(AzuriteKey), Region = AzureRegion.Global };
        var cc = factoryClient.CreateServiceClient(azuriteAccount).GetBlobContainerClient(containerName);
        await cc.CreateIfNotExistsAsync();

        try
        {
            // deleteContainer=false（默认）：本地配置删除，云端 container 仍在。
            var config1 = await (await _client.PostAsJsonAsync("/api/backup-configs",
                    SampleRequest("del-keep") with { AccountId = account!.Id, ContainerName = containerName }))
                .Content.ReadFromJsonAsync<BackupConfigResponse>();

            Assert.Equal(HttpStatusCode.NoContent,
                (await _client.DeleteAsync($"/api/backup-configs/{config1!.Id}?deleteContainer=false")).StatusCode);
            Assert.True((await cc.ExistsAsync()).Value);

            // deleteContainer=true：另建一个指向同 container 的配置，删除时云端 container 一并被删。
            var config2 = await (await _client.PostAsJsonAsync("/api/backup-configs",
                    SampleRequest("del-purge") with { AccountId = account.Id, ContainerName = containerName }))
                .Content.ReadFromJsonAsync<BackupConfigResponse>();

            Assert.Equal(HttpStatusCode.NoContent,
                (await _client.DeleteAsync($"/api/backup-configs/{config2!.Id}?deleteContainer=true")).StatusCode);
            Assert.False((await cc.ExistsAsync()).Value);
        }
        finally
        {
            await cc.DeleteIfExistsAsync();
        }
    }

    /// <summary>P2T6 review follow-up：删配置连带清本地权威缓存/状态（CachedVersionIndex + LocalBackupState），
    /// 否则同 account+container 重建备份会命中孤儿行、与新备份的版本身份错配。按 (accountId, container) 精确
    /// 清除，不同 account 或不同 container 的行必须保留（不可越界误删）。</summary>
    [Fact]
    public async Task Delete_Config_Purges_Local_Index_Cache_And_Local_Backup_State_Scoped_To_Account_Container()
    {
        var acctId = await CreateAccountAsync("del-cache");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("del-cache", acctId) with { ContainerName = "del-cache-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        var accountId = created!.AccountId;
        var container = created.ContainerName;
        var otherContainer = "del-cache-other-container";
        var otherAccountId = accountId + 999;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CachedVersionIndexes.Add(new CachedVersionIndex { AccountId = accountId, Container = container, Version = 1, IdentityTicks = 1, Bytes = [1] });
            db.LocalBackupStates.Add(new LocalBackupState { AccountId = accountId, Container = container, InfoBytes = [1], ETag = "e1" });
            // 同 account，不同 container → 必须保留
            db.CachedVersionIndexes.Add(new CachedVersionIndex { AccountId = accountId, Container = otherContainer, Version = 1, IdentityTicks = 1, Bytes = [1] });
            db.LocalBackupStates.Add(new LocalBackupState { AccountId = accountId, Container = otherContainer, InfoBytes = [1], ETag = "e2" });
            // 不同 account，同名 container → 必须保留
            db.CachedVersionIndexes.Add(new CachedVersionIndex { AccountId = otherAccountId, Container = container, Version = 1, IdentityTicks = 1, Bytes = [1] });
            db.LocalBackupStates.Add(new LocalBackupState { AccountId = otherAccountId, Container = container, InfoBytes = [1], ETag = "e3" });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/backup-configs/{created.Id}")).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.CachedVersionIndexes.AnyAsync(x => x.AccountId == accountId && x.Container == container));
            Assert.False(await db.LocalBackupStates.AnyAsync(x => x.AccountId == accountId && x.Container == container));
            Assert.True(await db.CachedVersionIndexes.AnyAsync(x => x.AccountId == accountId && x.Container == otherContainer));
            Assert.True(await db.LocalBackupStates.AnyAsync(x => x.AccountId == accountId && x.Container == otherContainer));
            Assert.True(await db.CachedVersionIndexes.AnyAsync(x => x.AccountId == otherAccountId && x.Container == container));
            Assert.True(await db.LocalBackupStates.AnyAsync(x => x.AccountId == otherAccountId && x.Container == container));
        }
    }

    [Fact]
    public async Task New_Config_Reports_Normal_Status_And_Idle_Activity()
    {
        var accountId = await CreateAccountAsync("status-idle");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("status-idle", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        Assert.Equal(BackupStatus.Normal, created!.Status);
        Assert.Null(created.LastError);
        Assert.Equal("Idle", created.Activity);

        var fetched = await (await _client.GetAsync($"/api/backup-configs/{created.Id}"))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal("Idle", fetched!.Activity);
    }

    [Fact]
    public async Task Busy_Config_Reports_Checking_Activity_In_List_And_Detail()
    {
        var accountId = await CreateAccountAsync("status-busy");
        var req = SampleRequest("status-busy", accountId) with { ContainerName = "busy-container" };
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", req))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        // 模拟检查持有的忙碌锁，且 CheckRunner 里没有对应记录（例如检查由计划任务发起）：
        // DeriveActivity 必须靠 BackupBusyTracker 兜底判出 Checking。
        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();
        Assert.True(busy.TryAcquire(created!.AccountId, created.ContainerName));
        try
        {
            var list = await (await _client.GetAsync("/api/backup-configs"))
                .Content.ReadFromJsonAsync<List<BackupConfigResponse>>();
            Assert.Equal("Checking", list!.Single(c => c.Id == created.Id).Activity);

            var single = await (await _client.GetAsync($"/api/backup-configs/{created.Id}"))
                .Content.ReadFromJsonAsync<BackupConfigResponse>();
            Assert.Equal("Checking", single!.Activity);
        }
        finally
        {
            busy.Release(created.AccountId, created.ContainerName);
        }

        var afterRelease = await (await _client.GetAsync($"/api/backup-configs/{created.Id}"))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal("Idle", afterRelease!.Activity);
    }

    // 前端 api/backupConfigs.ts 里的 BackupActivity 联合类型（'Idle' | 'BackingUp' | 'Restoring' |
    // 'Checking' | 'Repairing' | 'CleaningUp'）镜像的正是这六个字符串，但那边没有对应的测试。
    // 谁在后端改动了其中一个字面量，两边各自都还能编译通过——前端只会悄悄地不再轮询那一类。
    // 这里逐一逼出 DeriveActivity 的六条分支（BackingUp/Checking/CleaningUp/Repairing 走
    // BackupBusyTracker 的兜底渠道，与 TaskDispatcher.cs、BackupRunner.cs、RepairRunner.cs
    // 里真实使用的字面量同源；Restoring 是唯一不占忙碌锁的一个——见 RestoreRunner.cs 顶部
    // 注释，只能靠反射注入它自己的运行态来触发），断在字面量上，改名会在这里响亮地炸掉(Fix 8)。
    [Fact]
    public async Task Activity_Strings_Match_The_Frontend_BackupActivity_Union()
    {
        var accountId = await CreateAccountAsync("activity-strings");
        var req = SampleRequest("activity-strings", accountId) with { ContainerName = "activity-strings-container" };
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", req))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        async Task<string> ActivityAsync() =>
            (await (await _client.GetAsync($"/api/backup-configs/{created!.Id}")).Content.ReadFromJsonAsync<BackupConfigResponse>())!.Activity;

        Assert.Equal("Idle", await ActivityAsync());

        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();

        // BackingUp/Checking/CleaningUp/Repairing：DeriveActivity 在没有对应 Runner 记录时
        // 兜底读 BackupBusyTracker.CurrentActivity，这几个字面量正是 TaskDispatcher.cs 的
        // switch 和 BackupRunner.cs / RepairRunner.cs 里 TryAcquire 调用真实传入的那几个。
        foreach (var label in new[] { "BackingUp", "Checking", "CleaningUp", "Repairing" })
        {
            Assert.True(busy.TryAcquire(created!.AccountId, created.ContainerName, label));
            try
            {
                Assert.Equal(label, await ActivityAsync());
            }
            finally
            {
                busy.Release(created.AccountId, created.ContainerName);
            }
        }

        Assert.Equal("Idle", await ActivityAsync());

        // Restoring 不占忙碌锁（RestoreRunner.cs 顶部注释：还原可与备份并行），DeriveActivity
        // 只看 RestoreRunner 自己的运行态——直接反射进它的私有字典模拟一次"正在跑"的还原，
        // 避免为了触发这一个分支去真的跑一次还原、依赖 Azure/Azurite 的时序。
        var restoreRunner = factory.Services.GetRequiredService<RestoreRunner>();
        var runsField = typeof(RestoreRunner).GetField("_runs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var runs = (Dictionary<int, RestoreRunState>)runsField.GetValue(restoreRunner)!;
        runs[created!.Id] = new RestoreRunState { Status = RunStatus.Running };
        try
        {
            Assert.Equal("Restoring", await ActivityAsync());
        }
        finally
        {
            runs.Remove(created.Id);
        }

        Assert.Equal("Idle", await ActivityAsync());
    }

    [Fact]
    public async Task Failed_Operation_Sets_Error_And_Reset_Status_Clears_It()
    {
        var accountId = await CreateAccountAsync("status-error");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("status-error", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        // 模拟某个 runner 写状态点因操作失败落库 Error（决策 2）。
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IBackupConfigService>()
                .SetErrorAsync(created!.Id, "simulated failure");

        var afterFailure = await (await _client.GetAsync($"/api/backup-configs/{created!.Id}"))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal(BackupStatus.Error, afterFailure!.Status);
        Assert.Equal("simulated failure", afterFailure.LastError);
        Assert.NotNull(afterFailure.LastErrorAt);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.PostAsync($"/api/backup-configs/{created.Id}/reset-status", null)).StatusCode);

        var afterReset = await (await _client.GetAsync($"/api/backup-configs/{created.Id}"))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal(BackupStatus.Normal, afterReset!.Status);
        Assert.Null(afterReset.LastError);
    }

    [Fact]
    public async Task Reset_Status_On_Missing_Config_Returns_404()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PostAsync("/api/backup-configs/999999/reset-status", null)).StatusCode);
    }

    // ---- §5.8: /check, /repair, /versions, /file-versions, /unrecoverable, /tree, /restore-estimate ----

    private async Task<AccountResponse> CreateAzuriteAccountAsync()
    {
        var req = new AccountRequest("azurite-" + Guid.NewGuid().ToString("N")[..6], null, AzuriteEndpoint,
            AzureRegion.Global, AzuriteKey, false, ProxyMode.Independent, null, null, null, null);
        return (await (await _client.PostAsJsonAsync("/api/accounts", req)).Content.ReadFromJsonAsync<AccountResponse>())!;
    }

    /// <summary>直接写本地权威信息文件（TrackedInfoStore.LoadAsync 命中本地则不读云端），供 /versions、/tree、
    /// /file-versions、/unrecoverable 端点做无需 Azurite 的本地态测试。返回 identityTicks（=Backup.CreatedAt.UtcTicks），
    /// 供 /tree 端点匹配 CachedVersionIndex.IdentityTicks。</summary>
    private long SeedLocalInfo(int accountId, string container, List<BackupVersion> versions)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var info = new BackupInfoFile
        {
            Backup = new BackupMeta { Name = "seed", CreatedAt = createdAt, Encrypted = false },
            Versions = versions,
        };
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.LocalBackupStates.Add(new LocalBackupState
        {
            AccountId = accountId, Container = container,
            InfoBytes = IndexSerializer.SerializeInfoFile(info), ETag = "seed-etag",
        });
        db.SaveChanges();
        return createdAt.UtcTicks;
    }

    private sealed record VersionSummary(int version, DateTimeOffset createdAt, long files, long bytes, long changedFiles);
    private sealed record VersionSpanRow(int version, DateTimeOffset createdAt, DateTimeOffset? startedAt);
    private sealed record FileVersionCandidate(int version, DateTimeOffset createdAt, long length);
    private sealed record RestoreEstimateResult(long downloadBytes, long uncompressedBytes, int fileCount, int archivedObjects, int rehydratePending);

    [Fact]
    public async Task Versions_Endpoint_Returns_Seeded_Version_Stats()
    {
        var account = await CreateAzuriteAccountAsync();
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("ep-versions") with { AccountId = account.Id, ContainerName = "ep-versions-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        SeedLocalInfo(account.Id, created!.ContainerName,
        [
            new BackupVersion
            {
                Version = 1, CreatedAt = DateTimeOffset.UtcNow, IndexBlob = "v1.index",
                Stats = new VersionStats(Files: 2, Bytes: 100, ChangedFiles: 2, ChangedBytes: 100),
            },
        ]);

        var res = await _client.GetAsync($"/api/backup-configs/{created.Id}/versions");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var rows = await res.Content.ReadFromJsonAsync<List<VersionSummary>>();
        var row = Assert.Single(rows!);
        Assert.Equal(1, row.version);
        Assert.Equal(2, row.files);
        Assert.Equal(100, row.bytes);
    }

    /// <summary>还原对话框靠这两个时刻认版本。升级前写下的版本没有开始时刻，端点如实给 null，
    /// 界面写「—」——不拿上一版本的结束时刻冒充。</summary>
    [Fact]
    public async Task Versions_Endpoint_Exposes_Start_Time_And_Null_For_Legacy_Versions()
    {
        var account = await CreateAzuriteAccountAsync();
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("ep-vspan") with { AccountId = account.Id, ContainerName = "ep-vspan-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var started = new DateTimeOffset(2026, 8, 2, 14, 3, 0, TimeSpan.Zero);
        var finished = new DateTimeOffset(2026, 8, 2, 14, 47, 0, TimeSpan.Zero);
        SeedLocalInfo(account.Id, created!.ContainerName,
        [
            new BackupVersion
            {
                Version = 1, CreatedAt = finished.AddDays(-1), IndexBlob = "v1.index",  // 升级前写下的：无 StartedAt
                Stats = new VersionStats(1, 10, 1, 10),
            },
            new BackupVersion
            {
                Version = 2, CreatedAt = finished, StartedAt = started, IndexBlob = "v2.index",
                Stats = new VersionStats(2, 20, 1, 10),
            },
        ]);

        var rows = await _client.GetFromJsonAsync<List<VersionSpanRow>>(
            $"/api/backup-configs/{created.Id}/versions");

        Assert.NotNull(rows);
        Assert.Null(rows.Single(r => r.version == 1).startedAt);
        Assert.Equal(started, rows.Single(r => r.version == 2).startedAt);
        Assert.Equal(finished, rows.Single(r => r.version == 2).createdAt);
    }

    /// <summary>还原/检查的版本下拉里，"Latest" 之后必须是从新到旧——最可能被选的就在最近处。
    /// 端点按版本号降序给，与 /file-versions 的"就近排序"一致。</summary>
    [Fact]
    public async Task Versions_Endpoint_Returns_Newest_First()
    {
        var account = await CreateAzuriteAccountAsync();
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("ep-vorder") with { AccountId = account.Id, ContainerName = "ep-vorder-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        SeedLocalInfo(account.Id, created!.ContainerName,
        [
            new BackupVersion { Version = 1, CreatedAt = DateTimeOffset.UtcNow.AddDays(-3), IndexBlob = "v1.index", Stats = new VersionStats(1, 10, 1, 10) },
            new BackupVersion { Version = 2, CreatedAt = DateTimeOffset.UtcNow.AddDays(-2), IndexBlob = "v2.index", Stats = new VersionStats(2, 20, 1, 10) },
            new BackupVersion { Version = 3, CreatedAt = DateTimeOffset.UtcNow.AddDays(-1), IndexBlob = "v3.index", Stats = new VersionStats(3, 30, 1, 10) },
        ]);

        var rows = await _client.GetFromJsonAsync<List<VersionSummary>>(
            $"/api/backup-configs/{created.Id}/versions");

        Assert.Equal([3, 2, 1], rows!.Select(r => r.version));
    }

    [Fact]
    public async Task Tree_Endpoint_Returns_Root_Children()
    {
        var account = await CreateAzuriteAccountAsync();
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("ep-tree") with { AccountId = account.Id, ContainerName = "ep-tree-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var identityTicks = SeedLocalInfo(account.Id, created!.ContainerName,
        [
            new BackupVersion
            {
                Version = 1, CreatedAt = DateTimeOffset.UtcNow, IndexBlob = "v1.index",
                Stats = new VersionStats(1, 5, 1, 5),
            },
        ]);

        var index = new VersionIndex
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
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CachedVersionIndexes.Add(new CachedVersionIndex
            {
                AccountId = account.Id, Container = created.ContainerName, Version = 1,
                IdentityTicks = identityTicks, Bytes = IndexSerializer.SerializeIndex(index),
            });
            await db.SaveChangesAsync();
        }

        var res = await _client.GetAsync($"/api/backup-configs/{created.Id}/tree?version=1");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var nodes = await res.Content.ReadFromJsonAsync<List<TreeNode>>();
        var node = Assert.Single(nodes!);
        Assert.Equal("a.txt", node.Name);
        Assert.False(node.IsDir);
        Assert.Equal(5, node.Length);

        // 指定不存在的版本 → 200 空数组（与 /unrecoverable、/file-versions 一致，非 404）。
        var missingVer = await _client.GetAsync($"/api/backup-configs/{created.Id}/tree?version=999");
        Assert.Equal(HttpStatusCode.OK, missingVer.StatusCode);
        Assert.Empty((await missingVer.Content.ReadFromJsonAsync<List<TreeNode>>())!);
    }

    /// <summary>
    /// 读版本索引的端点必须走**本地权威缓存**，不能在运行期读云端（设计核心原则：本地索引权威、
    /// 运行期零云读）。/tree 一直是对的，而 /unrecoverable、/file-versions、/unreadable 三个
    /// 直接调 IBackupInfoStore.ReadIndexAsync ——还原对话框一打开就至少两次云端索引下载，
    /// /file-versions 更是**每个版本各一次**：延迟之外还是实打实的 Azure 出站流量费，
    /// 而本地就躺着权威副本。
    /// <para>
    /// 判据很干净：本测试的 container 在 Azurite 上**根本不存在**，本地缓存里却有完整索引。
    /// 走本地则三个端点都能返回正确内容；走云端则必然失败。修复前 /unrecoverable 与 /unreadable
    /// 返回 500，/file-versions 返回空。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Index_Reading_Endpoints_Use_The_Local_Cache_Not_The_Cloud()
    {
        var account = await CreateAzuriteAccountAsync();
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("ep-nocloud") with { AccountId = account.Id, ContainerName = "ep-nocloud-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var identityTicks = SeedLocalInfo(account.Id, created!.ContainerName,
        [
            new BackupVersion
            {
                Version = 1, CreatedAt = DateTimeOffset.UtcNow, IndexBlob = "v1.index",
                Stats = new VersionStats(2, 10, 2, 10),
            },
        ]);

        var stale = new DateTimeOffset(2026, 7, 20, 8, 30, 0, TimeSpan.Zero);
        var index = new VersionIndex
        {
            Version = 1,
            Entries =
            [
                new IndexEntry
                {
                    Path = "carried.txt", Kind = "file", Length = 5, Mtime = DateTimeOffset.UtcNow,
                    Permissions = "644", UnreadableAt = stale,
                    Storage = new StorageRef { Kind = "blob", Ref = "data/abc" },
                },
                new IndexEntry
                {
                    Path = "broken.txt", Kind = "file", Length = 5, Mtime = DateTimeOffset.UtcNow,
                    Permissions = "644", Storage = new StorageRef { Kind = "blob", Ref = "data/def" },
                },
            ],
            UnrecoverablePaths = ["broken.txt"],
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CachedVersionIndexes.Add(new CachedVersionIndex
            {
                AccountId = account.Id, Container = created.ContainerName, Version = 1,
                IdentityTicks = identityTicks, Bytes = IndexSerializer.SerializeIndex(index),
            });
            await db.SaveChangesAsync();
        }

        // /unreadable —— 云端没有这个 container，能返回内容就证明读的是本地缓存。
        var unread = await _client.GetAsync($"/api/backup-configs/{created.Id}/unreadable?version=1");
        Assert.Equal(HttpStatusCode.OK, unread.StatusCode);
        var unreadRow = Assert.Single((await unread.Content.ReadFromJsonAsync<List<UnreadableRow>>())!);
        Assert.Equal("carried.txt", unreadRow.path);
        Assert.Equal(stale, unreadRow.unreadableAt);

        // /unrecoverable
        var unrec = await _client.GetAsync($"/api/backup-configs/{created.Id}/unrecoverable?version=1");
        Assert.Equal(HttpStatusCode.OK, unrec.StatusCode);
        Assert.Equal(["broken.txt"], (await unrec.Content.ReadFromJsonAsync<List<string>>())!);

        // /file-versions —— 循环里每个版本各读一次索引，最该走本地。
        var fv = await _client.GetAsync(
            $"/api/backup-configs/{created.Id}/file-versions?path={Uri.EscapeDataString("carried.txt")}");
        Assert.Equal(HttpStatusCode.OK, fv.StatusCode);
        var candidate = Assert.Single((await fv.Content.ReadFromJsonAsync<List<FileVersionCandidate>>())!);
        Assert.Equal(1, candidate.version);

        // 标记为不可恢复的路径不得出现在替代候选里（本地读没有把这条既有语义读丢）。
        var fvBroken = await _client.GetAsync(
            $"/api/backup-configs/{created.Id}/file-versions?path={Uri.EscapeDataString("broken.txt")}");
        Assert.Empty((await fvBroken.Content.ReadFromJsonAsync<List<FileVersionCandidate>>())!);
    }

    [Fact]
    public async Task File_Versions_And_Unrecoverable_Return_Empty_Array_When_No_Versions_Exist()
    {
        var account = await CreateAzuriteAccountAsync();
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("ep-fv-empty") with { AccountId = account.Id, ContainerName = "ep-fv-empty-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        SeedLocalInfo(account.Id, created!.ContainerName, []); // 无版本 → 两端点均短路，不触碰云端

        var fv = await _client.GetFromJsonAsync<List<FileVersionCandidate>>(
            $"/api/backup-configs/{created.Id}/file-versions?path=a.txt");
        Assert.Empty(fv!);

        var unrec = await _client.GetFromJsonAsync<List<string>>($"/api/backup-configs/{created.Id}/unrecoverable");
        Assert.Empty(unrec!);

        // /unreadable 与 /unrecoverable 同构：无版本时同样短路成 200 空数组，不触碰云端。
        var unread = await _client.GetFromJsonAsync<List<UnreadableRow>>($"/api/backup-configs/{created.Id}/unreadable");
        Assert.Empty(unread!);
    }

    private sealed record UnreadableRow(string path, DateTimeOffset unreadableAt);

    /// <summary>检查改成后台 job 之后，忙碌不再是同步的 409：POST 只负责把运行起起来，
    /// 冲突要在**运行态**里体现（与 /repair 同一约定）。</summary>
    [Fact]
    public async Task Check_Endpoint_Reports_Busy_Through_The_Run_State()
    {
        var account = await CreateAzuriteAccountAsync();
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("ep-check-busy") with { AccountId = account.Id, ContainerName = "ep-check-busy-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var busy = factory.Services.GetRequiredService<BackupBusyTracker>();
        Assert.True(busy.TryAcquire(created!.AccountId, created.ContainerName));
        try
        {
            var res = await _client.PostAsync($"/api/backup-configs/{created.Id}/check", null);
            Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

            CheckRunResponse? run = null;
            for (var i = 0; i < 200; i++)
            {
                run = await (await _client.GetAsync($"/api/backup-configs/{created.Id}/check"))
                    .Content.ReadFromJsonAsync<CheckRunResponse>();
                if (run!.Status != "Running") break;
                await Task.Delay(25);
            }
            Assert.Equal("Failed", run!.Status);
            Assert.Contains("busy", run.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.Null(run.Report);
        }
        finally
        {
            busy.Release(created.AccountId, created.ContainerName);
        }
    }

    /// <summary>没跑过检查要答 204 而不是 404：对话框一打开就问一次，404 会在浏览器控制台
    /// 留下一条红色报错，看着像故障。</summary>
    [Fact]
    public async Task Check_Status_Endpoint_Is_204_Until_A_Check_Has_Been_Started()
    {
        var accountId = await CreateAccountAsync("check-never-run");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("check-never-run", accountId) with { ContainerName = "check-never-run-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.GetAsync($"/api/backup-configs/{created!.Id}/check")).StatusCode);
    }

    /// <summary>停止：在此之前，一次跑了几小时的备份唯一的停法是重启容器。逐操作停而不是
    /// 一键停光——备份与还原可以并发，误停另一条同样是几小时的损失。</summary>
    [Fact]
    public async Task Cancel_Endpoint_Signals_Only_The_Requested_Operation()
    {
        var accountId = await CreateAccountAsync("cancel-dispatch");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("cancel-dispatch", accountId) with { ContainerName = "cancel-dispatch-container" }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        // 没有任何运行 → 409：与「停了什么都没停到」区分开，界面才好如实说明。
        var idle = await _client.PostAsync($"/api/backup-configs/{created!.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, idle.StatusCode);

        // 直接往两个 runner 的私有字典里塞一条 Running 记录（同 Activity_Strings 测试的手法）：
        // 为了触发取消分发而去真跑一次备份/还原，会把这个测试绑死在 Azurite 的时序上。
        var backupRunner = factory.Services.GetRequiredService<BackupRunner>();
        var restoreRunner = factory.Services.GetRequiredService<RestoreRunner>();
        var backupState = new BackupRunState { Status = RunStatus.Running };
        var restoreState = new RestoreRunState { Status = RunStatus.Running };
        InjectRun(backupRunner, created.Id, backupState);
        InjectRun(restoreRunner, created.Id, restoreState);
        try
        {
            var res = await _client.PostAsync($"/api/backup-configs/{created.Id}/cancel?what=backup", null);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<CanceledBody>();
            Assert.Equal(["backup"], body!.canceled);

            Assert.True(backupState.Cancellation.IsCancellationRequested);
            // 并发的还原不能被顺手一起停掉。
            Assert.False(restoreState.Cancellation.IsCancellationRequested);

            // 不带 what → 停掉该配置上所有在跑的操作。
            var all = await _client.PostAsync($"/api/backup-configs/{created.Id}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, all.StatusCode);
            Assert.True(restoreState.Cancellation.IsCancellationRequested);
        }
        finally
        {
            RemoveRun(backupRunner, created.Id);
            RemoveRun(restoreRunner, created.Id);
        }
    }

    [Fact]
    public async Task Cancel_On_Missing_Config_Returns_404()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PostAsync("/api/backup-configs/999999/cancel", null)).StatusCode);
    }

    private sealed record CanceledBody(List<string> canceled);

    private static Dictionary<int, TState> RunsOf<TRunner, TState>(TRunner runner) where TRunner : notnull =>
        (Dictionary<int, TState>)typeof(TRunner)
            .GetField("_runs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(runner)!;

    private static void InjectRun<TRunner, TState>(TRunner runner, int configId, TState state) where TRunner : notnull =>
        RunsOf<TRunner, TState>(runner)[configId] = state;

    private static void RemoveRun<TRunner>(TRunner runner, int configId) where TRunner : notnull =>
        ((System.Collections.IDictionary)typeof(TRunner)
            .GetField("_runs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(runner)!).Remove(configId);

    [Fact]
    public async Task Read_And_Action_Endpoints_On_Missing_Config_Return_404()
    {
        const int missingId = 999999;
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/backup-configs/{missingId}/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/backup-configs/{missingId}/file-versions?path=a.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/backup-configs/{missingId}/unrecoverable")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/backup-configs/{missingId}/unreadable")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/backup-configs/{missingId}/tree")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/backup-configs/{missingId}/check", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/backup-configs/{missingId}/repair", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PostAsJsonAsync($"/api/backup-configs/{missingId}/restore-estimate",
                new RestoreEstimateRequestBody(null, []))).StatusCode);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Check_Repair_RestoreEstimate_FileVersions_Unrecoverable_Endpoints_Work_After_Backup()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var localRoot = Path.Combine(Path.GetTempPath(), "asb-ep-cre-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localRoot);
        await File.WriteAllTextAsync(Path.Combine(localRoot, "a.txt"), "alpha");

        var containerName = "ep-cre-" + Guid.NewGuid().ToString("N")[..8];
        var account = await CreateAzuriteAccountAsync();
        var config = await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleRequest("ep-cre") with { AccountId = account.Id, ContainerName = containerName, LocalRoot = localRoot }))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var factoryClient = new BlobClientFactory(TestSecrets.Reader);
        var azurite = new Account { BlobEndpoint = AzuriteEndpoint, AccountKeyProtected = TestSecrets.Protect(AzuriteKey), Region = AzureRegion.Global };
        var container = factoryClient.CreateServiceClient(azurite).GetBlobContainerClient(containerName);

        try
        {
            await _client.PostAsync($"/api/backup-configs/{config!.Id}/run", null);
            BackupRunResponse? backup = null;
            for (var i = 0; i < 600; i++)
            {
                backup = await (await _client.GetAsync($"/api/backup-configs/{config.Id}/run")).Content.ReadFromJsonAsync<BackupRunResponse>();
                if (backup!.Status != "Running") break;
                await Task.Delay(200);
            }
            Assert.Equal("Completed", backup!.Status);

            // /check：健康备份 → ok=true，单文件在案。检查是后台 job（202 + 轮询）：
            // 内容级检查要把整个备份下载重算一遍 hash，同步端点会先被浏览器/反代超时掐断。
            var checkStart = await _client.PostAsync($"/api/backup-configs/{config.Id}/check", null);
            Assert.Equal(HttpStatusCode.Accepted, checkStart.StatusCode);
            CheckRunResponse? check = null;
            for (var i = 0; i < 600; i++)
            {
                check = await (await _client.GetAsync($"/api/backup-configs/{config.Id}/check"))
                    .Content.ReadFromJsonAsync<CheckRunResponse>();
                if (check!.Status != "Running") break;
                await Task.Delay(200);
            }
            Assert.Equal("Completed", check!.Status);
            var checkReport = check.Report;
            Assert.True(checkReport!.Ok);
            Assert.Single(checkReport.Findings);

            // /repair：无需修复 → Completed
            var repairStart = await _client.PostAsync($"/api/backup-configs/{config.Id}/repair", null);
            Assert.Equal(HttpStatusCode.Accepted, repairStart.StatusCode);
            RepairRunResponse? repair = null;
            for (var i = 0; i < 600; i++)
            {
                repair = await (await _client.GetAsync($"/api/backup-configs/{config.Id}/repair")).Content.ReadFromJsonAsync<RepairRunResponse>();
                if (repair!.Status != "Running") break;
                await Task.Delay(200);
            }
            Assert.Equal("Completed", repair!.Status);

            // /restore-estimate：单文件下载量估算
            var estimateRes = await _client.PostAsJsonAsync($"/api/backup-configs/{config.Id}/restore-estimate",
                new RestoreEstimateRequestBody(null, ["a.txt"]));
            Assert.Equal(HttpStatusCode.OK, estimateRes.StatusCode);
            var estimate = await estimateRes.Content.ReadFromJsonAsync<RestoreEstimateResult>();
            Assert.Equal(1, estimate!.fileCount);
            Assert.True(estimate.downloadBytes > 0);

            // /file-versions + /unrecoverable：健康备份下 candidate 存在、无不可恢复项
            var fv = await _client.GetFromJsonAsync<List<FileVersionCandidate>>($"/api/backup-configs/{config.Id}/file-versions?path=a.txt");
            var candidate = Assert.Single(fv!);
            Assert.Equal(1, candidate.version);

            var unrec = await _client.GetFromJsonAsync<List<string>>($"/api/backup-configs/{config.Id}/unrecoverable");
            Assert.Empty(unrec!);
        }
        finally
        {
            try { Directory.Delete(localRoot, recursive: true); } catch { /* best effort */ }
            await container.DeleteIfExistsAsync();
        }
    }

    [Fact]
    public async Task Delete_config_discards_its_journals()
    {
        var accountId = await CreateAccountAsync("journal-sweep");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("j", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var journals = _services.GetRequiredService<BackupJournalStore>();
        await using (var j = await journals.CreateAsync(accountId, "photos", "leftover", new JournalHeader
        {
            RunId = "leftover", ConfigId = created!.Id, StartedAt = DateTimeOffset.UtcNow,
            BaselineVersion = 0, LocalRoot = "/data/photos", EncryptionIdentity = "plain",
        }, default))
            await j.AppendAsync(
                new JournalRecord { Kind = "blob", Ref = "data/aaa", Path = "a.bin", FullHash = "aaa" }, default);
        Assert.Single(await journals.ListAsync(accountId, "photos", default));

        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.DeleteAsync($"/api/backup-configs/{created.Id}")).StatusCode);

        // 配置没了就再没人会来采纳这卷 journal；留着它只会永远保住那批块不被清理。
        Assert.Empty(await journals.ListAsync(accountId, "photos", default));
    }

    [Fact]
    public async Task Suspend_without_a_running_backup_is_a_conflict()
    {
        var accountId = await CreateAccountAsync("suspend-idle");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("s", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var res = await _client.PostAsync($"/api/backup-configs/{created!.Id}/suspend", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Retry_now_without_a_paused_backup_is_a_conflict()
    {
        var accountId = await CreateAccountAsync("retry-idle");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("r", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var res = await _client.PostAsync($"/api/backup-configs/{created!.Id}/retry-now", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Interrupted_runs_are_listed_with_their_block_count()
    {
        var accountId = await CreateAccountAsync("interrupted-list");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("i", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var journals = _services.GetRequiredService<BackupJournalStore>();
        await using (var j = await journals.CreateAsync(accountId, "photos", "run-1", new JournalHeader
        {
            RunId = "run-1", ConfigId = created!.Id, StartedAt = DateTimeOffset.UnixEpoch,
            BaselineVersion = 0, LocalRoot = "/data/photos", EncryptionIdentity = "plain",
        }, default))
        {
            await j.AppendAsync(
                new JournalRecord { Kind = "blob", Ref = "data/aaa", Path = "a.bin", FullHash = "aaa" }, default);
            await j.AppendAsync(
                new JournalRecord { Kind = "blob", Ref = "data/bbb", Path = "b.bin", FullHash = "bbb" }, default);
        }

        var listed = await _client.GetFromJsonAsync<List<InterruptedRunResponse>>(
            $"/api/backup-configs/{created.Id}/interrupted");

        Assert.Single(listed!);
        Assert.Equal("run-1", listed![0].RunId);
        Assert.Equal(2, listed[0].Blocks);          // 头一行不算进去
        Assert.True(listed[0].JournalBytes > 0);
        Assert.True(listed[0].Resumable);
    }

    [Fact]
    public async Task Interrupted_run_from_another_local_root_is_listed_but_not_resumable()
    {
        var accountId = await CreateAccountAsync("interrupted-moved");
        var created = await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest("m", accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var journals = _services.GetRequiredService<BackupJournalStore>();
        await using (var j = await journals.CreateAsync(accountId, "photos", "run-2", new JournalHeader
        {
            RunId = "run-2", ConfigId = created!.Id, StartedAt = DateTimeOffset.UnixEpoch,
            BaselineVersion = 0, LocalRoot = "/somewhere/else", EncryptionIdentity = "plain",
        }, default)) { }

        var listed = await _client.GetFromJsonAsync<List<InterruptedRunResponse>>(
            $"/api/backup-configs/{created.Id}/interrupted");
        Assert.False(listed![0].Resumable);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.DeleteAsync($"/api/backup-configs/{created.Id}/interrupted")).StatusCode);
        Assert.Empty(await _client.GetFromJsonAsync<List<InterruptedRunResponse>>(
            $"/api/backup-configs/{created.Id}/interrupted") ?? []);
    }
}
