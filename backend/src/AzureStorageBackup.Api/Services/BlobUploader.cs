using Azure;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Upload of data/pack/index blobs (M4 §5): setting the Tier, retry backoff, content-addressed idempotent
/// skipping, concurrency.
/// <para>
/// <c>filePath</c> is not always something this process wrote. Since the raw route stopped copying, a store-only,
/// unencrypted, single-volume item is uploaded straight from the **user's own file** — so an implementation must
/// open it the way this project opens source files, through <see cref="FileHasher.OpenRead"/>, and must not move,
/// truncate or delete it. The reason is in that method's remarks: an ordinary open() on a FIFO blocks forever
/// inside a syscall no CancellationToken can reach, and it would take the whole run with it.
/// </para>
/// </summary>
public interface IBlobUploader
{
    /// <summary>Upload a file to a blob (with Tier + optional metadata). If the blob already exists, skip it and return false (content-addressed idempotency).</summary>
    Task<bool> UploadIfMissingAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? metadata = null);

    /// <summary>
    /// Same as above, plus byte reporting **during** the upload (<paramref name="progress"/> receives a running total within this call).
    /// Without it, the speed in the UI can only jump at the granularity of "one blob finished": uploading a 100 MB pack takes
    /// dozens of seconds, and throughout those seconds the measurement window is empty and the reading drops to zero.
    /// <para>
    /// The default implementation simply drops the progress and forwards to the version without it — test doubles don't have to
    /// change a line for this. The progress parameter goes last **and gets no default value**: 8 arguments uniquely match the
    /// overload above, 9 uniquely match this one, so there is no ambiguity.
    /// </para>
    /// </summary>
    Task<bool> UploadIfMissingAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry, CancellationToken ct,
        IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
        => UploadIfMissingAsync(account, container, blobName, filePath, tier, retry, ct, metadata);

    /// <summary>Upload a file to a blob, overwriting (with Tier + optional metadata), with **no** existence short-circuit — an existing target is overwritten anyway.
    /// For atomic replacement: overwrite-upload the new volumes first, then delete the leftover old ones, which lowers the crash window from "the whole blob is lost" to "a mix of new and old volumes" (repairable).</summary>
    Task UploadOverwriteAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? metadata = null);

    /// <summary>Overwrite upload with byte progress — the same default-forwarding shape as the if-missing
    /// overload above, for the same reason: test doubles change nothing, and the two parameter counts uniquely
    /// select their overloads.</summary>
    Task UploadOverwriteAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry, CancellationToken ct,
        IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
        => UploadOverwriteAsync(account, container, blobName, filePath, tier, retry, ct, metadata);
}

public sealed class BlobUploader(IBlobClientFactory factory) : IBlobUploader
{
    /// <summary>Files at or under this size are read whole into memory, labelled (<see cref="VolumeIdentity"/>)
    /// and uploaded from that buffer — one disk read feeds both the hash and the wire. Files past it (the raw
    /// route can hand this uploader an arbitrarily large source file) stream exactly as before and go
    /// unlabelled: never a wrong label, never an unbounded buffer. The default clears the default volume size
    /// (100 MB) with headroom; the bound on resident memory is this × upload concurrency.</summary>
    public long LabelMemoryLimit { get; init; } = 256L * 1024 * 1024;

