using System.Diagnostics;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Models;

/// <summary>
/// CPU priority tiers for the 7z process.
/// <para>
/// <b>Lowest must be 0.</b> When the column was added, EF backfilled existing rows with 0, so an upgraded
/// old database naturally lands on "lowest", matching a fresh database's default. The cautionary tale is
/// StagedLimitBytes / ProcessingMaxAttempts: their legal defaults are not 0, so <see cref="Services.GlobalSettingsService"/>
/// still carries a "read 0, swap back to the default" patch to this day. Pinning Lowest at 0 avoids that debt.
/// </para>
/// <para>No "above normal" is offered: raising priority on Linux takes privileges, and letting compression
/// cut ahead of the web UI has nothing but downsides for a background backup program.</para>
/// </summary>
public enum SevenZipCpuPriority
{
    /// <summary>Linux nice 19. Eats only the CPU nobody else wants.</summary>
    Lowest = 0,
    /// <summary>Linux nice 10.</summary>
    BelowNormal = 1,
    /// <summary>Linux nice 0, competing on equal terms with every other process.</summary>
    Normal = 2,
}

public static class SevenZipCpuPriorityExtensions
{
    /// <summary>Map to a process priority. Anything falling through to default (an unrecognized value stored in
    /// the database) goes to lowest: compressing a bit slower when we can't tell is a small thing, wedging the
    /// machine is not.</summary>
    public static ProcessPriorityClass ToProcessPriorityClass(this SevenZipCpuPriority priority) => priority switch
    {
        SevenZipCpuPriority.Normal => ProcessPriorityClass.Normal,
        SevenZipCpuPriority.BelowNormal => ProcessPriorityClass.BelowNormal,
        _ => ProcessPriorityClass.Idle,
    };
}

/// <summary>
/// Global settings (singleton, Id=1). Defaults for new backups (PRD §11 "use default") + global items (log retention, concurrency).
/// </summary>
public class GlobalSettings
{
    public int Id { get; set; }

    // Defaults for new backups
    public StorageTier DefaultIndexTier { get; set; } = StorageTier.Hot;
    public StorageTier DefaultDataTier { get; set; } = StorageTier.Archive;
    public int DefaultMaxVersions { get; set; } = 100;
    public int DefaultMaxAgeDays { get; set; } = 180;
    public RetentionMode DefaultRetentionMode { get; set; } = RetentionMode.EitherTriggers;
    public long DefaultSingleFileThresholdBytes { get; set; } = 5 * 1024 * 1024;
    public long DefaultGroupCapBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>The target pack size (default 100M) doubles as the compression volume size (PRD 3.3.2.3); 0/null = no splitting.</summary>
    public long? DefaultVolumeBytes { get; set; } = 100 * 1024 * 1024;

    // Dead-weight compaction (grouped packs only): per data tier, decides whether a repack may download the cloud pack to fill in members missing locally.
    // Local files (with matching content) are preferred; if a member is missing locally and this switch is false, the repack of that pack is abandoned. Archive defaults to false (avoids costly retrieval/rehydrate).
    public bool RepackDownloadHot { get; set; } = true;
    public bool RepackDownloadCool { get; set; } = true;
    public bool RepackDownloadCold { get; set; } = true;
    public bool RepackDownloadArchive { get; set; }

    /// <summary>Whether a given data tier may download the cloud pack to fill in locally missing members during a dead-weight repack.</summary>
    public bool RepackDownloadAllowed(StorageTier tier) => tier switch
    {
        StorageTier.Cool => RepackDownloadCool,
        StorageTier.Cold => RepackDownloadCold,
        StorageTier.Archive => RepackDownloadArchive,
        _ => RepackDownloadHot,
    };
    public bool DefaultIncludeSymlinks { get; set; }
    public string? DefaultIgnoreRules { get; set; }
    public string? DefaultDontCompressRules { get; set; }
    public string? DefaultDontGroupRules { get; set; }

    /// <summary>Global default for cross-path grouping rules (gitignore syntax). Empty = group strictly by directory.</summary>
    public string? DefaultCrossDirGroupRules { get; set; }

    // Case-insensitive halves, matching the split on BackupConfig — see the note there for why extensions and
    // paths cannot share one sensitivity.
    public string? DefaultIgnoreRulesCaseInsensitive { get; set; }
    public string? DefaultDontCompressRulesCaseInsensitive { get; set; }
    public string? DefaultDontGroupRulesCaseInsensitive { get; set; }
    public string? DefaultCrossDirGroupRulesCaseInsensitive { get; set; }

