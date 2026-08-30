using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The local-root apply endpoint must HOLD the busy lock from before validation through the write, not
/// just glance at IsBusy once at the top: between that glance and ChangeLocalRootAsync sit several awaited
/// I/O steps (baseline load, directory sampling), and a scheduled backup that fires inside the window
/// acquires the lock unopposed — the root is then swapped under a run that is actively reading the old
/// directory, the exact rug-pull the endpoint's own comment forbids.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LocalRootBusyHoldTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string AzuriteEndpoint = "http://127.0.0.1:10000/devstoreaccount1";

    private readonly TestWebAppFactory _factory = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lrbh-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    /// <summary>Armed once, trips once: the next ChangeLocalRootAsync parks until released, exposing the
    /// window between the endpoint's validation pass and its write.</summary>
    private sealed class RootWriteGate
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

    private sealed class GatedConfigService(BackupConfigService inner, RootWriteGate gate) : IBackupConfigService
    {
        public Task<IReadOnlyList<BackupConfig>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public Task<BackupConfig?> GetAsync(int id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public Task<BackupConfig?> FindAsync(int accountId, string containerName, CancellationToken ct = default) =>
            inner.FindAsync(accountId, containerName, ct);
        public Task<BackupConfig> CreateAsync(BackupConfig config, CancellationToken ct = default) => inner.CreateAsync(config, ct);
        public Task<BackupConfig?> UpdateAsync(int id, BackupConfig update, CancellationToken ct = default) => inner.UpdateAsync(id, update, ct);
        public async Task<BackupConfig?> ChangeLocalRootAsync(int id, string newRoot, CancellationToken ct = default)
        {
            await gate.PassAsync();
            return await inner.ChangeLocalRootAsync(id, newRoot, ct);
        }
        public Task<bool> DeleteAsync(int id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
        public Task SetErrorAsync(int id, string message, CancellationToken ct = default) => inner.SetErrorAsync(id, message, ct);
        public Task SetNormalAsync(int id, CancellationToken ct = default) => inner.SetNormalAsync(id, ct);
        public Task ResetStatusAsync(int id, CancellationToken ct = default) => inner.ResetStatusAsync(id, ct);
    }

    [SkippableFact]
    public async Task Apply_Holds_The_Busy_Lock_Through_The_Root_Write()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var gate = new RootWriteGate();
        using var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton(gate);
            s.AddScoped<BackupConfigService>();
            var descriptor = s.Single(d => d.ServiceType == typeof(IBackupConfigService));
            s.Remove(descriptor);
            s.AddScoped<IBackupConfigService>(sp => new GatedConfigService(
                sp.GetRequiredService<BackupConfigService>(), sp.GetRequiredService<RootWriteGate>()));
        }));
        var client = host.CreateClient();

        var oldRoot = Path.Combine(_dir, "old");
        var newRoot = Path.Combine(_dir, "new");
        Directory.CreateDirectory(oldRoot);
        Directory.CreateDirectory(newRoot);

        var accountReq = new AccountRequest("azurite", null, AzuriteEndpoint, AzureRegion.Global,
            AzuriteKey, false, ProxyMode.Independent, null, null, null, null);
        var account = await (await client.PostAsJsonAsync("/api/accounts", accountReq))
            .Content.ReadFromJsonAsync<AccountResponse>();
        int configId;
        string container;
        using (var scope = host.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
            container = "lrbh-" + Guid.NewGuid().ToString("N")[..8];
            configId = (await svc.CreateAsync(new BackupConfig
            {
                AccountId = account!.Id,
                ContainerName = container,
                Name = "photos",
                LocalRoot = oldRoot,
                IndexTier = StorageTier.Hot,
                DataTier = StorageTier.Cool,
            })).Id;
        }

        gate.Arm();
        var post = client.PostAsJsonAsync($"/api/backup-configs/{configId}/local-root", new LocalRootChangeRequest(newRoot));
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60));

        var busy = host.Services.GetRequiredService<BackupBusyTracker>();
        try
        {
            // Validation is over, the write has not happened: a backup firing right now must find the
            // target locked, exactly as if any other operation were mid-flight.
            Assert.True(busy.IsBusy(account!.Id, container));
            Assert.False(busy.TryAcquire(account.Id, container, "BackingUp"));
        }
        finally
        {
            gate.Released.TrySetResult();
        }

        var response = await post;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(busy.IsBusy(account!.Id, container)); // and released once done

        using var verify = host.Services.CreateScope();
        var after = await verify.ServiceProvider.GetRequiredService<IBackupConfigService>().GetAsync(configId);
        Assert.Equal(newRoot, after!.LocalRoot);
    }

    [SkippableFact]
    public async Task Apply_Refuses_When_The_Target_Is_Already_Busy()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");

        var client = _factory.CreateClient();
        var root = Path.Combine(_dir, "busy-old");
        Directory.CreateDirectory(root);

        var accountReq = new AccountRequest("azurite2", null, AzuriteEndpoint, AzureRegion.Global,
            AzuriteKey, false, ProxyMode.Independent, null, null, null, null);
        var account = await (await client.PostAsJsonAsync("/api/accounts", accountReq))
            .Content.ReadFromJsonAsync<AccountResponse>();
        int configId;
        string container;
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<IBackupConfigService>();
            container = "lrbh2-" + Guid.NewGuid().ToString("N")[..8];
            configId = (await svc.CreateAsync(new BackupConfig
            {
                AccountId = account!.Id,
                ContainerName = container,
                Name = "photos",
                LocalRoot = root,
                IndexTier = StorageTier.Hot,
                DataTier = StorageTier.Cool,
            })).Id;
        }

        var busy = _factory.Services.GetRequiredService<BackupBusyTracker>();
        Assert.True(busy.TryAcquire(account!.Id, container, "BackingUp"));
        try
        {
            var response = await client.PostAsJsonAsync(
                $"/api/backup-configs/{configId}/local-root", new LocalRootChangeRequest(root));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        finally
        {
            busy.Release(account.Id, container);
        }
    }
}
