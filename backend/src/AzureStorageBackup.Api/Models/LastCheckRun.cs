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
}
