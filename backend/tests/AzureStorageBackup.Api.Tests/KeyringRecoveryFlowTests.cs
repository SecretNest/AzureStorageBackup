using System.Net.Http.Json;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 全分支复审 Finding 1：恢复流程的**中间态**。整套用例集此前只覆盖两端
/// （全丢 / 全恢复），恰好跨过了唯一会死锁的那一步。
///
/// 规范安装（≥1 账户 + ≥1 加密备份配置）下 /keys 丢失后：账户全部重设成功、备份密码仍是旧密文。
/// 此时全局状态必须仍是 Lost（备份密码没修好），而 accountsPending 必须已归零、
/// 账户行不再标记 secretsUnavailable——否则前端的顺序依赖（账户未清零 → 禁用备份密码
/// 「Re-enter」按钮）会把恢复流程彻底锁死：按钮永不可用 → 密码永不能重设 → 状态永不翻转。
/// </summary>
public class KeyringRecoveryFlowTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record KeyringStatusResponse(string Status, int AccountsPending, int BackupConfigsPending);

    private IKeyringHealth Keyring => factory.Services.GetRequiredService<IKeyringHealth>();

    private static AccountRequest SampleAccount(string name) => new(
        Name: name,
        Description: null,
        BlobEndpoint: "https://" + name + ".blob.core.windows.net",
        Region: AzureRegion.Global,
        AccountKey: "dGVzdGtleQ==",
        UseProxy: false,
        ProxyMode: ProxyMode.Independent,
        ProxyHost: null,
        ProxyPort: null,
        ProxyUsername: null,
        ProxyPassword: null);

    private static BackupConfigRequest SampleConfig(int accountId, string name, string? password) => new(
        AccountId: accountId,
        ContainerName: name + "-container",
        Name: name,
        Description: null,
        LocalRoot: "/data/" + name,
        Password: password,
        IndexTier: StorageTier.Hot,
        DataTier: StorageTier.Cool,
        IgnoreRules: null,
        DontCompressRules: null,
        DontGroupRules: null,
        IncludeSymlinks: false,
        MaxVersions: 50,
        MaxAgeDays: 180,
        RetentionMode: RetentionMode.EitherTriggers,
        SingleFileThresholdBytes: 5_000_000,
        GroupCapBytes: 100_000_000);

    [Fact]
    public async Task Accounts_Pending_Reaches_Zero_While_A_Stale_Backup_Password_Keeps_Status_Lost()
    {
        var account = (await (await _client.PostAsJsonAsync("/api/accounts", SampleAccount("recovery-flow-acct")))
            .Content.ReadFromJsonAsync<AccountResponse>())!;
        var encrypted = (await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleConfig(account.Id, "recovery-flow-encrypted", "s3cret")))
            .Content.ReadFromJsonAsync<BackupConfigResponse>())!;

        // /keys 丢失：账户密钥与备份密码都变成当前密钥环解不开的旧密文。
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Accounts.FirstAsync(a => a.Id == account.Id)).AccountKeyProtected = TestSecrets.Stale("old-key");
            (await db.BackupConfigs.FirstAsync(c => c.Id == encrypted.Id)).PasswordProtected = TestSecrets.Stale("old-pw");
            await db.SaveChangesAsync();
        }
        Keyring.Set(KeyringStatus.Lost);

        try
        {
            var before = await (await _client.GetAsync("/api/system/keyring"))
                .Content.ReadFromJsonAsync<KeyringStatusResponse>();
            Assert.Equal("Lost", before!.Status);
            Assert.Equal(1, before.AccountsPending);
            Assert.Equal(1, before.BackupConfigsPending);

            // 账户重设成功（等价于 reset-secrets 验证通过后落库 + 尝试收尾），备份密码还没轮到。
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
                (await db.Accounts.FirstAsync(a => a.Id == account.Id)).AccountKeyProtected = encryption.Encrypt("dGVzdGtleQ==");
                await db.SaveChangesAsync();

                // 备份密码仍解不开 → 收尾判定必须拒绝翻转。
                Assert.False(await scope.ServiceProvider.GetRequiredService<KeyringRecovery>().TryCompleteAsync());
            }

            var after = await (await _client.GetAsync("/api/system/keyring"))
                .Content.ReadFromJsonAsync<KeyringStatusResponse>();
            Assert.Equal("Lost", after!.Status);          // 备份密码还没修好，全局状态不许翻转
            Assert.Equal(0, after.AccountsPending);       // 但账户已经全部修好，计数必须归零
            Assert.Equal(1, after.BackupConfigsPending);

            var accounts = await (await _client.GetAsync("/api/accounts"))
                .Content.ReadFromJsonAsync<List<AccountResponse>>();
            Assert.False(accounts!.Single(a => a.Id == account.Id).SecretsUnavailable);

            var configs = await (await _client.GetAsync("/api/backup-configs"))
                .Content.ReadFromJsonAsync<List<BackupConfigResponse>>();
            Assert.True(configs!.Single(c => c.Id == encrypted.Id).SecretsUnavailable);
        }
        finally
        {
            Keyring.Set(KeyringStatus.Healthy);
        }
    }
}
