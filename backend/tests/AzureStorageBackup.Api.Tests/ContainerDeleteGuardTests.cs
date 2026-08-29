using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Deleting a container that still holds a backup from the Account → Containers page makes the cloud data vanish while the
/// local <see cref="BackupConfig"/> row stays untouched: the backup list then keeps showing a backup with nothing behind it,
/// and every operation the user clicks into fails in some shape or another. This is something a user actually hit.
/// <para>
/// The delete-the-backup path (<c>DELETE /api/backups/{id}?deleteContainer=true</c>) already gets it right: it clears the
/// local index cache, backup state and operation log along with it, and it blocks "delete while an operation is running".
/// So this is not a second cleanup implementation, it just closes the shortcut around that path and points the user back to the right one.
/// </para>
/// </summary>
public class ContainerDeleteGuardTests
{
    private sealed record ErrorBody(string error);

    /// <summary>Records delete calls so we can assert "the cloud was never touched at all".</summary>
    private sealed class RecordingContainerService : IContainerService
    {
        public List<string> Deleted { get; } = [];

        public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(Account a, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContainerInfo>>([]);

        public Task CreateContainerAsync(Account a, string name, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteContainerAsync(Account a, string name, CancellationToken ct = default)
        {
            Deleted.Add(name);
            return Task.CompletedTask;
        }
    }

    private static async Task<int> CreateAccountAsync(HttpClient client, string? endpoint = null)
    {
        var res = await client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            Name: "guard-" + Guid.NewGuid().ToString("N")[..8],
            Description: null,
            BlobEndpoint: endpoint ?? "http://127.0.0.1:10000/devstoreaccount1",
            Region: AzureRegion.Global,
            AccountKey: "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",
            UseProxy: false,
            ProxyMode: ProxyMode.Independent,
            ProxyHost: null,
            ProxyPort: null,
            ProxyUsername: null,
            ProxyPassword: null));
        // One endpoint, one record now: adopt the account an earlier test already registered for Azurite.
        return await TestAccounts.EnsureFromAsync(client, res, endpoint ?? "http://127.0.0.1:10000/devstoreaccount1");
    }

    private static (TestWebAppFactory Factory, HttpClient Client, RecordingContainerService Containers) Rig()
    {
        var factory = new TestWebAppFactory();
        var recorder = new RecordingContainerService();
        var configured = factory.WithWebHostBuilder(
            b => b.ConfigureServices(s => s.AddSingleton<IContainerService>(recorder)));
        return (factory, configured.CreateClient(), recorder);
    }

    [Fact]
    public async Task Deleting_A_Container_That_Still_Holds_A_Backup_Is_Refused_Without_Touching_Azure()
    {
        var (factory, client, containers) = Rig();
        using var _ = factory;

        var accountId = await CreateAccountAsync(client);
        const string container = "guarded-container";

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBackupConfigService>().CreateAsync(new BackupConfig
            {
                AccountId = accountId,
                ContainerName = container,
                Name = "Photos",
                LocalRoot = "/data/photos",
            });
        }

        var res = await client.DeleteAsync($"/api/accounts/{accountId}/containers/{container}");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        // The crux: the guard has to take effect **before touching the cloud**. Delete first and report afterwards and the data is already gone; nothing you report then helps.
        Assert.Empty(containers.Deleted);

        var body = await res.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.NotNull(body);
        // The error has to name which backup is in the way and point at the right path — otherwise the user only learns "not allowed" and has no idea what to do next.
        Assert.Contains("Photos", body!.error);
        Assert.Contains("backup", body.error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deleting_A_Container_With_No_Backup_Still_Works()
    {
        var (factory, client, containers) = Rig();
        using var _ = factory;

        var accountId = await CreateAccountAsync(client);

        var res = await client.DeleteAsync($"/api/accounts/{accountId}/containers/spare-container");

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal(["spare-container"], containers.Deleted);
    }

    /// <summary>
    /// The guard is scoped exactly by (account, container). <see cref="BackupConfig"/> has a unique index on those two
    /// columns, and different accounts may hold containers of the same name — matching by name alone would let account A's backup block an empty container of the same name in account B.
    /// </summary>
    [Fact]
    public async Task A_Backup_In_One_Account_Does_Not_Guard_The_Same_Name_In_Another()
    {
        var (factory, client, containers) = Rig();
        using var _ = factory;

        var guarded = await CreateAccountAsync(client);
        // A genuinely different account means a different endpoint now (one endpoint, one record); the
        // container service in this rig is a fake, so the endpoint can be fictitious.
        var other = await CreateAccountAsync(client, "https://second-" + Guid.NewGuid().ToString("N")[..8] + ".blob.core.windows.net");
        const string container = "shared-name";

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBackupConfigService>().CreateAsync(new BackupConfig
            {
                AccountId = guarded,
                ContainerName = container,
                Name = "Guarded",
                LocalRoot = "/data/guarded",
            });
        }

        Assert.Equal(HttpStatusCode.Conflict,
            (await client.DeleteAsync($"/api/accounts/{guarded}/containers/{container}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/accounts/{other}/containers/{container}")).StatusCode);
        Assert.Equal([container], containers.Deleted);
    }
}
