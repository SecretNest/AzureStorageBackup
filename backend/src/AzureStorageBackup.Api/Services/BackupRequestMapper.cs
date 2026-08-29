using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The two packing limits that are set **per machine** (not per backup), supplied by environment variables: <c>Backup__MaxPackMembers</c> and
/// <c>Backup__MaxPackPathBytes</c>. The third one, <c>GroupCapBytes</c>, is a per-backup setting and does not belong here.
/// <para>
/// Why per machine: these two constrain **the memory and argv limit of the 7z process on this machine**,
/// which has nothing to do with "how big this backup wants its packs to be". Move the same config to another machine and the right values may be completely different.
/// </para>
/// </summary>
public sealed record PackLimits(int MaxPackMembers = 20_000, long MaxPackPathBytes = 1_000_000)
{
    public static readonly PackLimits Default = new();
}

/// <summary>Maps the persisted BackupConfig to the engine's BackupRequest (shared by BackupRunner and the scheduler).</summary>
public static class BackupRequestMapper
{
    /// <summary>
    /// <paramref name="password"/> is the **plaintext**, obtained by the caller via ISecretReader.RevealBackupPassword
    /// (design §3.1: decryption happens only at the chokepoint; the mapper is static and cannot get hold of ISecretReader).
    /// </summary>
    public static BackupRequest From(
        BackupConfig config, Account account, string? password, GlobalSettings? settings = null,
        PackLimits? packLimits = null)
    {
        var r = ResolvedBackupSettings.From(config, settings);
        var limits = packLimits ?? PackLimits.Default;
        return new BackupRequest
        {
            Account = account,
            Container = config.ContainerName,
            LocalRoot = config.LocalRoot,
            Name = config.Name,
            Description = config.Description,
            Password = password,
            IndexTier = MapTier(config.IndexTier),
            DataTier = MapTier(config.DataTier),
            Options = new BackupEngineOptions
            {
                Ignore = Rules(r.IgnoreRules, r.IgnoreRulesCaseInsensitive) ?? new IgnoreRuleSet([]),
                DontCompress = r.DontCompress(),
                DontGroup = Rules(r.DontGroupRules, r.DontGroupRulesCaseInsensitive),
                CrossDirGroup = Rules(r.CrossDirGroupRules, r.CrossDirGroupRulesCaseInsensitive),
                // ScopeRules is not inheritable, so take it straight from config rather than from r (ResolvedBackupSettings).
                Scan = new ScanOptions
                {
                    IncludeSymlinks = r.IncludeSymlinks,
                    Scope = ScopeRuleSet.Parse(config.ScopeRules),
                },
                Plan = new PlanOptions
                {
                    SingleFileThresholdBytes = r.SingleFileThresholdBytes,
                    GroupCapBytes = r.GroupCapBytes,
                    MaxPackMembers = limits.MaxPackMembers,
                    MaxPackPathBytes = limits.MaxPackPathBytes,
                },
                VolumeBytes = r.VolumeBytes is > 0 ? r.VolumeBytes : null,
                Retention = RetentionOf(config, settings),
                UploadConcurrency = settings is { UploadConcurrency: > 0 } ? settings.UploadConcurrency : 5,
                Upload = RetryOf(settings),
                DeadWeightThreshold = settings is { DeadWeightThresholdPercent: > 0 }
                    ? settings.DeadWeightThresholdPercent / 100.0 : 0.30,
                AllowRepackDownload = settings?.RepackDownloadAllowed(config.DataTier) ?? true,
                VerboseLogging = r.VerboseLogging,
                ProcessingMaxAttempts = settings is { ProcessingMaxAttempts: > 0 } ? settings.ProcessingMaxAttempts : 5,
                OverlapDiffAndUpload = settings?.OverlapDiffAndUpload ?? true,
            },
        };
    }

    /// <summary>Maps the global settings' network retry backoff (PRD 4.1) to the upload path's RetryOptions.</summary>
    public static RetryOptions RetryOf(GlobalSettings? settings)
    {
        if (settings is null)
            return new RetryOptions();

        var sequence = ParseSeconds(settings.RetryBackoffSeconds);
        if (sequence.Count == 0)
            return new RetryOptions();

        return new RetryOptions
        {
            Backoff = sequence,
            SteadyInterval = sequence[^1],
            MaxTotalDelay = TimeSpan.FromMinutes(Math.Max(1, settings.RetryMaxTotalMinutes)),
        };
    }

    private static IReadOnlyList<TimeSpan> ParseSeconds(string? text) =>
        (text ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.TryParse(s, out var v) && v > 0 ? v : -1)
            .Where(v => v > 0)
            .Select(TimeSpan.FromSeconds)
            .ToList();

    public static RetentionPolicy RetentionOf(BackupConfig config, GlobalSettings? settings)
    {
        var r = ResolvedBackupSettings.From(config, settings);
        return new RetentionPolicy
        {
            MaxVersions = r.MaxVersions,
            MaxAgeDays = r.MaxAgeDays,
            Mode = r.RetentionMode,
        };
    }

    /// <summary>Cleanup options (retention + the tier/volume/threshold needed for dead-weight compaction), used by the scheduler's Cleanup task.</summary>
    public static CleanupOptions CleanupOf(BackupConfig config, GlobalSettings? settings = null)
    {
        var r = ResolvedBackupSettings.From(config, settings);
        return new CleanupOptions
        {
            Retention = RetentionOf(config, settings),
            DataTier = MapTier(config.DataTier),
            VolumeBytes = r.VolumeBytes is > 0 ? r.VolumeBytes : null,
            DeadWeightThreshold = settings is { DeadWeightThresholdPercent: > 0 }
                ? settings.DeadWeightThresholdPercent / 100.0 : 0.30,
            LocalRoot = config.LocalRoot,
            AllowRepackDownload = settings?.RepackDownloadAllowed(config.DataTier) ?? true,
        };
    }

    // The backup password now comes from ISecretReader.RevealBackupPassword (design §3.1); it is no longer exposed here.

    public static AccessTier MapTier(StorageTier tier) => tier switch
    {
        StorageTier.Cool => AccessTier.Cool,
        StorageTier.Cold => AccessTier.Cold,
        StorageTier.Archive => AccessTier.Archive,
        _ => AccessTier.Hot,
    };

    /// <summary>
    /// Joins a list's two halves into one rule set: the case-sensitive rules first, the case-insensitive ones
    /// after. Both empty → null, i.e. "no rules at all".
    /// <para>
    /// One set rather than two consulted in turn, because gitignore's "the last matching rule decides" has to keep
    /// holding across the pair — with two sets OR-ed together, a `!keep.mp4` in either half could never override a
    /// match in the other, and the negation would silently do nothing. Sensitive first is the arbitrary half of
    /// the choice; what matters is that the order is fixed and documented, so a negation can be written knowing
    /// what it overrides.
    /// </para>
    /// </summary>
    public static IgnoreRuleSet? Rules(string? sensitive, string? insensitive)
    {
        var tagged = SplitLines(sensitive).Select(p => (p, false))
            .Concat(SplitLines(insensitive).Select(p => (p, true)))
            .ToList();
        return tagged.Count == 0 ? null : IgnoreRuleSet.FromTagged(tagged);
    }

    private static IEnumerable<string> SplitLines(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
