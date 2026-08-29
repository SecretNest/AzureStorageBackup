using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;
using Microsoft.AspNetCore.Hosting;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The sentinel path as it is configured: what the endpoints accept, what they refuse, and what stays editable
/// after creation. The gate that acts on it lives in <see cref="SentinelGateTests"/> and
/// <see cref="SentinelSkipTests"/>.
/// </summary>
public sealed class SentinelPathTests : IDisposable
{
    private sealed class RootedFactory(string root) : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Backup:Root", root);
        }
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "asb-sent-cfg-" + Guid.NewGuid().ToString("N"));

    public SentinelPathTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static AccountRequest SampleAccount() => new(
        Name: "acct-" + Guid.NewGuid().ToString("N")[..6], Description: null,
        BlobEndpoint: "https://t" + Guid.NewGuid().ToString("N")[..12] + ".blob.core.windows.net",
        Region: AzureRegion.Global, AccountKey: "dGVzdA==",
        UseProxy: false, ProxyMode: ProxyMode.Independent,
        ProxyHost: null, ProxyPort: null, ProxyUsername: null, ProxyPassword: null);

    private BackupConfigRequest SampleConfig(int accountId, string? sentinel) => new(
        AccountId: accountId,
        ContainerName: "photos",
        Name: "photos",
        Description: null,
        LocalRoot: Path.Combine(_root, "photos"),
        Password: null,
        IndexTier: StorageTier.Hot,
        DataTier: StorageTier.Cool)
    { SentinelPath = sentinel };

    private async Task<(HttpClient Client, int AccountId)> StartAsync(TestWebAppFactory factory)
    {
        var client = factory.CreateClient();
        var acct = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount()))
            .Content.ReadFromJsonAsync<AccountResponse>();
        return (client, acct!.Id);
    }

    [Fact]
    public async Task A_Sentinel_That_Does_Not_Exist_Yet_Is_Accepted()
    {
        // The whole point of the feature: the moment you configure it is very likely a moment when the mount is
        // *not* there. Validating existence at save time would make the setting impossible to enter exactly when
        // it is needed.
        using var factory = new RootedFactory(_root);
        var (client, accountId) = await StartAsync(factory);
        var sentinel = Path.Combine(_root, "photos", "not-mounted-yet", ".mounted");

        var res = await client.PostAsJsonAsync("/api/backup-configs", SampleConfig(accountId, sentinel));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var created = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal(sentinel, created!.SentinelPath);
    }

    [Fact]
    public async Task A_Sentinel_Outside_This_Backups_Local_Root_Is_Rejected()
    {
        // Stricter than the Backup__Root filter, on purpose: the sentinel's job is to say whether *this*
        // backup's source is mounted, and one living somewhere else would go on saying "yes" while the source
        // it vouches for is gone. This path is inside Backup__Root and still refused — which is the point.
        using var factory = new RootedFactory(_root);
        var (client, accountId) = await StartAsync(factory);

        var res = await client.PostAsJsonAsync(
            "/api/backup-configs", SampleConfig(accountId, Path.Combine(_root, "somewhere-else", ".mounted")));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("local root", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_Local_Root_Itself_Is_An_Acceptable_Sentinel()
    {
        // The boundary is inclusive: "under the root" has to admit the root, or the picker's own starting point
        // is a value the form refuses to save.
        using var factory = new RootedFactory(_root);
        var (client, accountId) = await StartAsync(factory);

        var res = await client.PostAsJsonAsync(
            "/api/backup-configs", SampleConfig(accountId, Path.Combine(_root, "photos")));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    [Fact]
    public async Task The_Sentinel_Can_Be_Changed_After_Creation()
    {
        // Unlike LocalRoot, this is not a locked base field: nothing downstream is keyed by it, and an operator
        // who reorganises a mount has to be able to follow it without recreating the backup.
        using var factory = new RootedFactory(_root);
        var (client, accountId) = await StartAsync(factory);
        var created = await (await client.PostAsJsonAsync(
                "/api/backup-configs", SampleConfig(accountId, Path.Combine(_root, "photos", "old"))))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        var moved = Path.Combine(_root, "photos", "new");

        var res = await client.PutAsJsonAsync(
            $"/api/backup-configs/{created!.Id}", SampleConfig(accountId, moved));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var updated = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Equal(moved, updated!.SentinelPath);
    }

    [Fact]
    public async Task The_Sentinel_Can_Be_Cleared_After_Creation()
    {
        // Turning the feature back off has to be reachable from the same text box that turned it on; a blank must
        // land as "no sentinel", not as a sentinel named "".
        using var factory = new RootedFactory(_root);
        var (client, accountId) = await StartAsync(factory);
        var created = await (await client.PostAsJsonAsync(
                "/api/backup-configs", SampleConfig(accountId, Path.Combine(_root, "photos", ".mounted"))))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var res = await client.PutAsJsonAsync(
            $"/api/backup-configs/{created!.Id}", SampleConfig(accountId, "   "));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var updated = await res.Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.Null(updated!.SentinelPath);
    }
}
