using System.Globalization;
using System.Text;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Layout of the summary line for a successful backup. This message goes to both the operation log and the webhook
/// notification, so it is the one line the operator is certain to read; it has to answer a few questions on its own:
/// which files moved this round, how much data the cloud gained, how much the old versions freed up.
/// <para>
/// Pulling it out into a pure function (instead of concatenating strings inside the orchestrator) has exactly one
/// purpose: to make the "a zero makes the whole segment disappear" rule testable item by item. If every round dragged
/// a string of zeros along, the round that actually carries information would drown in the noise — especially
/// unreadable, which is precisely the item that most needs to be seen.
/// </para>
/// </summary>
public static class BackupSummary
{
    public static string Format(BackupRunResult r)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Version {r.Version}");

        var files = new List<string>(4);
        if (r.NewFiles > 0)
            files.Add($"{r.NewFiles} new");
        if (r.ModifiedFiles > 0)
            files.Add($"{r.ModifiedFiles} modified");
        if (r.DeletedFiles > 0)
            files.Add($"{r.DeletedFiles} deleted");
        // An unreadable file counts as neither a change nor a deletion; the index silently carries the old entry
        // forward — so it has to occupy an item of its own. Without this line, a "successful" backup papers over the
        // files this round never actually stored.
        if (r.UnreadableFiles > 0)
            files.Add($"{r.UnreadableFiles} unreadable (skipped)");

        sb.Append("\nFiles: ").Append(files.Count > 0 ? string.Join(", ", files) : "no changes");

        // The line is dropped only when the source-side change volume and the uploaded volume are both zero. When
        // only the uploaded volume is zero it must **not** be dropped — that is exactly the round where dedup hit on
        // everything, and "changed 4.7 GB yet uploaded not a single byte" is the whole point of reporting these two
        // figures separately.
        if (r.ChangedBytes > 0 || r.UploadedBytes > 0)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"\nData: {ByteSize.Human(r.ChangedBytes)} changed at source → {ByteSize.Human(r.UploadedBytes)} uploaded");
        }

        if (!r.Cleanup.IsEmpty)
            sb.Append('\n').Append(FormatRetention(r.Cleanup));

        // Said out loud rather than left as an empty cleanup line: the backup is complete either way, but nothing
        // was retired this round, so the container keeps growing until a later run manages to reach the cloud.
        // Whoever reads this summary is the only one in a position to notice that early.
        if (r.CleanupSkipped is { Length: > 0 } why)
            sb.Append(CultureInfo.InvariantCulture, $"\nCleanup: skipped, will retry next run ({why})");

        return sb.ToString();
    }

    /// <summary>
    /// The retention-cleanup line. It is public because the scheduled cleanup task (TaskDispatcher) has to report the
    /// same numbers when it runs on its own — write the wording for one thing twice in two places and it will drift
    /// into two different phrasings sooner or later, while the operator has to read both logs.
    /// Callers must skip it themselves on <see cref="CleanupReport.IsEmpty"/>: when nothing was cleaned up, this line should not appear.
    /// </summary>
    public static string FormatRetention(CleanupReport c)
    {
        var parts = new List<string>(3);
        if (c.RetiredVersions > 0)
            parts.Add($"retired {c.RetiredVersions} version(s)");

        var objects = new List<string>(2);
        if (c.DeletedPacks > 0)
            objects.Add($"{c.DeletedPacks} pack(s)");
        if (c.DeletedBlobs > 0)
            objects.Add($"{c.DeletedBlobs} blob(s)");
        if (objects.Count > 0)
            parts.Add("deleted " + string.Join(" + ", objects));

        if (c.FreedBytes > 0)
            parts.Add($"freed {ByteSize.Human(c.FreedBytes)}");

        return "Retention: " + string.Join(", ", parts);
    }
}
