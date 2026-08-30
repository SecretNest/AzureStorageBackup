using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// A completed check must persist its report (the check/repair gate) BEFORE the run becomes visible as
/// Completed and before the busy lock is released. Otherwise there is a window in which the UI already
/// shows the result, DropAsync's only guard (Status == Running) no longer refuses, and a Drop that lands
/// inside the window finds no row — after which the late PersistAsync resurrects the report the user just
/// dismissed. RepairRunner already writes its rows before releasing busy; this pins the same ordering for
/// checks.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CheckRunSettleOrderingTests : IDisposable
{
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string AzuriteEndpoint = "http://127.0.0.1:10000/devstoreaccount1";

    private readonly TestWebAppFactory _factory = new();
    private readonly string _localRoot = Path.Combine(Path.GetTempPath(), "asb-ckord-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_localRoot, recursive: true); } catch { /* best effort */ }
    }

    private static bool AzuriteReachable()
    {
        try { using var c = new TcpClient(); c.Connect("127.0.0.1", 10000); return true; }
        catch { return false; }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;

    /// <summary>Armed once, trips once: the next SetNormalAsync parks until the test releases it, exposing
    /// the exact instant between "the check finished computing" and "its report row landed".</summary>
    private sealed class StatusWriteGate
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

    private sealed class GatedConfigService(BackupConfigService inner, StatusWriteGate gate) : IBackupConfigService
    {
        public Task<IReadOnlyList<BackupConfig>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public Task<BackupConfig?> GetAsync(int id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public Task<BackupConfig?> FindAsync(int accountId, string containerName, CancellationToken ct = default) =>
            inner.FindAsync(accountId, containerName, ct);
        public Task<BackupConfig> CreateAsync(BackupConfig config, CancellationToken ct = default) => inner.CreateAsync(config, ct);
        public Task<BackupConfig?> UpdateAsync(int id, BackupConfig update, CancellationToken ct = default) => inner.UpdateAsync(id, update, ct);
        public Task<BackupConfig?> ChangeLocalRootAsync(int id, string newRoot, CancellationToken ct = default) =>
            inner.ChangeLocalRootAsync(id, newRoot, ct);
        public Task<bool> DeleteAsync(int id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
        public Task SetErrorAsync(int id, string message, CancellationToken ct = default) => inner.SetErrorAsync(id, message, ct);
        public async Task SetNormalAsync(int id, CancellationToken ct = default)
        {
            await gate.PassAsync();
            await inner.SetNormalAsync(id, ct);
        }
        public Task ResetStatusAsync(int id, CancellationToken ct = default) => inner.ResetStatusAsync(id, ct);
    }

    [SkippableFact]
    public async Task Check_Persists_Its_Report_Before_Leaving_The_Running_State()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        Skip.IfNot(SevenZip(), "7z not found");

        var gate = new StatusWriteGate();
        using var host = _factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton(gate);
            s.AddScoped<BackupConfigService>();
            var descriptor = s.Single(d => d.ServiceType == typeof(IBackupConfigService));
            s.Remove(descriptor);
            s.AddScoped<IBackupConfigService>(sp => new GatedConfigService(
                sp.GetRequiredService<BackupConfigService>(), sp.GetRequiredService<StatusWriteGate>()));
        }));
        var client = host.CreateClient();

        Directory.CreateDirectory(_localRoot);
        await File.WriteAllTextAsync(Path.Combine(_localRoot, "a.txt"), "alpha");
        var containerName = "ckord-" + Guid.NewGuid().ToString("N")[..8];

        var accountReq = new AccountRequest("azurite", null, AzuriteEndpoint, AzureRegion.Global,
            AzuriteKey, false, ProxyMode.Independent, null, null, null, null);
        var account = await (await client.PostAsJsonAsync("/api/accounts", accountReq))
            .Content.ReadFromJsonAsync<AccountResponse>();
        var configReq = new BackupConfigRequest(account!.Id, containerName, "ckord", null, _localRoot,
            null, StorageTier.Hot, StorageTier.Hot, null, null, null, false,
            100, 180, RetentionMode.EitherTriggers, 5_000_000, 100_000_000);
        var config = await (await client.PostAsJsonAsync("/api/backup-configs", configReq))
            .Content.ReadFromJsonAsync<BackupConfigResponse>();

        var containerClient = new BlobClientFactory(TestSecrets.Reader)
            .CreateServiceClient(new Account { BlobEndpoint = AzuriteEndpoint, AccountKeyProtected = TestSecrets.Protect(AzuriteKey), Region = AzureRegion.Global })
            .GetBlobContainerClient(containerName);
        try
        {
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/api/backup-configs/{config!.Id}/run", null)).StatusCode);
            await PollAsync(async () =>
                (await (await client.GetAsync($"/api/backup-configs/{config.Id}/run"))
                    .Content.ReadFromJsonAsync<BackupRunResponse>())!.Status == "Completed");

            var busy = host.Services.GetRequiredService<BackupBusyTracker>();
            await PollAsync(() => Task.FromResult(!busy.IsBusy(account.Id, containerName)));
            await Task.Delay(500); // let the backup's own trailing status write clear the gate's path

            gate.Arm();
            var runner = host.Services.GetRequiredService<CheckRunner>();
            runner.Start(config.Id, version: null, new CheckOptions { Cloud = CloudCheckLevel.ExistenceSize, Local = LocalCheckLevel.None });
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60));

            try
            {
                // The check has finished computing but its report row has not landed: to every observer this
                // run must still be a live, busy, undroppable Running run.
                Assert.Equal(RunStatus.Running, runner.Get(config.Id)!.Status);
                Assert.True(busy.IsBusy(account.Id, containerName));
                Assert.False(await runner.DropAsync(config.Id));
            }
            finally
            {
                gate.Released.TrySetResult();
            }

            await PollAsync(() => Task.FromResult(runner.Get(config.Id)?.Status != RunStatus.Running));
            Assert.Equal(RunStatus.Completed, runner.Get(config.Id)!.Status);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.LastCheckRuns.AsNoTracking().SingleAsync(x => x.BackupConfigId == config.Id);
            Assert.Equal(CheckResolution.Clean, row.Resolution);

            // And the refused Drop repeated after completion behaves normally: dismissed for good.
            Assert.True(await runner.DropAsync(config.Id));
            Assert.False(await db.LastCheckRuns.AsNoTracking().AnyAsync(x => x.BackupConfigId == config.Id));
        }
        finally
        {
            gate.Released.TrySetResult(); // never leave the run parked on a failed assertion
            await containerClient.DeleteIfExistsAsync();
        }
    }

    private static async Task PollAsync(Func<Task<bool>> done)
    {
        for (var i = 0; i < 600; i++)
        {
            if (await done()) return;
            await Task.Delay(200);
        }
        Assert.Fail("Timed out waiting for the polled condition.");
    }
}
