using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 备份密码重设端点（设计 §3.4）。验证依据是加密备份的信息文件本身——它就是用该密码加密的 7z，
/// 容器内最小的加密对象，解得开即证明密码正确。这里直接向真实 Azurite 写一个加密信息文件
/// （不经完整编排器，无需真实数据文件），再打 HTTP 端点验证「对密码落库、错密码不落库」。
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackupPasswordResetTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string AzuriteEndpoint = "http://127.0.0.1:10000/devstoreaccount1";

    private readonly HttpClient _client = factory.CreateClient();

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;
    private static string RandomName(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private static BackupInfoFile SampleInfo(bool encrypted = true) => new()
    {
        Backup = new BackupMeta
        {
            Name = "reset-pw-fixture",
            Encrypted = encrypted,
            CreatedAt = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero),
        },
        Versions =
        [
            new BackupVersion
            {
                Version = 1,
                CreatedAt = new DateTimeOffset(2026, 7, 16, 12, 5, 0, TimeSpan.Zero),
                IndexBlob = "indexes/v1.json.enc",
                Stats = new VersionStats(1, 10, 1, 10),
            },
        ],
    };

    /// <summary>直接用一个独立的 BackupInfoStore（不经宿主 HTTP）向真实 Azurite 写加密信息文件——
    /// 这就是端点要拿去验证密码的「金标准」制品。</summary>
    private static async Task SeedEncryptedInfoAsync(string container, string password)
    {
        var blobFactory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(blobFactory, new SevenZipArchiveCodec());
        var account = new Account
        {
            BlobEndpoint = AzuriteEndpoint,
            AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
            Region = AzureRegion.Global,
        };
        var cc = blobFactory.CreateServiceClient(account).GetBlobContainerClient(container);
        await cc.CreateIfNotExistsAsync();
        await store.WriteInfoAsync(account, container, SampleInfo(), password);
    }

    /// <summary>Finding 1 回归夹具：容器里放一份*未加密*的信息文件（用 password: null 写到未加密 blob 名），
    /// 模拟本地配置认为已加密、但云端实际是明文对象的错配场景。ReadInfoWithETagAsync 优先探测未加密 blob 名，
    /// 若不核对 Backup.Encrypted，端点会把提交的任意字符串当密码落库而根本没验证过。</summary>
    private static async Task SeedPlaintextInfoAsync(string container)
    {
        var blobFactory = new BlobClientFactory(TestSecrets.Reader);
        var store = new BackupInfoStore(blobFactory, new SevenZipArchiveCodec());
        var account = new Account
        {
            BlobEndpoint = AzuriteEndpoint,
            AccountKeyProtected = TestSecrets.Protect(AzuriteKey),
            Region = AzureRegion.Global,
        };
        var cc = blobFactory.CreateServiceClient(account).GetBlobContainerClient(container);
        await cc.CreateIfNotExistsAsync();
        await store.WriteInfoAsync(account, container, SampleInfo(encrypted: false), password: null);
    }

    private async Task<(AccountResponse Account, BackupConfigResponse Config)> CreateAccountAndConfigAsync(
        string container, string initialPassword)
    {
        var account = await (await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            "azurite", null, AzuriteEndpoint, AzureRegion.Global, AzuriteKey,
            false, ProxyMode.Independent, null, null, null, null)))
            .Content.ReadFromJsonAsync<AccountResponse>();
        Assert.NotNull(account);

        var config = await (await _client.PostAsJsonAsync("/api/backup-configs", new BackupConfigRequest(
            account!.Id, container, "reset-pw-fixture", null, "/some/local/root", initialPassword,
            StorageTier.Hot, StorageTier.Archive, null, null, null, false,
            100, 180, RetentionMode.EitherTriggers, 5_000_000, 100_000_000)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.NotNull(config);

        return (account, config!);
    }

    private sealed record KeyringSnapshot(IReadOnlyList<KeyringCanary> Canaries, KeyringStatus Status);

    private KeyringSnapshot SnapshotKeyring()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return new KeyringSnapshot(
            db.KeyringCanaries.AsNoTracking().ToList(),
            factory.Services.GetRequiredService<IKeyringHealth>().Status);
    }

    /// <summary>
    /// 用例自己的收尾。三个用例共用同一个宿主（<c>IClassFixture</c>），今天靠随机 container 名互不干扰，
    /// 所以只删容器也算正确；但留在夹具库里的 Account / BackupConfig 行，以及重设成功时
    /// <see cref="KeyringRecovery"/> 重写的哨兵与翻回的状态位，都是跨用例可见的残留——
    /// 夹具一旦改成跨测试类共享，邻居用例那套「逐条试解」的待重设计数就会被这些行悄悄改写，
    /// 且失败现场与真正的成因隔着一个测试类。故用完即还原。
    /// </summary>
    private async Task CleanUpAsync(string container, int accountId, int configId, KeyringSnapshot before)
    {
        var blobFactory = new BlobClientFactory(TestSecrets.Reader);
        var azurite = new Account
        { BlobEndpoint = AzuriteEndpoint, AccountKeyProtected = TestSecrets.Protect(AzuriteKey), Region = AzureRegion.Global };
        await blobFactory.CreateServiceClient(azurite).GetBlobContainerClient(container).DeleteIfExistsAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.BackupConfigs.Where(c => c.Id == configId).ExecuteDeleteAsync();
        await db.Accounts.Where(a => a.Id == accountId).ExecuteDeleteAsync();

        // 哨兵按内容还原（Id 交给库重新生成——判定只看「最小 Id 那一行的密文」，不看具体 Id）。
        await db.KeyringCanaries.ExecuteDeleteAsync();
        db.KeyringCanaries.AddRange(before.Canaries.Select(
            k => new KeyringCanary { Ciphertext = k.Ciphertext, CreatedAt = k.CreatedAt }));
        await db.SaveChangesAsync();
        factory.Services.GetRequiredService<IKeyringHealth>().Set(before.Status);
    }

    /// <summary>
    /// 对密码：验证通过、新密文落库，且验证路径没有回填任何本地权威状态
    /// （用的是纯读 ReadInfoWithETagAsync，不是会 seed 的 TrackedInfoStore.SeedFromCloudAsync）。
    /// </summary>
    [SkippableFact]
    public async Task Correct_Password_Verifies_And_Is_Persisted_Without_Seeding_Local_State()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var keyringBefore = SnapshotKeyring();
        var container = RandomName("rpw-ok-");
        const string correctPassword = "the-real-password";
        await SeedEncryptedInfoAsync(container, correctPassword);

        var (account, config) = await CreateAccountAndConfigAsync(container, initialPassword: "stale-initial-value");

        try
        {
            var reset = await _client.PostAsJsonAsync(
                $"/api/backup-configs/{config.Id}/reset-password", new ResetBackupPasswordRequest(correctPassword));
            Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

            var row = await db.BackupConfigs.AsNoTracking().FirstAsync(c => c.Id == config.Id);
            Assert.Equal(correctPassword, TestSecrets.Reveal(encryption, row.PasswordProtected!));

            // 验证是纯读：不应该在本地权威状态里留下任何痕迹。
            Assert.Empty(await db.LocalBackupStates
                .Where(s => s.AccountId == account.Id && s.Container == container).ToListAsync());
            Assert.Empty(await db.CachedVersionIndexes
                .Where(c => c.AccountId == account.Id && c.Container == container).ToListAsync());
        }
        finally
        {
            await CleanUpAsync(container, account.Id, config.Id, keyringBefore);
        }
    }

    /// <summary>
    /// 错密码：必须是「不落库」而不是「落库了错误值」。断言 400 且原密文原封不动，
    /// 且同样不留下本地权威状态的痕迹。
    /// </summary>
    [SkippableFact]
    public async Task Wrong_Password_Is_Rejected_And_Leaves_Stored_Ciphertext_And_Local_State_Untouched()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var keyringBefore = SnapshotKeyring();
        var container = RandomName("rpw-bad-");
        const string correctPassword = "the-real-password-2";
        await SeedEncryptedInfoAsync(container, correctPassword);

        var (account, config) = await CreateAccountAndConfigAsync(container, initialPassword: "original-value-untouched");

        try
        {
            var reset = await _client.PostAsJsonAsync(
                $"/api/backup-configs/{config.Id}/reset-password", new ResetBackupPasswordRequest("totally-wrong-password"));
            Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

            var row = await db.BackupConfigs.AsNoTracking().FirstAsync(c => c.Id == config.Id);
            Assert.Equal("original-value-untouched", TestSecrets.Reveal(encryption, row.PasswordProtected!));

            Assert.Empty(await db.LocalBackupStates
                .Where(s => s.AccountId == account.Id && s.Container == container).ToListAsync());
            Assert.Empty(await db.CachedVersionIndexes
                .Where(c => c.AccountId == account.Id && c.Container == container).ToListAsync());
        }
        finally
        {
            await CleanUpAsync(container, account.Id, config.Id, keyringBefore);
        }
    }

    /// <summary>
    /// 回归 Finding 1：本地配置认为已加密（PasswordProtected 非空），但云端容器里放的其实是
    /// *未加密*的信息文件。ReadInfoWithETagAsync 优先探测未加密 blob 名，会用 password: null
    /// 读回成功——如果端点不核对返回内容确实来自加密对象，就会把提交的任意字符串当密码落库，
    /// 而真密码从未被验证过。必须拒绝，且原密文原封不动。
    /// </summary>
    [SkippableFact]
    public async Task Plaintext_Info_Blob_In_Encrypted_Config_Container_Is_Rejected_Without_Using_Password()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var keyringBefore = SnapshotKeyring();
        var container = RandomName("rpw-plain-");
        await SeedPlaintextInfoAsync(container);

        var (account, config) = await CreateAccountAndConfigAsync(container, initialPassword: "original-value-untouched-3");

        try
        {
            var reset = await _client.PostAsJsonAsync(
                $"/api/backup-configs/{config.Id}/reset-password", new ResetBackupPasswordRequest("any-guessed-password"));
            Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

            var row = await db.BackupConfigs.AsNoTracking().FirstAsync(c => c.Id == config.Id);
            Assert.Equal("original-value-untouched-3", TestSecrets.Reveal(encryption, row.PasswordProtected!));

            Assert.Empty(await db.LocalBackupStates
                .Where(s => s.AccountId == account.Id && s.Container == container).ToListAsync());
            Assert.Empty(await db.CachedVersionIndexes
                .Where(c => c.AccountId == account.Id && c.Container == container).ToListAsync());
        }
        finally
        {
            await CleanUpAsync(container, account.Id, config.Id, keyringBefore);
        }
    }
}
