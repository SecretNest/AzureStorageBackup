namespace AzureStorageBackup.Api.Models;

/// <summary>
/// The most recent **completed** check per backup config, persisted across restarts.
/// <para>
/// The in-memory run state already outlives the dialog (close it, reopen it, the report is still there — see
/// CheckRunState.Report), but not the process: pull a new image and the report the user was coming back to act
/// on is gone, and the only way to see it again is to re-run the check. Only completed runs with a report are
/// written here — a failed run (a busy click, a network blip) must not clobber the last real result, because
/// this row answers "what did the last finished check find", not "what happened most recently".
/// </para>
/// </summary>
public sealed class LastCheckRun
{
    /// <summary>One row per config, keyed the same way the in-memory runner keys its runs.</summary>
    public int BackupConfigId { get; set; }

    /// <summary>The report, JSON-serialized. The computed properties (Ok, MissingRefs…) recompute on load.</summary>
    public string ReportJson { get; set; } = "";

    public DateTimeOffset FinishedAt { get; set; }

    /// <summary>How the last check ended up ("这个信息还是要进入db"). Only <see cref="CheckResolution.Pending"/>
    /// GATES — refuses further checks, turns the row's button red, holds the orphan sweep. The other three are
    /// the one-line history the dialog shows ("全部正常/已修好/drop了,N个没修好"), freely overwritten by the next
    /// check, scheduled or manual.</summary>
    public CheckResolution Resolution { get; set; }

    /// <summary>How many problem files remain unrepaired — set when the report persists, updated by every
    /// completed repair, frozen when the report is dropped. The number the "dropped" line carries.</summary>
    public int UnrepairedCount { get; set; }
}

/// <summary>The lifecycle of a persisted check report.</summary>
public enum CheckResolution
{
    /// <summary>Problems (or orphans) found and not yet dealt with: gates further checks, the button is red.</summary>
    Pending = 0,

    /// <summary>The check found nothing wrong.</summary>
    Clean = 1,

    /// <summary>A repair fixed everything the report listed.</summary>
    Repaired = 2,

    /// <summary>The operator dropped the report with problems still open; the marks carry the memory.</summary>
    Dropped = 3,
}