    public Task<bool> UploadIfMissingAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? metadata = null)
        => UploadCoreAsync(account, container, blobName, filePath, tier, overwrite: false, retry, ct, metadata);

    public Task<bool> UploadIfMissingAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry, CancellationToken ct,
        IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
        => UploadCoreAsync(account, container, blobName, filePath, tier, overwrite: false, retry, ct, metadata, progress);

    public async Task UploadOverwriteAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? metadata = null)
        => await UploadCoreAsync(account, container, blobName, filePath, tier, overwrite: true, retry, ct, metadata);

    public async Task UploadOverwriteAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, RetryOptions? retry, CancellationToken ct,
        IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress)
        => await UploadCoreAsync(account, container, blobName, filePath, tier, overwrite: true, retry, ct, metadata, progress);

    /// <summary>Upload core: with overwrite=false, short-circuit and return false if the blob already exists (if-missing semantics);
    /// with overwrite=true, just overwrite-upload. Returns whether an upload actually happened.</summary>
    private async Task<bool> UploadCoreAsync(
        Account account, string container, string blobName, string filePath,
        AccessTier tier, bool overwrite, RetryOptions? retry, CancellationToken ct,
        IReadOnlyDictionary<string, string>? metadata, IProgress<long>? progress = null)
    {
        var blob = factory.CreateServiceClient(account)
            .GetBlobContainerClient(container)
            .GetBlobClient(blobName);

        var options = new BlobUploadOptions { AccessTier = tier, ProgressHandler = progress };
        if (metadata is not null)
            options.Metadata = metadata.ToDictionary(kv => kv.Key, kv => kv.Value);

        // The identity label rides the same request as the bytes it describes (volume-identity.md): read the
        // file once into memory, hash it, and upload that very buffer, so the label can never describe anything
        // but what went over the wire. Sized-out files stream below, unlabelled.
        // A caller that already knows the bytes' hash supplies it in the metadata (the raw route: the blob IS the
        // source file, whose FullHash the backup computed in the same format) — used verbatim, no buffering, no
        // recompute, any size. Consistency with the bytes rides the caller's own guarantees (the raw route's
        // stat-bracket).
        byte[]? buffered = null;
        if (metadata?.ContainsKey(VolumeIdentity.MetaKey) != true
            && new FileInfo(filePath).Length <= LabelMemoryLimit)
        {
            await using (var read = FileHasher.OpenRead(filePath))
            using (var ms = new MemoryStream())
            {
                await read.CopyToAsync(ms, ct);
                buffered = ms.ToArray();
            }
            options.Metadata ??= new Dictionary<string, string>();
            options.Metadata[VolumeIdentity.MetaKey] = VolumeIdentity.Compute(buffered);
        }

        // Let the **server** enforce the if-missing semantics, rather than relying on "Exists first, then upload".
        //
        // That approach has a non-atomic gap, and uploads do get retried: the network hiccups (very common on a NAS), the
        // server has in fact already written the blob but the client only got a timeout or a 5xx, so the retry goes and
        // overwrites a blob that already exists. When the data tier is Archive that fails outright — an archived blob may not
        // be overwritten (Put Block can't even carry a tier) and it returns 409 BlobArchived, which is not on the retryable
        // list, so the whole backup run goes down with it.
        // The same gap gets hit by concurrency: two tasks pass the existence check for the same blob name one after the other, and both see "doesn't exist".
        //
        // A conditional request has no such window: the server evaluates the condition before writing and rejects outright
        // (412) without writing a single byte if it isn't satisfied, so retries and concurrency are idempotent by nature and
        // an already-archived blob is never touched. It saves that one HEAD along the way, too.
        if (!overwrite)
            options.Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };

        try
        {
            // Forward the real cancellation token to TransientErrors.IsTransient so it can distinguish a user cancel from a network timeout.
            // Without forwarding it, OperationCanceledException would be misjudged as a transient error and enter the retry flow.
            await RetryPolicy.ExecuteAsync(async token =>
            {
                // FileHasher.OpenRead, not File.OpenRead. Since the raw route stopped copying, the path handed in
                // here can be a **source file** rather than something this process wrote into staged-temp, and
                // reading a source file has exactly one door in this project for a reason: an ordinary open() on a
                // FIFO blocks forever waiting for a writer, inside a syscall no CancellationToken can reach, and it
                // would take the whole run with it. See the remarks on FileHasher.OpenRead. For the staged volumes
                // that come through here otherwise it is the same open — O_NONBLOCK has no effect on the read
                // semantics of a regular file.
                if (buffered is not null)
                {
                    // A fresh stream per attempt: a retried upload must restart from byte 0.
                    using var stream = new MemoryStream(buffered, writable: false);
                    await blob.UploadAsync(stream, options, token);
                }
                else
                {
                    await using var stream = FileHasher.OpenRead(filePath);
                    await blob.UploadAsync(stream, options, token);
                }
            }, retry, ex => TransientErrors.IsTransient(ex, ct), ct);
        }
        catch (RequestFailedException ex) when (!overwrite && IsAlreadyThere(ex))
        {
            // Already there is exactly the outcome if-missing wants, not an error.
            return false;
        }

        return true;
    }

    /// <summary>
    /// A conditional upload was turned away by "already exists". 412 is the normal path for an unsatisfied If-None-Match;
    /// 409 BlobAlreadyExists is accepted too — when a retry runs into the copy it just wrote successfully, the server may give either.
    /// <para>
    /// **BlobArchived counts as well.** A conditional request cannot save an archived blob: for a write to an already-archived
    /// object the server rejects **before** evaluating the condition, so what comes back is not a 412 but a 409 BlobArchived.
    /// And under if-missing semantics the meaning of that error is unambiguous — the target is already there, which is exactly
    /// "no need to upload it again". Running a backup on the Archive data tier, every already-stored object comes through here.
    /// </para>
    /// <para>
    /// Only accepted on the if-missing side. <c>overwrite: true</c> (repair, dead-weight compaction) hitting BlobArchived
    /// really does mean overwriting archived data, and that case must fail loudly, never silently count as success.
    /// </para>
    /// </summary>
    private static bool IsAlreadyThere(RequestFailedException ex) =>
        ex.Status == 412 || ex.ErrorCode is "BlobAlreadyExists" or "BlobArchived";
}
