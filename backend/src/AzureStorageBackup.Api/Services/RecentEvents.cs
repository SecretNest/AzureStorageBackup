namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The last few events of a run (what got skipped, which file failed, what it is waiting on).
/// <para>
/// Why it is needed: restore used to write messages like these into a **single-valued** field (`RestoreRunState.Phase`),
/// where each new one simply overwrote the previous. So a restore that skipped or failed dozens of files finished with only
/// the last message on screen and no way to trace the rest — visible only as the FailedFiles number, when "which files, and why" is what the operator actually wants.
/// </para>
/// <para>The capacity is bounded: messages like these can be of the same order as the file count, and keeping them unbounded trades memory
/// for a log nobody will ever read to the end. When it is full the oldest goes — what happened most recently is more likely to bear on the problem at hand.</para>
/// </summary>
public sealed class RecentEvents(int capacity = 200)
{
    private readonly Queue<string> _items = new();
    private readonly Lock _gate = new();

    public void Add(string message)
    {
        lock (_gate)
        {
            _items.Enqueue(message);
            while (_items.Count > capacity)
                _items.Dequeue();
        }
    }

    /// <summary>Takes a snapshot. Returns a copy rather than the internal collection — the caller (HTTP serialization) and the writer sit on different threads.</summary>
    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
            return [.. _items];
    }
}
