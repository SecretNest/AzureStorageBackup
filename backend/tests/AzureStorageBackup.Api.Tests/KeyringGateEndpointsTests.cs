using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Task 5 复审 Finding 1/3：密钥环丢失时，真正状态变更/依赖凭据的三个触发点
/// （/run、/restore、/repair）必须在入口即 409，而只读的列表端点仍须可达
/// （否则整个「恢复模式」功能失去意义）。KeyringGuardTests 只测了静态方法本身，
/// 这里驱动真实 HTTP 请求，防止有人漏挂闸门却测试仍然全绿。
/// </summary>
public class KeyringGateEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record KeyringLostError(string error, string code);

    private static BackupConfigRequest SampleRequest(string name) => new(
        AccountId: 1,
        ContainerName: name + "-container",
        Name: name,
        Description: null,
        LocalRoot: "/data/" + name,
        Password: "s3cret",
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

    private async Task<BackupConfigResponse> CreateConfigAsync(string name)
        => (await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest(name)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>())!;

    private IKeyringHealth Keyring => factory.Services.GetRequiredService<IKeyringHealth>();

    [Fact]
    public async Task Run_Restore_Repair_Return_409_KeyringLost_When_Keyring_Is_Lost()
    {
        var created = await CreateConfigAsync("gate-run-restore-repair");

        Keyring.Set(KeyringStatus.Lost);
        try
        {
            var run = await _client.PostAsync($"/api/backup-configs/{created.Id}/run", null);
            Assert.Equal(HttpStatusCode.Conflict, run.StatusCode);
            var runBody = await run.Content.ReadFromJsonAsync<KeyringLostError>();
            Assert.Equal("keyring_lost", runBody!.code);

            var restore = await _client.PostAsJsonAsync($"/api/backup-configs/{created.Id}/restore",
                new RestoreRequestBody(null, null));
            Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
            var restoreBody = await restore.Content.ReadFromJsonAsync<KeyringLostError>();
            Assert.Equal("keyring_lost", restoreBody!.code);

            var repair = await _client.PostAsync($"/api/backup-configs/{created.Id}/repair", null);
            Assert.Equal(HttpStatusCode.Conflict, repair.StatusCode);
            var repairBody = await repair.Content.ReadFromJsonAsync<KeyringLostError>();
            Assert.Equal("keyring_lost", repairBody!.code);
        }
        finally
        {
            Keyring.Set(KeyringStatus.Healthy);
        }
    }

    /// <summary>整个恢复模式的存在意义：即便密钥环丢失，只读列表端点也不能跟着一起 409。</summary>
    [Fact]
    public async Task List_Endpoint_Still_Returns_200_When_Keyring_Is_Lost()
    {
        await CreateConfigAsync("gate-list-still-works");

        Keyring.Set(KeyringStatus.Lost);
        try
        {
            var res = await _client.GetAsync("/api/backup-configs");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var list = await res.Content.ReadFromJsonAsync<List<BackupConfigResponse>>();
            Assert.NotEmpty(list!);
        }
        finally
        {
            Keyring.Set(KeyringStatus.Healthy);
        }
    }
}
