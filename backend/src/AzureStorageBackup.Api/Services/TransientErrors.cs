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
        _ => false,
    };
}
