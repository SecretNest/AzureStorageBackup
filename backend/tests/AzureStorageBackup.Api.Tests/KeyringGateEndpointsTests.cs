using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Task 5 review Finding 1/3: while the keyring is lost, the three triggers that really change state or depend on credentials
/// (/run, /restore, /repair) must 409 right at the entrance, while the read-only list endpoints must stay reachable
/// (otherwise the whole "recovery mode" feature is pointless). KeyringGuardTests only exercises the static method itself;
/// this drives real HTTP requests, so forgetting to wire up the gate cannot leave the suite green.
/// </summary>
public class KeyringGateEndpointsTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record KeyringLostError(string error, string code);

    private static BackupConfigRequest SampleRequest(string name, int accountId) => new(
        AccountId: accountId,
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

    /// <summary>Create a real account, for config creation that has to get past the "Account not found." gate.</summary>
    private async Task<int> CreateAccountAsync(string name)
    {
        var req = new AccountRequest(
            Name: "acct-" + name + "-" + Guid.NewGuid().ToString("N")[..6], Description: null,
            BlobEndpoint: "https://example.blob.core.windows.net", Region: AzureRegion.Global,
            AccountKey: "dGVzdGtleQ==", UseProxy: false, ProxyMode: ProxyMode.Independent,
            ProxyHost: null, ProxyPort: null, ProxyUsername: null, ProxyPassword: null);
        var res = await _client.PostAsJsonAsync("/api/accounts", req);
        return (await res.Content.ReadFromJsonAsync<AccountResponse>())!.Id;
    }

    private async Task<BackupConfigResponse> CreateConfigAsync(string name)
    {
        var accountId = await CreateAccountAsync(name);
        return (await (await _client.PostAsJsonAsync("/api/backup-configs", SampleRequest(name, accountId)))
            .Content.ReadFromJsonAsync<BackupConfigResponse>())!;
    }

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

    /// <summary>
    /// All-branch review Finding 2: manually triggering a scheduled task ("Run now") is one of the entrances to backup/check/cleanup,
    /// exactly like /backup-configs/{id}/run. Without a gate, decrypting the backup password inside the dispatcher throws, the throw is swallowed into a log line,
    /// and the endpoint still advances LastRunAt and returns 200 — the UI shows success while nothing whatsoever happened.
    /// It must be 409, and LastRunAt must stay where it was.
    /// </summary>
    [Fact]
    public async Task Manual_Task_Run_Returns_409_KeyringLost_And_Does_Not_Advance_LastRunAt()
    {
        var task = (await (await _client.PostAsJsonAsync("/api/tasks", new TaskRequest(
                TaskTargetKind.Backup, 1, "gate-task-run-container", null,
                ScheduledTaskType.Backup, "0 3 * * *", true)))
            .Content.ReadFromJsonAsync<TaskResponse>())!;
        Assert.Null(task.LastRunAt);

        Keyring.Set(KeyringStatus.Lost);
        try
        {
            var res = await _client.PostAsync($"/api/tasks/{task.Id}/run", null);
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Equal("keyring_lost", (await res.Content.ReadFromJsonAsync<KeyringLostError>())!.code);
        }
        finally
        {
            Keyring.Set(KeyringStatus.Healthy);
        }

        var after = await (await _client.GetAsync($"/api/tasks/{task.Id}"))
            .Content.ReadFromJsonAsync<TaskResponse>();
        Assert.Null(after!.LastRunAt);
    }

    /// <summary>
    /// All-branch review Finding 4/5: listing containers is named in design §3.1 as an action that needs credentials,
    /// and the delete branch that also drops the cloud container needs the account key just the same. Neither had a gate, so with the keyring lost
    /// both ran all the way down to SecretReader throwing, and the client got a bare 500. Both must be 409 keyring_lost.
    /// </summary>
    [Fact]
    public async Task Container_Listing_And_Cloud_Deleting_Delete_Return_409_KeyringLost()
    {
        var account = (await (await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
                "gate-containers", null, "https://gate.blob.core.windows.net", AzureRegion.Global,
                "dGVzdGtleQ==", false, ProxyMode.Independent, null, null, null, null)))
            .Content.ReadFromJsonAsync<AccountResponse>())!;
        var config = await CreateConfigAsync("gate-delete-container");

        Keyring.Set(KeyringStatus.Lost);
        try
        {
            var list = await _client.GetAsync($"/api/accounts/{account.Id}/containers");
            Assert.Equal(HttpStatusCode.Conflict, list.StatusCode);
            Assert.Equal("keyring_lost", (await list.Content.ReadFromJsonAsync<KeyringLostError>())!.code);

            var create = await _client.PostAsJsonAsync($"/api/accounts/{account.Id}/containers", new { name = "x" });
            Assert.Equal(HttpStatusCode.Conflict, create.StatusCode);

            var dropContainer = await _client.DeleteAsync($"/api/accounts/{account.Id}/containers/x");
            Assert.Equal(HttpStatusCode.Conflict, dropContainer.StatusCode);

            var dropCloud = await _client.DeleteAsync($"/api/backup-configs/{config.Id}?deleteContainer=true");
            Assert.Equal(HttpStatusCode.Conflict, dropCloud.StatusCode);
            Assert.Equal("keyring_lost", (await dropCloud.Content.ReadFromJsonAsync<KeyringLostError>())!.code);

            // Local-only delete must still be allowed: under decision 6 it is the only way out when you cannot remember the backup password.
            var dropLocal = await _client.DeleteAsync($"/api/backup-configs/{config.Id}");
            Assert.Equal(HttpStatusCode.NoContent, dropLocal.StatusCode);
        }
        finally
        {
            Keyring.Set(KeyringStatus.Healthy);
        }
    }

    /// <summary>
    /// Defence in depth (design §3.1): the keyring is swapped out while the process is running and the canary has not been re-evaluated,
    /// so the global status is still Healthy, the gate lets the request through, and decryption only fails at the choke point. Without the mapping the client gets a bare 500
    /// (Program.cs registers no exception-handling middleware at all). It must still be 409 keyring_lost.
    /// This also separates the gate from the mapping: the status is Healthy, so KeyringGuard never fires.
    /// </summary>
    [Fact]
    public async Task Undecryptable_Secret_Maps_To_409_Even_While_Status_Is_Healthy()
    {
        var account = (await (await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
                "gate-deep-defence", null, "https://deep.blob.core.windows.net", AzureRegion.Global,
                "dGVzdGtleQ==", false, ProxyMode.Independent, null, null, null, null)))
            .Content.ReadFromJsonAsync<AccountResponse>())!;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Accounts.FirstAsync(a => a.Id == account.Id)).AccountKeyProtected = TestSecrets.Stale("swapped-out");
            await db.SaveChangesAsync();
        }

        Assert.Equal(KeyringStatus.Healthy, Keyring.Status);

        var res = await _client.GetAsync($"/api/accounts/{account.Id}/containers");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("keyring_lost", (await res.Content.ReadFromJsonAsync<KeyringLostError>())!.code);
    }

    /// <summary>
    /// All-branch review Finding 5: the broad catch in /import reports "cannot read the account key" as
    /// "Could not read info file (wrong password?)", blaming a keyring problem on the password the user typed.
    /// The message must instead point at the account credentials.
    /// </summary>
    [Fact]
    public async Task Import_Blames_Account_Credentials_Not_The_Password_When_The_Key_Is_Undecryptable()
    {
        var account = (await (await _client.PostAsJsonAsync("/api/accounts", new AccountRequest(
                "gate-import", null, "https://import.blob.core.windows.net", AzureRegion.Global,
                "dGVzdGtleQ==", false, ProxyMode.Independent, null, null, null, null)))
            .Content.ReadFromJsonAsync<AccountResponse>())!;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.Accounts.FirstAsync(a => a.Id == account.Id)).AccountKeyProtected = TestSecrets.Stale("lost-key");
            await db.SaveChangesAsync();
        }

        var res = await _client.PostAsJsonAsync("/api/backup-configs/import",
            new ImportRequest(account.Id, "gate-import-container", "some-password"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("Re-enter this account's credentials first.", body!["error"]);
    }

    /// <summary>The entire point of recovery mode: even with the keyring lost, read-only list endpoints must not 409 along with everything else.</summary>
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