    // Global
    public int UploadConcurrency { get; set; } = 5;
    public int DownloadConcurrency { get; set; } = 5; // Download concurrency for restore / deep check (PRD 3.4)

    /// <summary>Concurrency for the check's HEAD-only probing (existence+size, rehydration estimates). Separate from
    /// <see cref="DownloadConcurrency"/> because a HEAD moves no data: sizing the download budget against a bandwidth
    /// cap must not strangle a stage that is round-trip-bound, not bandwidth-bound. Default 20.</summary>
    public int CheckHeadConcurrency { get; set; } = 20;

    /// <summary>Retention in days for ephemeral (debug/info) logs (PRD 3.6, default 14). Durable audit logs are not subject to it.</summary>
    public int LogEphemeralMaxAgeDays { get; set; } = 14;

    /// <summary>Whether new backups write debug-level logs (which include operated file names) by default. Off by default (can be turned on per backup).</summary>
    public bool DefaultVerboseLogging { get; set; }

    // Network retry backoff (PRD 4.1): a comma-separated sequence of seconds + a total time cap (in minutes).
    // Defaults to 5s, 30s, 90s, 300s, then every 300s (= the last entry of the sequence), capped at 2h in total.
    public string RetryBackoffSeconds { get; set; } = "5,30,90,300";
    public int RetryMaxTotalMinutes { get; set; } = 120;

    // Dead-weight compaction threshold (PRD 3.3.3.4, M4 §6): when a pack's dead-weight ratio exceeds this percentage, repack it in place to reclaim space.
    public int DeadWeightThresholdPercent { get; set; } = 30;

    /// <summary>Byte cap on the compression staging area (staged-temp), the backpressure threshold (decision 4, changeable live via Settings). Default 2GB.</summary>
    public long StagedLimitBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>How the staging pool behaves when it is FULL: false (default) = the classic strict ceiling —
    /// nobody may start compressing until usage drops below the limit (disk safety first); true = fair-share —
    /// 20% of the limit is split evenly as a per-run guarantee and the other 80% is first-come shared, so one
    /// run's oversized family (a single archive can exceed the whole limit; it cannot be split) never starves
    /// the others completely. The trade is honest and the operator's to make ("让用户自己去开关"): fair-share can
    /// overshoot the limit further when EVERY run is handling huge files at once.</summary>
    public bool StagingFairShare { get; set; }

    /// <summary>Cap on reprocessing attempts when the same member keeps changing during post-compression re-verification (PRD §5.1, M4 §9, default 5).</summary>
    public int ProcessingMaxAttempts { get; set; } = 5;

    /// <summary>
    /// Whether diffing and "compress + upload" overlap during a backup (on by default). With it on, the network
    /// doesn't have to wait for all hashing to finish; the price is that diff reads and compression reads land on the
    /// same disk at once. On a NAS with spinning disks the two read streams can drag each other down enough to not be
    /// worth it — turn it off in that case and go back to "decide everything first, then upload".
    /// </summary>
    public bool OverlapDiffAndUpload { get; set; } = true;

    /// <summary>
    /// Automatically resume the last interrupted backup after a restart (on by default). Built for planned restarts
    /// and upgrades: shutdown suspends the run to disk, startup picks it back up, nobody clicks anything in between.
    /// <para>
    /// The criterion is deliberately narrow: only a run whose suspend marker reads <see cref="SuspendReason.ShuttingDown"/>
    /// is resumed automatically, because only that one unambiguously means "this process's own planned exit stopped it
    /// here". A user-pressed pause, a gate downgrade, and the whole class with **no marker at all** (crash, killed,
    /// shutdown flush timeout, operator-pressed cancel) are all left alone — the absence of a marker does not establish
    /// "this was an accident", it could just as easily have been a cancel, and reopening a run the user just canceled is
    /// far worse than not resuming it. All of those keep the Run button waiting for a human.
    /// </para>
    /// </summary>
    public bool AutoResumeInterruptedRuns { get; set; } = true;

    /// <summary>
    /// CPU priority for the 7z process, lowest by default. Compression and decompression are the only things this
    /// program does that will saturate a CPU, and it runs on a machine with other things running too — nobody notices a
    /// slower backup, everybody notices the machine seizing up.
    /// <para>
    /// A different thing from <c>-mmt=N</c> in <c>Backup__SevenZipMethodArgs</c>: capping threads lowers parallelism,
    /// this lowers the queueing weight under contention. A single saturated thread can still make the UI stutter, and
    /// priority is the only thing that helps there.
    /// </para>
    /// </summary>
    public SevenZipCpuPriority SevenZipPriority { get; set; } = SevenZipCpuPriority.Lowest;
}
