namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Where <see cref="LocalFileScanner"/> hands each entry as it finds it. Collecting the whole tree into a <c>List</c>
/// — what the scanner used to return — put every scanned file in memory at once, proportional to file count. A sink
/// lets the scanner stay ignorant of where an entry ends up: a test can keep an in-memory list
/// (<see cref="ListScanSink"/>), while a real run writes straight into the per-run work database
/// (<see cref="WorkDbScanSink"/>) and never holds more than one entry at a time.
/// </summary>
public interface IScanSink
{
    ValueTask AddAsync(ScannedEntry entry, CancellationToken ct);
}

/// <summary>
/// Keeps every entry in memory, in the order the walk produced them — this sink performs no sorting. Callers that
/// need a stable order sort <see cref="Entries"/> themselves; ordering is no longer the scanner's job now that a sink
/// can be something with no notion of "the whole list" at all, such as <see cref="WorkDbScanSink"/>, whose order
/// comes from the <c>path_key</c> the database reads by.
/// </summary>
public sealed class ListScanSink : IScanSink
{
    public List<ScannedEntry> Entries { get; } = [];

    public ValueTask AddAsync(ScannedEntry entry, CancellationToken ct)
    {
        Entries.Add(entry);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Writes each entry straight into the run's scratch database. The grouping verdict (single file / per-directory
/// group / cross-directory group) is computed here, entry by entry, via <see cref="GroupingPlanner.ClassifyOne"/> —
/// there is no bulk classification pass afterward, because that would need the whole scan held in memory first,
/// which is exactly what moving to a sink was meant to avoid. No buffering happens on this side: one
/// <see cref="RunWorkDb.InsertScanAsync"/> per entry: the work database's own writer already batches internally.
/// </summary>
public sealed class WorkDbScanSink(RunWorkDb work, PlanOptions plan) : IScanSink
{
    public ValueTask AddAsync(ScannedEntry entry, CancellationToken ct)
    {
        var cls = GroupingPlanner.ClassifyOne(entry.Path, entry.Length, plan);
        return work.InsertScanAsync(
            new ScanRow(entry.Path, entry.Kind, entry.Length, entry.ModifiedAt, entry.Permissions, entry.Target,
                cls.Category, cls.GroupKey),
            ct);
    }
}

/// <summary>
/// What a scan left behind, once every entry has already been handed to the sink: how many entries there were
/// (the sink itself may not be able to say — <see cref="WorkDbScanSink"/> holds none of them), plus the empty
/// directories and unreadable paths, which stay small (bounded by directory count and by failures, not by file
/// count) and so are kept as ordinary lists rather than written to the sink too.
/// </summary>
public sealed record ScanSummary(long Entries, IReadOnlyList<string> EmptyDirs, IReadOnlyList<UnreadablePath> Unreadable);
