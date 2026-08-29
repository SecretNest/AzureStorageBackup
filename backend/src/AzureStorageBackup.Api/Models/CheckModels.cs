using Azure.Storage.Blobs.Models;

namespace AzureStorageBackup.Api.Models;

/// <summary>Cloud check depth (tiered; the user picks one per run; PRD check).</summary>
public enum CloudCheckLevel
{
    /// <summary>Do not check the cloud side at all.</summary>
    None = 0,

    /// <summary>Read only the cloud info/version files and compare them with the local cache, reporting drift (data blobs are never touched).</summary>
    Metadata = 1,

    /// <summary>Data blob/volume "exists + size" (HEAD, no download; when the size is unknown only existence is verified). Default.</summary>
    ExistenceSize = 2,

    /// <summary>Download and recompute the hash to verify the content (Archive must be rehydrated first).</summary>
    Content = 3,
}

/// <summary>Local source file check depth (tiered).</summary>
public enum LocalCheckLevel
{
    /// <summary>Do not check the local side at all.</summary>
    None = 0,

    /// <summary>Exists + size + permissions.</summary>
    Attributes = 1,

    /// <summary>Content hash (= the criterion for "repairable from local"). Default.</summary>
    Content = 2,
}

/// <summary>Options for one check run: two independent depth axes (cloud/local) + the Archive rehydration tier.</summary>
public sealed record CheckOptions
{
    public CloudCheckLevel Cloud { get; init; } = CloudCheckLevel.ExistenceSize;
    public LocalCheckLevel Local { get; init; } = LocalCheckLevel.Content;

    /// <summary>Tier to rehydrate to when a Content-level check hits an Archive blob (null = do not rehydrate; an Archive blob is then recorded as pending rehydration).</summary>
    public AccessTier? RehydrateTier { get; init; }

    /// <summary>
    /// Cloud listing check (§4.8): enumerate every blob in the container and report the orphans that no retained
    /// version references (stale volumes, leftovers from failed uploads, orphan indexes left behind by an ETag
    /// conflict, and so on). Report **only**; deletion happens solely during an explicit repair. Default false.
    /// </summary>
    public bool ListOrphans { get; init; }
}

/// <summary>Cloud-side state of one file.</summary>
public enum CloudState { NotChecked = 0, Ok = 1, MissingOrBad = 2 }

/// <summary>Local-side state of one file.</summary>
public enum LocalState { NotChecked = 0, Ok = 1, Missing = 2, Changed = 3 }

/// <summary>Check verdict for a single file.</summary>
public sealed record FileFinding(string Path, string? Ref, CloudState Cloud, LocalState Local)
{
    /// <summary>The entry's recorded source length. What a repair of this file costs is proportional to it, so
    /// the repair's byte-based progress and the plan's sizing both read it from here.</summary>
    public long Length { get; init; }

    /// <summary>The cloud copy is bad and the local content matches → repairable from local.</summary>
    public bool Repairable => Cloud == CloudState.MissingOrBad && Local == LocalState.Ok;

    /// <summary>Non-null = this cloud copy was carried over from an earlier version (backup has never managed to
    /// read the source file), and the value says since when. Without this information,
    /// <see cref="LocalState.Changed"/> reads as "the local file was edited", when the real reason is "backup never
    /// successfully updated this cloud copy" — and the two call for completely different action.</summary>
    public DateTimeOffset? UnreadableAt { get; init; }
}

/// <summary>Check report: per-file verdicts + an optional metadata drift note.</summary>
public sealed record CheckReport(int Version, IReadOnlyList<FileFinding> Findings, string? MetadataIssue = null)
{
    public bool Ok => MetadataIssue is null && Findings.All(f => f.Cloud != CloudState.MissingOrBad);

    /// <summary>
    /// Unreferenced blob names (orphans/garbage) found by the cloud listing check (§4.8). Populated only when
    /// <see cref="CheckOptions.ListOrphans"/> is set. Orphans do **not** affect <see cref="Ok"/> (they are not data
    /// corruption, just reclaimable wasted space). Empty by default.
    /// </summary>
    public IReadOnlyList<string> OrphanBlobs { get; init; } = [];

    /// <summary>
    /// The sentinel path that was missing, which is why every <see cref="FileFinding.Local"/> on this report reads
    /// <see cref="LocalState.NotChecked"/>; null when the local axis was not demoted.
    /// <para>
    /// It has to be carried on the report for the same reason <see cref="OrphansChecked"/> does, and the failure
    /// mode is worse here: a column of NotChecked cannot tell "nobody asked for a local check" from "asked, and
    /// the source was not mounted", and a reader who assumes the former concludes the backup verified fine. The
    /// check dialog is closed by the time anyone reads this, so the report is the only thing left that can say it.
    /// </para>
    /// <para>
    /// It deliberately does **not** affect <see cref="Ok"/>. The cloud half really did run and really did pass;
    /// failing the whole check because half of it was not applicable would be the false alarm this feature exists
    /// to remove, just moved to a different screen.
    /// </para>
    /// </summary>
    public string? LocalSkippedSentinel { get; init; }

    /// <summary>
    /// Whether the cloud listing check actually ran, which an empty <see cref="OrphanBlobs"/> cannot say on its own:
    /// "nobody asked" and "asked, and the container is clean" are the same empty list, and a reader with only that
    /// list has to guess.
    /// <para>
    /// The guess was being made in the UI, off the state of its own checkbox — which is lost the moment the dialog
    /// closes, and the dialog closes as soon as the check starts. So a completed scan reported nothing at all,
    /// whatever it found. The report has to carry this itself, because the report is what outlives the dialog.
    /// </para>
    /// <para>False when the scan was asked for but abandoned; <see cref="OrphanScanIssue"/> then says why.</para>
    /// </summary>
    public bool OrphansChecked { get; init; }

    /// <summary>
    /// Why the orphan scan was abandoned even though it was asked for (the full reference set could not be built, so
    /// calling anything an orphan would be a guess). Null when it ran, and when it was never asked for.
    /// <para>
    /// Reported rather than merely logged: silence here looks exactly like "the box was never ticked", which is the
    /// very confusion <see cref="OrphansChecked"/> exists to end.
    /// </para>
    /// </summary>
    public string? OrphanScanIssue { get; init; }

    /// <summary>Names of the broken blobs (deduplicated; kept for the old frontend).</summary>
    public IReadOnlyList<string> MissingRefs =>
        Findings.Where(f => f.Cloud == CloudState.MissingOrBad && f.Ref is not null)
            .Select(f => f.Ref!).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Paths of the broken files (kept for the old frontend).</summary>
    public IReadOnlyList<string> CorruptedPaths =>
        Findings.Where(f => f.Cloud == CloudState.MissingOrBad).Select(f => f.Path).ToList();

    /// <summary>Paths of the files that can be repaired from local.</summary>
    public IReadOnlyList<string> RepairablePaths =>
        Findings.Where(f => f.Repairable).Select(f => f.Path).ToList();
}
