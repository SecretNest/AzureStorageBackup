using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The second half of "mark it and leave it to the next backup version" (§ the repair plan's deferral): after a
/// backup run finishes and the busy lock is back, the unrecoverable marks across the retained versions are
/// handed to a repair scoped to exactly those paths. Content that still matches its recorded identity is
/// re-uploaded in place — content addressing makes the healed blob every referencing version's blob at once —
/// and the marks come off; content that no longer matches never triggers at all (see
/// <see cref="HealCandidates"/>), so the loop converges instead of re-running a hopeless repair after every
/// backup forever.
/// <para>
/// Without this, a deferral at the plan would be a quiet lie: an unchanged file's damaged blob is content-
/// addressed, so no future backup would ever re-upload it on its own — diff sees "unchanged", dedup sees
/// "already in the cloud", and the damage outlives every version that inherits the reference.
/// </para>
/// </summary>
public sealed class DeferredRepairs(IServiceScopeFactory scopes, RepairRunner repairs, ILogger<DeferredRepairs>? logger = null)
{
    /// <summary>Fire-and-forget by design, called after the backup released the busy lock. Best-effort: a failure
    /// here must never turn a completed backup red — the marks stay, and the next completed backup tries again.</summary>
    public async Task TryStartAsync(int configId, CancellationToken ct = default)
    {
        try
        {
            // A suspended user repair outranks automation: the suspension is explicit intent, and its resume will
            // re-derive everything this trigger would have found. Skip, with a line saying so.
            if (await repairs.HasSuspendedAsync(configId, ct))
            {
                logger?.LogInformation(
                    "Deferred repair for config {ConfigId} skipped: a suspended user repair exists and takes precedence.", configId);
                return;
            }
            using var scope = scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var config = await sp.GetRequiredService<IBackupConfigService>().GetAsync(configId, ct);
            if (config is null)
                return;
            var account = await sp.GetRequiredService<IAccountService>().GetAsync(config.AccountId, ct);
            if (account is null)
                return;
            var password = sp.GetRequiredService<ISecretReader>().RevealBackupPassword(config);
            var trackedInfo = sp.GetRequiredService<TrackedInfoStore>();
            var indexCache = sp.GetRequiredService<ILocalIndexCache>();
            var info = await trackedInfo.LoadAsync(account, config.ContainerName, password, ct);
            if (info is null || info.Versions.Count == 0)
                return;

            var marked = new HashSet<string>(StringComparer.Ordinal);
            VersionIndex? latest = null;
            foreach (var v in info.Versions)
            {
                var idx = await indexCache.ReadAsync(
                    account, config.ContainerName, v.Version, info.Backup.CreatedAt.UtcTicks,
                    v.IndexBlob, password, v.IndexVolumes, ct);
                foreach (var p in idx.UnrecoverablePaths)
                    marked.Add(p);
                if (v.Version == info.Versions[^1].Version)
                    latest = idx;
            }
            if (marked.Count == 0 || latest is null)
                return;

            var candidates = HealCandidates(latest, marked, config.LocalRoot);
            if (candidates.Count == 0)
                return;

            repairs.Start(configId, version: null, CloudCheckLevel.ExistenceSize, rehydrate: null,
                cleanupOrphans: false, onlyPaths: candidates);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Deferred repair for config {ConfigId} could not start; the marks remain and the next backup tries again.", configId);
        }
    }

    /// <summary>
    /// Which marked paths this backup hands to the deferred repair. The filter is a stat, not a hash — its job is
    /// convergence, not verification (the repair's own hash gate verifies before anything is uploaded): a path
    /// whose local length matches its recorded length can heal, so it goes; one whose length differs (or is gone,
    /// or escapes the root — the import-oracle rule, as everywhere a cloud path meets the local root) cannot, and
    /// is skipped silently — otherwise every nightly backup would run a repair that re-marks the same unhealable
    /// file and pushes the same notification, forever.
    /// </summary>
    internal static List<string> HealCandidates(VersionIndex latest, IReadOnlySet<string> marked, string localRoot)
    {
        var candidates = new List<string>();
        foreach (var e in latest.Entries)
        {
            if (!marked.Contains(e.Path) || e.Storage is null || e.FullHash is null)
                continue;
            var local = Path.Combine(localRoot, e.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!PathBoundary.IsWithin(localRoot, local))
                continue;
            try
            {
                if (File.Exists(local) && new FileInfo(local).Length == e.Length)
                    candidates.Add(e.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable now → not a candidate now; the marks persist and a later backup retries.
            }
        }
        return candidates;
    }
}
