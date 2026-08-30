using System.Net;
using System.Net.Http.Json;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The check-then-act spans on the topology endpoints (delete a config, delete a container, delete an
/// account, create a config) each awaited I/O between the check and the write, and none of them held
/// anything that stops the raced counterpart from slipping through the gap: a run starting mid-config-delete,
/// a config created for a container mid-deletion, a config created under an account mid-deletion. Each test
/// here parks its endpoint inside the gap deterministically and drives the counterpart through it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TopologyGuardTests : IDisposable
{
    private readonly TestWebAppFactory _factory = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "asb-topo-" + Guid.NewGuid().ToString("N")[..8]);

    public TopologyGuardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static AccountRequest FakeAccountReq(string tag) => new("acct-" + tag, null,
        "https://t" + Guid.NewGuid().ToString("N")[..12] + ".blob.core.windows.net", AzureRegion.Global,
        "dGVzdGtleQ==", false, ProxyMode.Independent, null, null, null, null);

    private BackupConfigRequest ConfigReq(int accountId, string container, string tag) => new(
        accountId, container, tag, null, _dir,
        null, StorageTier.Hot, StorageTier.Hot, null, null, null, false,
        100, 180, RetentionMode.EitherTriggers, 5_000_000, 100_000_000);

    private sealed class Gate
    {
        private int _armed;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Interlocked.Exchange(ref _armed, 1);
        public async Task PassAsync()
        {
            if (Interlocked.Exchange(ref _armed, 0) != 1)
                return;
            Reached.TrySetResult();
            await Released.Task;
        }
    }

    private sealed class GatedConfigService(BackupConfigService inner, Gate gate) : IBackupConfigService
    {
        public Task<IReadOnlyList<BackupConfig>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public Task<BackupConfig?> GetAsync(int id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public Task<BackupConfig?> FindAsync(int accountId, string containerName, CancellationToken ct = default) => inner.FindAsync(accountId, containerName, ct);
        public Task<BackupConfig> CreateAsync(BackupConfig config, CancellationToken ct = default) => inner.CreateAsync(config, ct);
        public Task<BackupConfig?> UpdateAsync(int id, BackupConfig update, CancellationToken ct = default) => inner.UpdateAsync(id, update, ct);
        public Task<BackupConfig?> ChangeLocalRootAsync(int id, string newRoot, CancellationToken ct = default) => inner.ChangeLocalRootAsync(id, newRoot, ct);
        public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
        {
            await gate.PassAsync();
            return await inner.DeleteAsync(id, ct);
        }
        public Task SetErrorAsync(int id, string message, CancellationToken ct = default) => inner.SetErrorAsync(id, message, ct);
        public Task SetNormalAsync(int id, CancellationToken ct = default) => inner.SetNormalAsync(id, ct);
        public Task ResetStatusAsync(int id, CancellationToken ct = default) => inner.ResetStatusAsync(id, ct);
    }

    private sealed class GatedAccountService(AccountService inner, Gate gate) : IAccountService
    {
        public Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public Task<Account?> GetAsync(int id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public Task<Account> CreateAsync(Account account, CancellationToken ct = default) => inner.CreateAsync(account, ct);
        public Task<Account?> UpdateAsync(int id, Account update, CancellationToken ct = default) => inner.UpdateAsync(id, update, ct);
        public Task<bool> DeleteAsync(int id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
        public async Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> GetBackupUsageAsync(CancellationToken ct = default)
        {
            var usage = await inner.GetBackupUsageAsync(ct);
            await gate.PassAsync(); // park AFTER the snapshot: the classic check-then-act gap, held open
            return usage;
        }
    }

    private WebApplicationFactoryHost HostWith<TInner, TIface>(Gate gate, Func<TInner, Gate, TIface> wrap)
        where TInner : class where TIface : class
    {
        var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton(gate);
            s.AddScoped<TInner>();
            var descriptor = s.Single(d => d.ServiceType == typeof(TIface));
            s.Remove(descriptor);
            s.AddScoped(sp => wrap(sp.GetRequiredService<TInner>(), sp.GetRequiredService<Gate>()));
        }));
        return new WebApplicationFactoryHost(host);
    }

    /// <summary>Tiny disposal wrapper so the per-test derived hosts get cleaned up.</summary>
    private sealed class WebApplicationFactoryHost(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host) : IDisposable
    {
        public Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host => host;
        public void Dispose() => host.Dispose();
    }

    [Fact]
    public async Task Config_Delete_Holds_The_Busy_Lock_Through_The_Row_Removal()
    {
        var gate = new Gate();
        using var wrapped = HostWith<BackupConfigService, IBackupConfigService>(gate, (i, g) => new GatedConfigService(i, g));
        var client = wrapped.Host.CreateClient();

        var account = await (await client.PostAsJsonAsync("/api/accounts", FakeAccountReq("cfg-del")))
            .Content.ReadFromJsonAsync<AccountResponse>();
        var container = "topo-" + Guid.NewGuid().ToString("N")[..8];
        var config = await (await client.PostAsJsonAsync("/api/backup-configs", ConfigReq(account!.Id, container, "cfg-del")))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        gate.Arm();
        var delete = client.DeleteAsync($"/api/backup-configs/{config!.Id}");
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var busy = wrapped.Host.Services.GetRequiredService<BackupBusyTracker>();
        try
        {
            // The idle check passed and the row is about to go: a run firing right now must find the
            // target locked, or it starts against a config that is half-deleted.
            Assert.True(busy.IsBusy(account.Id, container));
            Assert.False(busy.TryAcquire(account.Id, container, "BackingUp"));
        }
        finally
        {
            gate.Released.TrySetResult();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await delete).StatusCode);
        Assert.False(busy.IsBusy(account.Id, container));
    }

    [Fact]
    public async Task Config_Create_Is_Refused_While_The_Container_Is_Being_Deleted()
    {
        var client = _factory.CreateClient();
        var account = await (await client.PostAsJsonAsync("/api/accounts", FakeAccountReq("cfg-create")))
            .Content.ReadFromJsonAsync<AccountResponse>();
        var container = "topo2-" + Guid.NewGuid().ToString("N")[..8];

        var busy = _factory.Services.GetRequiredService<BackupBusyTracker>();
        Assert.True(busy.TryAcquire(account!.Id, container, "Deleting")); // a container deletion mid-flight
        try
        {
            var response = await client.PostAsJsonAsync("/api/backup-configs", ConfigReq(account.Id, container, "cfg-create"));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        finally
        {
            busy.Release(account.Id, container);
        }

        // And once the deletion is over, creation goes through.
        var after = await client.PostAsJsonAsync("/api/backup-configs", ConfigReq(account.Id, container, "cfg-create"));
        Assert.Equal(HttpStatusCode.Created, after.StatusCode);
    }

    [Fact]
    public async Task Container_Delete_Is_Refused_While_The_Container_Is_Otherwise_Held()
    {
        var client = _factory.CreateClient();
        var account = await (await client.PostAsJsonAsync("/api/accounts", FakeAccountReq("cont-del")))
            .Content.ReadFromJsonAsync<AccountResponse>();
        const string container = "topo3-held";

        var busy = _factory.Services.GetRequiredService<BackupBusyTracker>();
        Assert.True(busy.TryAcquire(account!.Id, container, "Creating")); // a config creation mid-flight
        try
        {
            var response = await client.DeleteAsync($"/api/accounts/{account.Id}/containers/{container}");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        finally
        {
            busy.Release(account.Id, container);
        }
    }

    [Fact]
    public async Task Account_Delete_Cannot_Interleave_With_A_Config_Creation_Under_It()
    {
        var gate = new Gate();
        using var wrapped = HostWith<AccountService, IAccountService>(gate, (i, g) => new GatedAccountService(i, g));
        var client = wrapped.Host.CreateClient();

        var account = await (await client.PostAsJsonAsync("/api/accounts", FakeAccountReq("acct-del")))
            .Content.ReadFromJsonAsync<AccountResponse>();

        gate.Arm();
        var delete = client.DeleteAsync($"/api/accounts/{account!.Id}");
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The delete has read "no backups use this account" and is parked before removing the row. A config
        // creation racing in right now must NOT be able to commit a row referencing the account — either it
        // waits for the delete and then gets "Account not found", or the delete must fail. What may never
        // happen is both succeeding.
        var container = "topo4-" + Guid.NewGuid().ToString("N")[..8];
        var create = client.PostAsJsonAsync("/api/backup-configs", ConfigReq(account.Id, container, "acct-del"));
        await Task.Delay(500); // give the create every chance to slip through the parked gap
        gate.Released.TrySetResult();

        var deleteResponse = await delete;
        var createResponse = await create;

        using var scope = wrapped.Host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var accountExists = await db.Accounts.AsNoTracking().AnyAsync(a => a.Id == account.Id);
        var orphanConfig = await db.BackupConfigs.AsNoTracking().AnyAsync(c => c.AccountId == account.Id);
        Assert.False(orphanConfig && !accountExists,
            $"orphan config created: delete={deleteResponse.StatusCode}, create={createResponse.StatusCode}");
    }
}
