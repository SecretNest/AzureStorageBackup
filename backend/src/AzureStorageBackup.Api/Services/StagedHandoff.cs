namespace AzureStorageBackup.Api.Services;

/// <summary>
/// One entry's ownership of everything that must be handed back exactly once when a compressed archive travels
/// from the compressor to an uploader.
/// <para>
/// Two things ride along. The <see cref="StagedItem"/> holds pool quota — an in-memory counter on a singleton
/// shared by every run — plus volume files on disk; leaking it books that space until the process restarts, and
/// since the quota is also the gate output waits at (<c>StagingArea.WaitForRoomAsync</c>, private, hence not a
/// cref), enough leaks stall compression process-wide, invisibly: the UI column shows this run's seat usage, not
/// the global account.
/// <para>
/// The optional abandon callback answers a dedup reservation that would otherwise be left waiting. **No production
/// caller passes one today**: the single-file reservation is taken on the uploader, inside the same method that
/// answers it (<c>res.Fail</c> paired with <c>MarkSettled</c>), so it never crosses the queue and cannot be
/// orphaned by a discarded entry. The parameter is kept because the ownership rule it encodes is the one thing this
/// class exists to state — a queue entry that dies owes an answer to whoever is waiting on it — and the next thing
/// to travel this queue may well owe one.
/// </para>
/// </para>
/// <para>
/// This is the whole of that guard now. <c>StagingArea.Hold</c> used to wrap the same release in a <c>using</c>
/// scope, and it was deleted along with its last caller when the pack path was cut in two: the archive's lifetime
/// stopped ending inside the method that produced it, so a scope guard had nothing left to scope.
/// </para>
/// <para>
/// <see cref="MarkSettled"/> is the difference between "the upload answered the waiters" and "this archive died on
/// the way". It is not cosmetic: <c>Resolution.Fail</c> also withdraws the claim from the reservation table, so
/// calling it after a successful upload would make the next file with the same content upload those bytes again.
/// </para>
/// </summary>
public sealed class StagedHandoff(StagingArea area, StagedItem? staged, Action<Exception>? abandon = null)
    : IDisposable
{
    private int _settled;
    private int _disposed;

    /// <summary>The archive on disk. Null when 7z dropped every member of the group and left no archive at all.</summary>
    public StagedItem? Staged => staged;

    /// <summary>The upload finished (or deduplicated onto an existing blob): the waiters have their answer already.</summary>
    public void MarkSettled() => Interlocked.Exchange(ref _settled, 1);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (staged is not null)
            area.Release(staged);
        if (Volatile.Read(ref _settled) == 0)
            abandon?.Invoke(new OperationCanceledException(
                "Staged work was discarded before it reached the cloud."));
    }
}
