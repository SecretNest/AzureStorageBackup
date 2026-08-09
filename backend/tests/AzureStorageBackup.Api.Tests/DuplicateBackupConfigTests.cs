using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Two backup configs pointing at the same (account, container) means two mutually unaware sets of version numbers and indexes written to the same place:
/// whichever runs second reads a cloud info file that either has not been written yet or belongs to the other one, so it starts over from version 1,
/// overwrites the other's index.json, turns the other's data blobs into orphans, and the next retention cleanup deletes them.
/// <para>
/// So both create and import must refuse **before anything is written to the database**, with a unique index on the database catching the path that bypasses the endpoints.
/// </para>
/// </summary>
public class DuplicateBackupConfigTests
{
    private sealed record ErrorBody(string error);

    private static async Task<int> CreateAccountAsync(HttpClient client)
    {
        var res = await client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            Name: "dup-" + Guid.NewGuid().ToString("N")[..8],
            Description: null,
            BlobEndpoint: "http://127.0.0.1:10000/devstoreaccount1",
            Region: AzureRegion.Global,
            AccountKey: "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",
            UseProxy: false,
            ProxyMode: ProxyMode.Independent,
            ProxyHost: null,
            ProxyPort: null,
            ProxyUsername: null,
            ProxyPassword: null));
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<AccountResponse>())!.Id;
    }

    private static BackupConfigRequest Request(int accountId, string container, string name, string localRoot) =>
        new(accountId, container, name, null, localRoot, null,
            StorageTier.Hot, StorageTier.Hot, null, null, null, false,
            100, 180, RetentionMode.EitherTriggers, 5_000_000, 100_000_000);

    [Fact]
    public async Task A_Second_Backup_On_The_Same_Container_Is_Refused()
    {
        using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        var accountId = await CreateAccountAsync(client);

        var first = await client.PostAsJsonAsync("/api/backup-configs",
            Request(accountId, "one-container", "Photos", "/data/photos"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/backup-configs",
            Request(accountId, "one-container", "Documents", "/data/documents"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<ErrorBody>();
        // Name who is holding this container — otherwise the user only learns "you may not create it" and has no idea which backup to go look at.
        Assert.Contains("Photos", body!.error);

        // The crux: the refusal must happen before the database write, so only the first row is left behind.
        using var scope = factory.Services.CreateScope();
        var configs = await scope.ServiceProvider.GetRequiredService<IBackupConfigService>().ListAsync();
        Assert.Equal(["Photos"], configs.Select(c => c.Name));
    }

    /// <summary>Import has to be blocked too, and the check must come before any cloud read: a question the local database can answer should not cost a network round trip first.</summary>
    [Fact]
    public async Task Importing_Into_A_Container_A_Config_Already_Holds_Is_Refused()
    {
        using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        var accountId = await CreateAccountAsync(client);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/backup-configs",
            Request(accountId, "taken-container", "Photos", "/data/photos"))).StatusCode);

        var res = await client.PostAsJsonAsync("/api/backup-configs/import",
            new ImportRequest(accountId, "taken-container", null));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("Photos", (await res.Content.ReadFromJsonAsync<ErrorBody>())!.error);
    }

    [Fact]
    public async Task The_Same_Container_Name_Under_A_Different_Account_Is_Allowed()
    {
        using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        var one = await CreateAccountAsync(client);
        var two = await CreateAccountAsync(client);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/backup-configs",
            Request(one, "shared-name", "First", "/data/first"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/backup-configs",
            Request(two, "shared-name", "Second", "/data/second"))).StatusCode);
    }

    /// <summary>
    /// The endpoint's check is "look, then write", which leaves a window when two requests collide. The unique index on the database is the backstop:
    /// whether you write straight past the endpoint or squeeze concurrently into that window, the second row never lands.
    /// </summary>
    [Fact]
    public async Task The_Database_Itself_Rejects_A_Duplicate_Written_Behind_The_Service()
    {
        using var factory = new TestWebAppFactory();
        var client = factory.CreateClient();
        var accountId = await CreateAccountAsync(client);

        using var scope = factory.Services.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
        await configs.CreateAsync(new BackupConfig
        {
            AccountId = accountId, ContainerName = "sealed-container", Name = "First", LocalRoot = "/data/first",
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => configs.CreateAsync(new BackupConfig
        {
            AccountId = accountId, ContainerName = "sealed-container", Name = "Second", LocalRoot = "/data/second",
        }));
    }
}
