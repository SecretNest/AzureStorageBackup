using System.Net;
using System.Net.Http.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 两个「先查存在、再连云验证、最后写库」的重设端点（F2/F3）。验证要连云，检查与写库之间窗口不短：
/// 行在这期间被删掉时必须回 404（与全仓一致），而不是 FirstAsync 抛出的 500；
/// 取消（客户端断开 / 进程关停）必须原样上抛，而不是被包成「验证失败」的 400（约定见 a3ac967）。
/// <para>
/// 这里把云端那一步换成桩：删行 / 抛取消都在桩里发生，正好落在那个窗口内，不需要 Azurite。
/// </para>
/// </summary>
public sealed class EndpointWritePathRaceTests
{
    /// <summary>在基础测试宿主上再替换若干服务（桩件）。</summary>
    private sealed class StubbedFactory(Action<IServiceCollection> configure) : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(configure); // 在 Program.cs 的注册之后执行，故能覆盖
        }
    }

    /// <summary>一被要求建客户端就抛取消——用来把 OperationCanceledException 送进 BackupChecker 的云端调用里。</summary>
    private sealed class CancelsOnCreateServiceClient : IBlobClientFactory
    {
        public BlobServiceClient CreateServiceClient(Account account) => throw new OperationCanceledException();

        public Task<ConnectionResult> TestConnectionAsync(Account account, CancellationToken ct = default)
            => throw new OperationCanceledException();
    }

    /// <summary>连通测试「通过」，但在返回前把该账户行删掉——精确模拟验证成功到写库之间的删除竞争。</summary>
    private sealed class DeletesAccountOnTestConnection(IServiceScopeFactory scopes) : IBlobClientFactory
    {
        public BlobServiceClient CreateServiceClient(Account account) => throw new NotSupportedException();

        public async Task<ConnectionResult> TestConnectionAsync(Account account, CancellationToken ct = default)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Accounts.Where(a => a.Id == account.Id).ExecuteDeleteAsync(ct);
            return new ConnectionResult(true, null);
        }
    }

    /// <summary>只桩 <see cref="IBackupInfoStore.ReadInfoWithETagAsync"/>（reset-password 唯一用到的方法）。</summary>
    private sealed class StubInfoStore(Func<(BackupInfoFile Info, string ETag)?> onRead) : IBackupInfoStore
    {
        public Task<BackupInfoFile?> ReadInfoAsync(Account account, string container, string? password, CancellationToken ct = default)
            => Task.FromResult(onRead()?.Info);

        public Task<(BackupInfoFile Info, string ETag)?> ReadInfoWithETagAsync(
            Account account, string container, string? password, CancellationToken ct = default)
            => Task.FromResult(onRead());

        public Task WriteInfoAsync(Account account, string container, BackupInfoFile info, string? password, AccessTier? tier = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string> WriteInfoConditionalAsync(Account account, string container, BackupInfoFile info, string? password, AccessTier? tier, string? ifMatch, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<VersionIndex> ReadIndexAsync(Account account, string container, string indexBlob, string? password, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<string> WriteIndexAsync(Account account, string container, int version, VersionIndex index, string? password, AccessTier? tier = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static BackupInfoFile EncryptedInfo() => new()
    {
        Backup = new BackupMeta
        {
            Name = "race-fixture",
            Encrypted = true,
            CreatedAt = new DateTimeOffset(2026, 7, 25, 0, 0, 0, TimeSpan.Zero),
        },
    };

    private static AccountRequest SampleAccount(string name) => new(
        Name: name,
        Description: null,
        BlobEndpoint: "https://example.blob.core.windows.net",
        Region: AzureRegion.Global,
        AccountKey: "dGVzdGtleQ==",
        UseProxy: false,
        ProxyMode: ProxyMode.Independent,
        ProxyHost: null,
        ProxyPort: null,
        ProxyUsername: null,
        ProxyPassword: null);

    private static BackupConfigRequest SampleConfig(int accountId, string password) => new(
        accountId, "race-container", "race-fixture", null, "/some/local/root", password,
        StorageTier.Hot, StorageTier.Archive, null, null, null, false,
        100, 180, RetentionMode.EitherTriggers, 5_000_000, 100_000_000);

    /// <summary>
    /// F2：POST /api/accounts/{id}/reset-secrets。验证通过之后账户行被删 → 404，而不是 FirstAsync 的 500。
    /// </summary>
    [Fact]
    public async Task ResetSecrets_Returns_404_When_The_Account_Is_Deleted_After_Verification()
    {
        using var factory = new StubbedFactory(services =>
        {
            services.RemoveAll<IBlobClientFactory>();
            services.AddSingleton<IBlobClientFactory>(sp =>
                new DeletesAccountOnTestConnection(sp.GetRequiredService<IServiceScopeFactory>()));
        });
        var client = factory.CreateClient();

        var created = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount("race-reset-secrets")))
            .Content.ReadFromJsonAsync<AccountResponse>();
        Assert.NotNull(created);

        var res = await client.PostAsJsonAsync(
            $"/api/accounts/{created!.Id}/reset-secrets", new ResetAccountSecretsRequest("dGVzdGtleTI=", null));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    /// <summary>
    /// F2：POST /api/backup-configs/{id}/reset-password。验证通过之后配置行被删 → 404。
    /// </summary>
    [Fact]
    public async Task ResetPassword_Returns_404_When_The_Config_Is_Deleted_After_Verification()
    {
        IServiceScopeFactory? scopes = null;
        var configId = 0;
        using var factory = new StubbedFactory(services =>
        {
            services.RemoveAll<IBackupInfoStore>();
            services.AddScoped<IBackupInfoStore>(_ => new StubInfoStore(() =>
            {
                // 验证「成功」的同时，把配置行删掉——正落在检查与写库之间的窗口里。
                using var scope = scopes!.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.BackupConfigs.Where(c => c.Id == configId).ExecuteDelete();
                return (EncryptedInfo(), "\"etag\"");
            }));
        });
        var client = factory.CreateClient();
        scopes = factory.Services.GetRequiredService<IServiceScopeFactory>();

        var account = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount("race-reset-password")))
            .Content.ReadFromJsonAsync<AccountResponse>();
        var config = await (await client.PostAsJsonAsync("/api/backup-configs", SampleConfig(account!.Id, "initial-password")))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.NotNull(config);
        configId = config!.Id;

        var res = await client.PostAsJsonAsync(
            $"/api/backup-configs/{configId}/reset-password", new ResetBackupPasswordRequest("the-real-password"));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    /// <summary>
    /// F3：验证过程中的取消（客户端断开 / 进程关停）不是「密码不对」。
    /// 修复前 catch (Exception) 会把它变成 400 "Verification failed: The operation was canceled."。
    /// </summary>
    [Fact]
    public async Task ResetPassword_Does_Not_Swallow_Cancellation_As_A_Verification_Failure()
    {
        using var factory = new StubbedFactory(services =>
        {
            services.RemoveAll<IBackupInfoStore>();
            services.AddScoped<IBackupInfoStore>(_ => new StubInfoStore(
                () => throw new OperationCanceledException()));
        });
        var client = factory.CreateClient();

        var account = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount("cancel-reset-password")))
            .Content.ReadFromJsonAsync<AccountResponse>();
        var config = await (await client.PostAsJsonAsync("/api/backup-configs", SampleConfig(account!.Id, "initial-password")))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.NotNull(config);

        var res = await client.PostAsJsonAsync(
            $"/api/backup-configs/{config!.Id}/reset-password", new ResetBackupPasswordRequest("the-real-password"));
        var body = await res.Content.ReadAsStringAsync();

        // 取消一路上抛，而不是被伪装成用户的密码错误。修复前这里是
        // 400 + "Verification failed: The operation was canceled."。
        // 刻意不断言响应体里出现异常类型名——那只在开发者异常页在管道里时成立
        // （ASPNETCORE_ENVIRONMENT=Production 下就没有），与被测行为无关。
        Assert.NotEqual(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.DoesNotContain("Verification failed", body);

        // 密文原封不动：既没落库，也没被当成「验证通过」。
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
        var row = await db.BackupConfigs.AsNoTracking().FirstAsync(c => c.Id == config!.Id);
        Assert.Equal("initial-password", TestSecrets.Reveal(encryption, row.PasswordProtected!));
    }

    /// <summary>
    /// F3 的另一处、也是唯一有**持久化副作用**的一处：POST /api/backup-configs/{id}/check 的
    /// catch 会把异常消息写成该备份的 Error 状态。取消（客户端断开 / 进程关停）不是「检查失败」，
    /// 修复前它会在配置行上留下 Status=Error + LastError="The operation was canceled."，
    /// 用户界面从此显示一个不存在的故障，直到手动 reset。
    /// </summary>
    [Fact]
    public async Task Check_Does_Not_Persist_Cancellation_As_An_Error_Status()
    {
        using var factory = new StubbedFactory(services =>
        {
            services.RemoveAll<IBlobClientFactory>();
            services.AddSingleton<IBlobClientFactory, CancelsOnCreateServiceClient>();
        });
        var client = factory.CreateClient();

        var account = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount("cancel-check")))
            .Content.ReadFromJsonAsync<AccountResponse>();
        var config = await (await client.PostAsJsonAsync("/api/backup-configs", SampleConfig(account!.Id, "pw")))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.NotNull(config);

        var res = await client.PostAsJsonAsync($"/api/backup-configs/{config!.Id}/check", new { });

        // 取消原样上抛（宿主渲染成 500），不是 200「检查完成」。
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.BackupConfigs.AsNoTracking().FirstAsync(c => c.Id == config.Id);

        Assert.Equal(BackupStatus.Normal, row.Status);
        Assert.Null(row.LastError);
        Assert.Null(row.LastErrorAt);
    }
}
