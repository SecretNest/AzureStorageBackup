namespace AzureStorageBackup.Api.Services;

/// <summary>
/// How much of a volume an upload stream may hold in memory (volume-identity.md, "Writing the label").
/// <para>
/// A volume is labelled with its own hash, and the cheapest way to make the label describe exactly the bytes sent
/// is to read the file once into memory and upload that buffer. That costs one volume per upload stream in RAM,
/// and both factors are user-set: raise the volume size to 1 GB and the concurrency to 10 and the uploader is
/// holding 10 GB. The global <c>UploadMemoryLimitBytes</c> setting caps that product, <b>per task</b>: every
/// backup, repair and compaction splits the limit evenly across its own upload streams, and a volume bigger than
/// its stream's share is hashed from disk first and re-read from disk for the send (<see cref="BlobUploader"/>).
/// Two tasks running at once each get the full limit — the setting is spent per operation, like the concurrency
/// it multiplies. An encrypted task spends none of it: its volumes go up unlabelled and are never held
/// (<see cref="VolumeLabelling"/>).
/// </para>
/// </summary>
public static class UploadMemoryBudget
{
    /// <summary>
    /// The least a stream is ever granted once a limit is set at all: one read chunk. Below that, "holding the
    /// volume in memory" holds less than the streaming path's own buffer, so the cut-off would only turn every
    /// small blob into a two-pass upload for nothing.
    /// </summary>
    public const long FloorBytes = 80 * 1024;

    /// <summary>The per-stream share of <paramref name="limitBytes"/> across <paramref name="streams"/> uploaders.
    /// A limit of 0 (or less) means no volume is ever held in memory and returns 0, floor included: the user asked for none.</summary>
    public static long PerStream(long limitBytes, int streams)
    {
        if (limitBytes <= 0)
            return 0;
        return Math.Max(FloorBytes, limitBytes / Math.Max(1, streams));
    }
}
