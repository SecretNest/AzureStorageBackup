using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

public class KeyringStatusEndpointTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record KeyringStatusResponse(string Status, int AccountsPending, int BackupConfigsPending);

    private IKeyringHealth Keyring => factory.Services.GetRequiredService<IKeyringHealth>();

    [Fact]
    public async Task Reports_Healthy_On_A_Fresh_Database()
    {
        var res = await _client.GetAsync("/api/system/keyring");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<KeyringStatusResponse>();
        Assert.Equal("Healthy", body!.Status);
        Assert.Equal(0, body.AccountsPending);
    }

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

    /// <summary>
    /// 验证计数/标记规则不对称的两条边：账户密钥必填 → 全计全标；
    /// 备份密码可空 → 只有真正带密码的记录才计数/标记，未加密的备份即便密钥环 Lost 也不受影响。
    /// 若计数规则被反转（比如漏过滤未加密配置、或漏计全部账户），此测试应当失败。
    /// </summary>
    [Fact]
    public async Task Reports_Lost_With_Correct_Counts_And_Per_Record_Flags()
    {
        var account = (await (await _client.PostAsJsonAsync("/api/accounts", SampleAccount("keyring-status-acct")))
            .Content.ReadFromJsonAsync<AccountResponse>())!;

        var encrypted = (await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleConfig(account.Id, "keyring-status-encrypted", "s3cret")))
            .Content.ReadFromJsonAsync<BackupConfigResponse>())!;
        var plain = (await (await _client.PostAsJsonAsync("/api/backup-configs",
                SampleConfig(account.Id, "keyring-status-plain", null)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>())!;

        var accountsBefore = await (await _client.GetAsync("/api/accounts"))
            .Content.ReadFromJsonAsync<List<AccountResponse>>();
        var configsBefore = await (await _client.GetAsync("/api/backup-configs"))
            .Content.ReadFromJsonAsync<List<BackupConfigResponse>>();
        Assert.All(accountsBefore!, a => Assert.False(a.SecretsUnavailable));
        Assert.All(configsBefore!, c => Assert.False(c.SecretsUnavailable));

        Keyring.Set(KeyringStatus.Lost);
        try
        {
            var status = await (await _client.GetAsync("/api/system/keyring"))
                .Content.ReadFromJsonAsync<KeyringStatusResponse>();
            Assert.Equal("Lost", status!.Status);
            Assert.Equal(accountsBefore!.Count, status.AccountsPending);
            // 只有加密配置计数：明文配置没有密文可丢，不应计入。
            Assert.Equal(1, status.BackupConfigsPending);

            var accounts = await (await _client.GetAsync("/api/accounts"))
                .Content.ReadFromJsonAsync<List<AccountResponse>>();
            Assert.All(accounts!, a => Assert.True(a.SecretsUnavailable));

            var configs = await (await _client.GetAsync("/api/backup-configs"))
                .Content.ReadFromJsonAsync<List<BackupConfigResponse>>();
            Assert.True(configs!.Single(c => c.Id == encrypted.Id).SecretsUnavailable);
            Assert.False(configs!.Single(c => c.Id == plain.Id).SecretsUnavailable);
        }
        finally
        {
            Keyring.Set(KeyringStatus.Healthy);
        }
    }
}
