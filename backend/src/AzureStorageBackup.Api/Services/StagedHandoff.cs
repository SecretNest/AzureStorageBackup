namespace AzureStorageBackup.Api.Services;

/// <summary>
/// One entry's ownership of everything that must be handed back exactly once when a compressed archive travels
/// from the compressor to an uploader.
/// <para>
/// Two things ride along. The <see cref="StagedItem"/> holds pool quota — an in-memory counter on a singleton
/// shared by every run — plus volume files on disk; leaking it books that space until the process restarts, and
/// since the quota gates output for all runs, enough leaks stall compression process-wide (see
/// <see cref="StagingArea.Hold"/>). The optional abandon callback is the dedup reservation of a single-file item:
/// latecomers in this run with identical content are blocked on it, and an entry discarded without answering them
/// leaves them waiting for the rest of the run.
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
