using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The verdict "this container already holds a backup" used to look **only** at whether the cloud info file was there
/// (<see cref="ContainerService"/> → <see cref="BackupDiscovery"/>). But that file is written by the very last step of a
/// backup (BackupOrchestrator's Finalize), so halfway through a first backup the container listing reports it as empty —
/// the user goes by that listing, hands the same container to a second backup, and the two write their own indexes over each other.
/// <para>
/// The authority on occupancy is local: the <see cref="BackupConfig"/> row is in the database from the moment it was created, with nothing to wait on in the cloud.
/// Backup in progress, a failed backup leaving a half-finished product, the cloud momentarily unreadable — all three are covered at once.
/// </para>
/// </summary>
public class ContainerInUseVisibilityTests
{
    /// <summary>Returns a preset cloud presence per name, without touching real Azure.</summary>
    private sealed class StubContainerService(params ContainerInfo[] listed) : IContainerService
    {
        public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(Account a, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContainerInfo>>(listed);

        public Task CreateContainerAsync(Account a, string name, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteContainerAsync(Account a, string name, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static async Task<int> CreateAccountAsync(HttpClient client, string? endpoint = null)
    {
        var res = await client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            Name: "inuse-" + Guid.NewGuid().ToString("N")[..8],
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

    private static (TestWebAppFactory Factory, HttpClient Client) Rig(params ContainerInfo[] listed)
    {
        var factory = new TestWebAppFactory();
        var configured = factory.WithWebHostBuilder(b => b.ConfigureServices(
            s => s.AddSingleton<IContainerService>(new StubContainerService(listed))));
        return (factory, configured.CreateClient());
    }

    private static async Task AddConfigAsync(TestWebAppFactory factory, int accountId, string container, string name)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IBackupConfigService>().CreateAsync(new BackupConfig
        {
            AccountId = accountId,
            ContainerName = container,
            Name = name,
            LocalRoot = "/data/" + container,
        });
    }

    /// <summary>The exact scene the user hit: the first backup has not written the info file yet, but the container already holds this run's uploaded data.</summary>
    [Fact]
    public async Task A_Container_Held_By_A_Local_Config_Is_In_Use_Even_With_No_Cloud_Index_Yet()
    {
        var (factory, client) = Rig(new ContainerInfo("mid-backup", BackupPresence.None));
        using var _ = factory;

        var accountId = await CreateAccountAsync(client);
        await AddConfigAsync(factory, accountId, "mid-backup", "Photos");

        var list = await client.GetFromJsonAsync<List<ContainerInfo>>($"/api/accounts/{accountId}/containers");

        var row = Assert.Single(list!);
        // Name who is holding it: just saying "unavailable" leaves the user with no idea which backup to go touch.
        Assert.Equal("Photos", row.InUseBy);
    }

    /// <summary>Occupancy is scoped exactly by (account, container); containers of the same name under different accounts do not interfere.</summary>
    [Fact]
    public async Task A_Config_In_Another_Account_Does_Not_Mark_The_Container()
    {
        var (factory, client) = Rig(new ContainerInfo("shared-name", BackupPresence.None));
        using var _ = factory;

        var held = await CreateAccountAsync(client);
        // A genuinely different account means a different endpoint now (one endpoint, one record); this rig's
        // container service is a fake, so the endpoint can be fictitious.
        var other = await CreateAccountAsync(client, "https://second-" + Guid.NewGuid().ToString("N")[..8] + ".blob.core.windows.net");
        await AddConfigAsync(factory, held, "shared-name", "Held");

        var list = await client.GetFromJsonAsync<List<ContainerInfo>>($"/api/accounts/{other}/containers");

        Assert.Null(Assert.Single(list!).InUseBy);
    }

    /// <summary>The cloud verdict is preserved as-is: a container no local config holds keeps whatever presence it had.</summary>
    [Fact]
    public async Task Cloud_Presence_Still_Reported_For_Containers_No_Config_Holds()
    {
        var (factory, client) = Rig(
            new ContainerInfo("orphan-backup", BackupPresence.Plain),
            new ContainerInfo("really-empty", BackupPresence.None));
        using var _ = factory;

        var accountId = await CreateAccountAsync(client);

        var list = await client.GetFromJsonAsync<List<ContainerInfo>>($"/api/accounts/{accountId}/containers");

        var orphan = list!.Single(c => c.Name == "orphan-backup");
        Assert.Equal(BackupPresence.Plain, orphan.Backup);
        Assert.Null(orphan.InUseBy);
        Assert.Null(list!.Single(c => c.Name == "really-empty").InUseBy);
    }
}
