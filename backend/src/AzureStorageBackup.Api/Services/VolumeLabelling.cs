namespace AzureStorageBackup.Api.Services;

/// <summary>
/// How a volume upload is labelled (volume-identity.md): whether the uploader writes the volume's own xxh128 next to
/// it at all, and — when it does — the most of the volume it may hold in memory to compute that label over exactly
/// the bytes sent (<see cref="UploadMemoryBudget"/>; a bigger volume is hashed from disk and re-read for the send).
/// <para>
/// The label exists to let a later upload of the same family skip a volume that is already there, byte for byte.
/// An <b>encrypted</b> backup can never take that skip: 7z draws a fresh random IV for every archive, so the same
/// source compressed twice is different bytes end to end, and a label written today matches nothing tomorrow.
/// Labelling such a volume would buy nothing and cost either the memory that holds it or a second read of it,
/// so encrypted volumes go up unlabelled (<see cref="None"/>) and stream straight from disk through the read
/// buffer <see cref="FileHasher.OpenRead"/> already gives every upload. "Unlabelled" reads as "different" to every
/// skip decision, which is what an encrypted volume is anyway — the behaviour is unchanged, only the bill.
/// </para>
/// </summary>
/// <param name="Label">Whether to write the identity label at all.</param>
/// <param name="InMemoryLimitBytes">The per-stream share of the upload memory limit: the largest volume that is
/// read whole into memory for its label. Meaningless when <paramref name="Label"/> is false.</param>
public readonly record struct VolumeLabelling(bool Label, long InMemoryLimitBytes)
{
    /// <summary>Label every volume, holding at most <paramref name="inMemoryLimitBytes"/> of one in memory to do so.</summary>
    public static VolumeLabelling Labelled(long inMemoryLimitBytes) => new(true, inMemoryLimitBytes);

    /// <summary>No label, no buffering: the volume streams from disk as it is.</summary>
    public static VolumeLabelling None => new(false, 0);

    /// <summary>The labelling for a backup: encrypted (a password is set) → <see cref="None"/>; otherwise
    /// <see cref="Labelled"/> within the task's per-stream share.</summary>
    public static VolumeLabelling For(string? password, long inMemoryLimitBytes)
        => string.IsNullOrEmpty(password) ? Labelled(inMemoryLimitBytes) : None;

    public override string ToString() => Label ? $"labelled (≤ {InMemoryLimitBytes} B in memory)" : "unlabelled";
}
