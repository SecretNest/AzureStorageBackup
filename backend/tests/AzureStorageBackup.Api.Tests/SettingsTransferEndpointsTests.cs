using System.Net;
using System.Net.Http.Json;
using System.Text;
using AzureStorageBackup.Api.Endpoints;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

public class SettingsTransferEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();
    private IKeyringHealth Keyring => factory.Services.GetRequiredService<IKeyringHealth>();

    private static AccountRequest Request(string name, string endpoint) =>
        new(name, null, endpoint, AzureRegion.Global, "key==", false, ProxyMode.Independent, null, null, null, null);

    [Fact]
    public async Task Export_Is_A_Named_Json_Download_And_Omits_Secrets_By_Default()
    {
        await TestAccounts.EnsureAsync(_client, Request("export-a", "https://export-a.blob.core.windows.net"));

        var res = await _client.GetAsync("/api/settings/export");

        res.EnsureSuccessStatusCode();
        Assert.Equal("application/json", res.Content.Headers.ContentType!.MediaType);
        var disposition = res.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition!.DispositionType);
        Assert.Matches(@"^""?asb-settings-\d{8}-\d{4}\.json""?$", disposition.FileName);
        // A file that may hold account keys must never land in a shared or proxy cache.
        Assert.True(res.Headers.CacheControl?.NoStore, "Cache-Control: no-store expected on the export");
        var doc = await res.Content.ReadFromJsonAsync<SettingsDocument>();
        Assert.False(doc!.IncludesSecrets);
        var entry = doc.Accounts!.Single(a => a.Name == "export-a");
        Assert.Null(entry.AccountKey);
    }

    [Fact]
    public async Task Export_With_Secrets_Carries_The_Key_And_Is_Refused_When_The_Keyring_Is_Lost()
    {
        await TestAccounts.EnsureAsync(_client, Request("export-b", "https://export-b.blob.core.windows.net"));

        var doc = await _client.GetFromJsonAsync<SettingsDocument>("/api/settings/export?includeSecrets=true");
        Assert.True(doc!.IncludesSecrets);
        Assert.Equal("key==", doc.Accounts!.Single(a => a.Name == "export-b").AccountKey);

        Keyring.Set(KeyringStatus.Lost);
        try
        {
            var res = await _client.GetAsync("/api/settings/export?includeSecrets=true");
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            var body = await res.Content.ReadFromJsonAsync<KeyringLostError>();
            Assert.Equal("keyring_lost", body!.code);

            var plain = await _client.GetAsync("/api/settings/export");
            Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        }
        finally
        {
            Keyring.Set(KeyringStatus.Healthy);
        }
    }

    [Fact]
    public async Task Preview_Then_Import_Round_Trips_And_Keeps_The_Matched_Account_Id()
    {
        var id = await TestAccounts.EnsureAsync(_client, Request("rt", "https://rt.blob.core.windows.net"));
        var doc = await _client.GetFromJsonAsync<SettingsDocument>("/api/settings/export");
        var edited = doc! with
        {
            Accounts = [.. doc.Accounts!.Select(a => a.Name == "rt" ? a with { Name = "rt-renamed" } : a),
                new SettingsAccountEntry("rt-new", null, "https://rt-new.blob.core.windows.net", AzureRegion.Global,
                    "new-key==", false, ProxyMode.Independent, null, null, null, null)],
        };

        var preview = await (await _client.PostAsJsonAsync("/api/settings/import/preview", edited)).Content.ReadFromJsonAsync<ImportPlan>();
        Assert.Contains(preview!.Accounts, a => a.Name == "rt-renamed" && a.Action == "update");
        Assert.Contains(preview.Accounts, a => a.Name == "rt-new" && a.Action == "create" && !a.NeedsAccountKey);

        var applied = await _client.PostAsJsonAsync("/api/settings/import", edited);
        applied.EnsureSuccessStatusCode();

        var accounts = await _client.GetFromJsonAsync<List<AccountResponse>>("/api/accounts");
        Assert.Equal("rt-renamed", accounts!.Single(a => a.Id == id).Name);
        Assert.Contains(accounts!, a => a.Name == "rt-new");
    }

    [Fact]
    public async Task Import_Rejects_A_Bad_File_With_400_And_A_Message()
    {
        var res = await _client.PostAsJsonAsync("/api/settings/import", new { format = "nope", version = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Contains("azure-storage-backup-settings", body!["error"]);
    }

    [Fact]
    public async Task Import_Rejects_Non_Object_Body_With_400()
    {
        var res = await _client.PostAsync("/api/settings/import/preview",
            new StringContent("[]", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
