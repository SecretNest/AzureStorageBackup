namespace AzureStorageBackup.Api.Models;

/// <summary>
/// A user repair suspended mid-run, persisted so it survives the process (volume-identity.md § repair is a
/// run). Only the **original selection** is stored: resume re-runs the pre-check and intersects — files healed
/// meanwhile fall out on their own (their blobs check clean, and the healed-mark clearing writes that down),
/// half-replaced families are salvaged volume by volume by the verified skip. The labels in the cloud are the
/// resume state; this row is just the intent.
/// <para>
/// Its existence is also the deference signal: while a suspended user repair exists, the post-backup deferred
/// repair (<see cref="Services.DeferredRepairs"/>) must not start one of its own — a suspension is explicit
/// intent, and automation does not step over it.
/// </para>
/// </summary>
public sealed class SuspendedRepair
{
    public int BackupConfigId { get; set; }

    /// <summary>The plan's selection, JSON array of paths. Empty array = "mark everything" (a legitimate choice).</summary>
    public string PathsJson { get; set; } = "[]";

    public CloudCheckLevel Cloud { get; set; }
    public StorageTier? RehydrateTier { get; set; }
    public bool CleanupOrphans { get; set; }
    public DateTimeOffset SuspendedAt { get; set; }
}
