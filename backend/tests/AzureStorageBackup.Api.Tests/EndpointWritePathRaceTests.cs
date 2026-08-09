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
/// The two reset endpoints that "check existence, verify against the cloud, then write to the database" (F2/F3). Verification
/// goes to the cloud, so the window between the check and the write is not short: if the row is deleted in the meantime it must
/// come back 404 (as everywhere else in the repo), not the 500 FirstAsync throws; cancellation (client disconnect / process
/// shutdown) must propagate as-is instead of being wrapped into a "verification failed" 400 (convention set in a3ac967).
/// <para>
/// Here the cloud step is replaced by a stub: deleting the row / throwing cancellation happens inside the stub, landing exactly inside that window, and no Azurite is needed.
/// </para>
/// </summary>
public sealed class EndpointWritePathRaceTests
{
    /// <summary>Replace a few more services (stubs) on top of the base test host.</summary>
    private sealed class StubbedFactory(Action<IServiceCollection> configure) : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(configure); // runs after Program.cs's registrations, so it can override them
        }
    }

    /// <summary>Throws cancellation the moment it is asked to create a client — used to inject OperationCanceledException into BackupChecker's cloud call.</summary>
    private sealed class CancelsOnCreateServiceClient : IBlobClientFactory
    {
        public BlobServiceClient CreateServiceClient(Account account) => throw new OperationCanceledException();

        public Task<ConnectionResult> TestConnectionAsync(Account account, CancellationToken ct = default)
            => throw new OperationCanceledException();
    }

    /// <summary>The connectivity test "passes", but deletes the account row before returning — an exact reproduction of the delete race between successful verification and the write.</summary>
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

    /// <summary>Stubs only <see cref="IBackupInfoStore.ReadInfoWithETagAsync"/> (the one method reset-password uses).</summary>
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

    /// <summary>The second of the delete-config cleanup steps: throws cancellation the moment it is called. These cases should not reach the other methods.</summary>
    private sealed class CancelsOnEvictIndexCache : ILocalIndexCache
    {
        public Task<VersionIndex> ReadAsync(
            Account account, string container, int version, long identityTicks,
            string indexBlob, string? password, CancellationToken ct = default) => throw new NotSupportedException();

        public Task PutAsync(int accountId, string container, int version, long identityTicks, VersionIndex index, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RemoveAsync(int accountId, string container, int version, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RemoveForContainerAsync(int accountId, string container, CancellationToken ct = default)
            => throw new OperationCanceledException();
    }

    /// <summary>The third of the delete-config cleanup steps: only records whether it was called.</summary>
    private sealed class RecordingStateStore : ILocalBackupStateStore
    {
        public bool Removed { get; private set; }

        public Task<(byte[] InfoBytes, string ETag)?> TryGetAsync(int accountId, string container, CancellationToken ct = default)
            => Task.FromResult<(byte[], string)?>(null);

        public Task PutAsync(int accountId, string container, byte[] infoBytes, string etag, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RemoveAsync(int accountId, string container, CancellationToken ct = default)
        {
            Removed = true;
            return Task.CompletedTask;
        }
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
    /// F2: POST /api/accounts/{id}/reset-secrets. The account row is deleted after verification passes → 404, not FirstAsync's 500.
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
    /// F2: POST /api/backup-configs/{id}/reset-password. The config row is deleted after verification passes → 404.
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
                // Delete the config row at the very moment verification "succeeds" — right inside the window between the check and the write.
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
    /// F3: cancellation during verification (client disconnect / process shutdown) is not "wrong password".
    /// Before the fix, catch (Exception) turned it into a 400 "Verification failed: The operation was canceled.".
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

        // Cancellation propagates all the way up instead of being disguised as the user getting the password wrong. Before
        // the fix this was 400 + "Verification failed: The operation was canceled.".
        // Deliberately not asserting that the exception type name shows up in the body — that only holds while the developer
        // exception page is in the pipeline (it is absent under ASPNETCORE_ENVIRONMENT=Production) and is unrelated to the behavior under test.
        Assert.NotEqual(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.DoesNotContain("Verification failed", body);

        // The ciphertext is untouched: nothing was persisted, and nothing was treated as "verification passed".
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var encryption = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
        var row = await db.BackupConfigs.AsNoTracking().FirstAsync(c => c.Id == config!.Id);
        Assert.Equal("initial-password", TestSecrets.Reveal(encryption, row.PasswordProtected!));
    }

    /// <summary>
    /// The other F3 site, and the only one with a **persistent side effect**: the catch in POST /api/backup-configs/{id}/check
    /// writes the exception message as that backup's Error status. Cancellation (client disconnect / process shutdown) is not
    /// "the check failed"; before the fix it left Status=Error + LastError="The operation was canceled." on the config row,
    /// and from then on the UI showed a failure that never happened, until someone reset it by hand.
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

        // Now that check runs as a background job, cancellation no longer shows up in the status code (what we get here is
        // 202 "accepted") but in the run state. So we have to wait for this run to actually finish before looking at the config
        // row — reading the database the moment POST returns is racing the background task: read fast enough and nothing has
        // been written yet, the test goes green by accident, and it hides the very write we are guarding against.
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);

        CheckRunResponse? run = null;
        for (var i = 0; i < 200; i++)
        {
            run = await (await client.GetAsync($"/api/backup-configs/{config.Id}/check"))
                .Content.ReadFromJsonAsync<CheckRunResponse>();
            if (run!.Status != "Running") break;
            await Task.Delay(25);
        }
        // Canceled is canceled: neither "check completed" nor "check failed".
        Assert.Equal("Canceled", run!.Status);
        Assert.Null(run.Report);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.BackupConfigs.AsNoTracking().FirstAsync(c => c.Id == config.Id);

        Assert.Equal(BackupStatus.Normal, row.Status);
        Assert.Null(row.LastError);
        Assert.Null(row.LastErrorAt);
    }

    /// <summary>
    /// The third F3 site: the catch in POST /api/backup-configs/import reports every "could not read the info file" as a
    /// 400 "wrong password?". Cancellation (client disconnect / process shutdown) is not a wrong password — disguise it as a
    /// user error and the user will keep re-typing a password that was correct all along. Before the fix, catch (Exception) swallowed it.
    /// </summary>
    [Fact]
    public async Task Import_Does_Not_Swallow_Cancellation_As_A_Wrong_Password()
    {
        using var factory = new StubbedFactory(services =>
        {
            services.RemoveAll<IBackupInfoStore>();
            services.AddScoped<IBackupInfoStore>(_ => new StubInfoStore(
                () => throw new OperationCanceledException()));
        });
        var client = factory.CreateClient();

        var account = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount("cancel-import")))
            .Content.ReadFromJsonAsync<AccountResponse>();
        Assert.NotNull(account);

        var res = await client.PostAsJsonAsync(
            "/api/backup-configs/import", new ImportRequest(account!.Id, "import-container", "the-real-password"));
        var body = await res.Content.ReadAsStringAsync();

        // Cancellation propagates all the way up (the host renders it as a 500) instead of being caught by one of the
        // endpoint's catches and wrapped into any kind of "handled" response (every Results.XXX branch of this endpoint —
        // 400/404/201 — returns application/json). Asserting NotEqual(BadRequest) alone is not enough: another branch
        // swallowing cancellation into some other non-400 status (a bogus 404, say) would slip through just as easily.
        // The criterion is Content-Type rather than the status code or body text: whatever the host renders an unhandled
        // exception as (the developer exception page only exists under Development — that middleware is absent under
        // ASPNETCORE_ENVIRONMENT=Production), as long as it is not the application/json this endpoint writes itself, that is
        // proof enough that cancellation was not turned into a "handled" request result.
        Assert.NotEqual(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.NotEqual("application/json", res.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("wrong password", body, StringComparison.OrdinalIgnoreCase);

        // And no config row was created halfway through either.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.BackupConfigs.AsNoTracking().AnyAsync(c => c.ContainerName == "import-container"));
    }

    /// <summary>
    /// The fourth F3 site: the cleanup steps of DELETE /api/backup-configs/{id} run BestEffort (swallow the exception, log a
    /// Warning, one failed step does not block the rest). Cancellation is the exception — it does not mean "this step failed"
    /// but "the whole request should stop"; swallowing it logs one misleading Warning per remaining step and reports a
    /// canceled request as a 204 success.
    /// <para>Before the fix (catch (Exception) not excluding cancellation): step two was swallowed, step three ran as usual, and the endpoint returned 204.</para>
    /// </summary>
    [Fact]
    public async Task Delete_Does_Not_Swallow_Cancellation_In_The_Best_Effort_Cleanup()
    {
        var stateStore = new RecordingStateStore();
        using var factory = new StubbedFactory(services =>
        {
            services.RemoveAll<ILocalIndexCache>();
            services.AddScoped<ILocalIndexCache, CancelsOnEvictIndexCache>();
            services.RemoveAll<ILocalBackupStateStore>();
            services.AddScoped<ILocalBackupStateStore>(_ => stateStore);
        });
        var client = factory.CreateClient();

        var account = await (await client.PostAsJsonAsync("/api/accounts", SampleAccount("cancel-delete")))
            .Content.ReadFromJsonAsync<AccountResponse>();
        var config = await (await client.PostAsJsonAsync("/api/backup-configs", SampleConfig(account!.Id, "pw")))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();
        Assert.NotNull(config);

        var res = await client.DeleteAsync($"/api/backup-configs/{config!.Id}");

        // Cancellation propagates as-is (the host renders it as a 500), not a 204 "deleted cleanly".
        Assert.NotEqual(HttpStatusCode.NoContent, res.StatusCode);
        // And **the later steps did not keep running** — that is precisely the observable line between "swallow cancellation"
        // and "let cancellation through": if it were swallowed, step three (clearing the local authoritative state) would have run as usual.
        Assert.False(stateStore.Removed);
    }
}
