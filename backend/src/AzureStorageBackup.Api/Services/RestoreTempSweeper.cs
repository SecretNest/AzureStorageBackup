using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Startup sweep of <see cref="RestoreOrchestrator.TempCopyPrefix"/> — the cloud directory holding the Hot
/// copies a restore makes of archived volumes (the copies exist so the archived ORIGINALS are never touched;
/// see EnsureHotCopyAsync). The prefix is the whole bookkeeping: a restore deletes its own copies when a group
/// finishes, so anything still under the prefix at process start is an orphan of a crash — or of a restore run
/// against this container from another device — and every hour it survives is billed Hot storage for nothing.
/// The detection is exactly what it looks like: is there anything in the directory ("看这个目录是否存在/有没有文件").
/// <para>
/// Runs once, in the background, after startup; failures are logged and skipped (the next start retries).
/// "Restores begin after startup" is NOT a defense here: StartAsync detaches the sweep and returns at once,
/// and Kestrel — registered ahead of this service — is already serving /restore while the listing is still
/// under way, so a restart followed immediately by an archive restore would race its fresh Hot copy against
/// these deletes. Hence the per-container gate below: the sweep holds "CleaningUp" (refused while a reader
/// is active, refusing new readers while held) for exactly the span of one container's sweep, and a container
/// it cannot hold is skipped — its leftovers wait for the next start, billed but safe.
/// </para>
/// </summary>
public sealed class RestoreTempSweeper(
    IServiceScopeFactory scopes, IBlobClientFactory factory, ILogger<RestoreTempSweeper>? logger = null,
    BackupBusyTracker? busy = null)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => SweepAllAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task SweepAllAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
            var configs = await db.BackupConfigs.AsNoTracking()
                .Select(c => new { c.AccountId, c.ContainerName }).Distinct().ToListAsync(ct);
            var accounts = (await db.Accounts.AsNoTracking().ToListAsync(ct)).ToDictionary(a => a.Id);
            foreach (var c in configs)
            {
                if (!accounts.TryGetValue(c.AccountId, out var account))
                    continue;
                // The gate (see the class doc): hold the container as a rewriter or leave it alone entirely.
                // refuseWhenReaders is the whole point — a restore's temp copies are exactly what this deletes.
                // (Null busy = the direct-construction test path; production DI always passes the tracker.)
                if (busy is not null && !busy.TryAcquire(account.Id, c.ContainerName, "CleaningUp", refuseWhenReaders: true))
                {
                    logger?.LogDebug("Restore temp sweep skipped for busy {Container} (next start retries)", c.ContainerName);
                    continue;
                }
                try
                {
                    var cc = factory.CreateServiceClient(account).GetBlobContainerClient(c.ContainerName);
                    var swept = 0;
                    await foreach (var b in cc.GetBlobsAsync(
                        Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None,
                        RestoreOrchestrator.TempCopyPrefix, ct))
                    {
                        if ((await cc.GetBlobClient(b.Name).DeleteIfExistsAsync(cancellationToken: ct)).Value)
                            swept++;
                    }
                    if (swept > 0)
                        logger?.LogWarning(
                            "Swept {Count} leftover restore temp copies from {Container} (a crashed or foreign restore left them billing in the online tier)",
                            swept, c.ContainerName);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // An unreachable container must not block the sweep of the others (or startup).
                    logger?.LogDebug(ex, "Restore temp sweep skipped for {Container}", c.ContainerName);
                }
                finally
                {
                    // Reaching the try at all means the gate above was taken (or there is no tracker).
                    busy?.Release(account.Id, c.ContainerName);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogDebug(ex, "Restore temp sweep did not run");
        }
    }
}
