using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

public class AccountEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private IKeyringHealth Keyring => factory.Services.GetRequiredService<IKeyringHealth>();

    private static AccountRequest SampleRequest(string name = "prod") => new(
        Name: name,
        Description: "primary",
        BlobEndpoint: "https://t" + Guid.NewGuid().ToString("N")[..12] + ".blob.core.windows.net",
        Region: AzureRegion.Global,
        AccountKey: "dGVzdGtleQ==",
        UseProxy: false,
        ProxyMode: ProxyMode.Independent,
        ProxyHost: null,
        ProxyPort: null,
        ProxyUsername: null,
        ProxyPassword: null);

    [Fact]
    public async Task Post_Creates_Account_And_Returns_201()
    {
        var res = await _client.PostAsJsonAsync("/api/accounts", SampleRequest());

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var created = await res.Content.ReadFromJsonAsync<AccountResponse>();
        Assert.NotNull(created);
        Assert.True(created!.Id > 0);
        Assert.Equal("prod", created.Name);
    }

    [Fact]
    public async Task Post_Then_Get_Returns_Account()
    {
        var post = await _client.PostAsJsonAsync("/api/accounts", SampleRequest("get-test"));
        var created = await post.Content.ReadFromJsonAsync<AccountResponse>();

        var get = await _client.GetAsync($"/api/accounts/{created!.Id}");

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var fetched = await get.Content.ReadFromJsonAsync<AccountResponse>();
        Assert.Equal("get-test", fetched!.Name);
    }

    [Fact]
    public async Task Response_Does_Not_Expose_Secrets()
    {
        var post = await _client.PostAsJsonAsync("/api/accounts", SampleRequest("secret-test"));
        var body = await post.Content.ReadAsStringAsync();

        Assert.DoesNotContain("dGVzdGtleQ==", body);
        Assert.DoesNotContain("accountKey", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// F5: /test-connection was missing the empty-key check that POST / has. An empty key → AccountKeyProtected = "",
    /// which throws SecretUnavailableException at the decryption choke point and gets mapped by defence in depth to 409 keyring_lost —
    /// the user is told "the keyring cannot decrypt" when the real reason is simply that no key was entered. It must be a 400 with explicit wording.
    /// </summary>
    [Fact]
    public async Task TestConnection_Rejects_Empty_Key_Instead_Of_Blaming_The_Keyring()
    {
        foreach (var key in new[] { "", "   " })
        {
            var res = await _client.PostAsJsonAsync(
                "/api/accounts/test-connection", SampleRequest("test-conn-empty-key") with { AccountKey = key });

            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            var body = await res.Content.ReadAsStringAsync();
            Assert.Contains("AccountKey is required.", body);
            Assert.DoesNotContain("keyring", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Get_Missing_Returns_404()
    {
        var res = await _client.GetAsync("/api/accounts/999999");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Delete_Removes_Account()
    {
        var post = await _client.PostAsJsonAsync("/api/accounts", SampleRequest("del-test"));
        var created = await post.Content.ReadFromJsonAsync<AccountResponse>();

        var del = await _client.DeleteAsync($"/api/accounts/{created!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var get = await _client.GetAsync($"/api/accounts/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    /// <summary>
    /// An account still used by a backup must not be deletable. <c>BackupConfig.AccountId</c> has no foreign key constraint in the
    /// database, so deleting raises no error; it just leaves a pile of orphan configs pointing at nothing — and they only blow up
    /// on the next real run (the "Account {id} not found" in <c>BackupRunner</c>/<c>CheckRunner</c>/<c>RestoreRunner</c>). For a
    /// scheduled task that means failing at 3am and noticing the next day; restore is worse — you find out the config is broken exactly when you need the data back.
    /// </summary>
    [Fact]
    public async Task Delete_Is_Refused_While_A_Backup_Still_Uses_The_Account()
    {
        var post = await _client.PostAsJsonAsync("/api/accounts", SampleRequest("del-in-use"));
        var created = await post.Content.ReadFromJsonAsync<AccountResponse>();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BackupConfigs.Add(new BackupConfig
            {
                AccountId = created!.Id, ContainerName = "photos", Name = "Photos", LocalRoot = "/data/photos",
            });
            db.BackupConfigs.Add(new BackupConfig
            {
                AccountId = created.Id, ContainerName = "docs", Name = "Documents", LocalRoot = "/data/docs",
            });
            await db.SaveChangesAsync();
        }

        var del = await _client.DeleteAsync($"/api/accounts/{created!.Id}");
        Assert.Equal(HttpStatusCode.Conflict, del.StatusCode);

        // Name the backups holding it — a bare "cannot delete" leaves the user trawling through backups one by one to find the culprit.
        var body = await del.Content.ReadAsStringAsync();
        Assert.Contains("Documents", body);
        Assert.Contains("Photos", body);

        // And it really was not deleted.
        var get = await _client.GetAsync($"/api/accounts/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    /// <summary>Usage info must ship with the account, so the UI can disable the delete button and explain why at the moment it renders.</summary>
    [Fact]
    public async Task Get_Reports_Which_Backups_Use_The_Account()
    {
        var post = await _client.PostAsJsonAsync("/api/accounts", SampleRequest("usage-report"));
        var created = await post.Content.ReadFromJsonAsync<AccountResponse>();

        // A freshly created account is used by nobody.
        Assert.Empty(created!.UsedByBackups);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Inserted in reverse order on purpose: the response must come back sorted, otherwise the tooltip on the very same page reshuffles on every refresh.
            db.BackupConfigs.Add(new BackupConfig
            {
                AccountId = created.Id, ContainerName = "z", Name = "Zeta", LocalRoot = "/data/z",
            });
            db.BackupConfigs.Add(new BackupConfig
            {
                AccountId = created.Id, ContainerName = "a", Name = "Alpha", LocalRoot = "/data/a",
            });
            await db.SaveChangesAsync();
        }

        var get = await _client.GetAsync($"/api/accounts/{created.Id}");
        var fetched = await get.Content.ReadFromJsonAsync<AccountResponse>();
        Assert.Equal(["Alpha", "Zeta"], fetched!.UsedByBackups);

        // The list page goes through the other (batched) path and must carry it too.
        var list = await _client.GetFromJsonAsync<List<AccountResponse>>("/api/accounts");
        Assert.Equal(["Alpha", "Zeta"], list!.Single(a => a.Id == created.Id).UsedByBackups);
    }

    /// <summary>
    /// Connectivity test in edit mode: a blank Key box means "reuse the existing credentials", so this must not throw
    /// back a 400 the way the id-less endpoint does — "I changed the endpoint or the proxy and want to check the
    /// existing key still connects" is exactly what you most need to be able to do while editing.
    /// </summary>
    [Fact]
    public async Task TestConnection_For_An_Existing_Account_Accepts_A_Blank_Key()
    {
        var post = await _client.PostAsJsonAsync("/api/accounts", SampleRequest("test-conn-by-id"));
        var created = await post.Content.ReadFromJsonAsync<AccountResponse>();

        var res = await _client.PostAsJsonAsync(
            $"/api/accounts/{created!.Id}/test-connection", SampleRequest("test-conn-by-id") with { AccountKey = "" });

        // Whether it connects depends on this fake endpoint (it certainly will not), but it must **not** be the
        // "you did not enter a key" kind of rejection: getting this far proves the stored ciphertext was copied across correctly.
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("AccountKey is required.", body);
    }

    [Fact]
    public async Task TestConnection_For_A_Missing_Account_Returns_404()
    {
        var res = await _client.PostAsJsonAsync(
            "/api/accounts/999999/test-connection", SampleRequest() with { AccountKey = "" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Put_Updates_Name()
    {
        var post = await _client.PostAsJsonAsync("/api/accounts", SampleRequest("before"));
        var created = await post.Content.ReadFromJsonAsync<AccountResponse>();

        var update = SampleRequest("after") with { AccountKey = null }; // key unchanged
        var put = await _client.PutAsJsonAsync($"/api/accounts/{created!.Id}", update);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var get = await _client.GetAsync($"/api/accounts/{created.Id}");
        var fetched = await get.Content.ReadFromJsonAsync<AccountResponse>();
        Assert.Equal("after", fetched!.Name);
    }

    /// <summary>
    /// PUTting an account without changing AccountKey while the keyring is lost takes the "keep the old ciphertext" branch —
    /// and that ciphertext is precisely the one a lost keyring cannot decrypt. The response must honestly flag SecretsUnavailable=true,
    /// otherwise the UI looks perfectly fine while /api/system/keyring simultaneously counts it as pending, contradicting itself.
    /// </summary>
    [Fact]
    public async Task Put_While_Keyring_Lost_Reports_SecretsUnavailable_True()
    {
        var post = await _client.PostAsJsonAsync("/api/accounts", SampleRequest("keyring-lost-put"));
        var created = await post.Content.ReadFromJsonAsync<AccountResponse>();

        // /keys lost: replace the stored ciphertext with the output of a different keyring. The flag is decided by
        // per-record decryptability (design §3.3); flipping IKeyringHealth alone without touching the ciphertext is "keyring intact, status misconfigured" rather than a real loss.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Accounts.FirstAsync(a => a.Id == created!.Id)).AccountKeyProtected = TestSecrets.Stale("old-key");
            await db.SaveChangesAsync();
        }

        Keyring.Set(KeyringStatus.Lost);
        try
        {
            var update = SampleRequest("keyring-lost-put-renamed") with { AccountKey = null }; // blank, to take the keep-the-old-ciphertext branch
            var put = await _client.PutAsJsonAsync($"/api/accounts/{created!.Id}", update);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            var body = await put.Content.ReadFromJsonAsync<AccountResponse>();
            Assert.True(body!.SecretsUnavailable);
        }
        finally
        {
            Keyring.Set(KeyringStatus.Healthy);
        }
    }
}
