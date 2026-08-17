using System.Net.Sockets;
using Azure;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The one and only criterion for transient (retryable, suspendable) errors. Upload retry and the suspend gate share a single set,
/// so the two cannot judge separately and contradict each other with "the retry layer says retry, the gate layer says fail".
/// </summary>
public static class TransientErrors
{
    /// <param name="ct">
    /// The caller's cancellation token. Cancellation is the only case that needs context to tell apart: for one and the same
    /// <see cref="OperationCanceledException"/>, a token that has already fired means **the user pressed cancel** (must be rethrown),
    /// while one that has not means an SDK-internal network timeout (should be retried). Get this one wrong and the cancel button quietly stops working.
    /// </param>
    public static bool IsTransient(Exception ex, CancellationToken ct = default) => ex switch
    {
        RequestFailedException rfe => rfe.Status == 0 || rfe.Status >= 500 || rfe.Status is 408 or 429,
        IOException => true,
        SocketException => true,
        TimeoutException => true,
        OperationCanceledException => !ct.IsCancellationRequested,
        // This is exactly what Azure.Core throws once its own retries are exhausted (with a pile of TaskCanceledException inside).
        // We used to miss it here, so our own RetryPolicy layer never retried even once and simply pronounced the run dead.
        AggregateException agg => agg.InnerExceptions.Count > 0
            && agg.InnerExceptions.All(inner => IsTransient(inner, ct)),
        // A raw upload undid itself: the source moved while it was in flight, so the object was taken back and the
        // address given up again. The item that hit it never presents it here — its own caller answers it by
        // re-staging through the copying route, which cannot throw it. The one thing that does is a **peer**:
        // another file in this run whose content is byte-identical, parked on the same dedup reservation, which is
        // failed with whatever failed the upload. It receives it inside LocalDedupResolver.ResolveAsync, outside the
        // catch that answers it, so this line is all that stands between it and a dead run.
        // Retrying is also the right answer on the merits: for that peer this means what any failed upload means —
        // the address it was waiting for was withdrawn, so it has to upload the content itself, which is precisely
        // what a retry makes it do. And the wait it was in is the guilty item's **whole** upload, minutes for a
        // multi-GB file, with byte-identical duplicates being ordinary in the media library the raw route exists
        // for: not a window worth leaving a run-killer in.
        BackupOrchestrator.SourceMovedDuringUploadException => true,
        _ => false,
    };
}
