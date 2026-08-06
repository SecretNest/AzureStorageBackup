using System.Net;
using System.Net.Http.Json;
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
        var id = await CreateConfigAsync(await CreateAccountAsync(), _dir);

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
        var id = await CreateConfigAsync(await CreateAccountAsync(), _dir);

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
        var id = await CreateConfigAsync(await CreateAccountAsync(), localRoot: "");

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

    [Fact]
    public async Task Unknown_Config_Is_A_404()
    {
        Directory.CreateDirectory(_dir);

        var res = await _client.PostAsJsonAsync(
            "/api/backup-configs/999999/local-root/preview", new { newRoot = _dir });

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
