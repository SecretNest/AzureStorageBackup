using System.IO.Hashing;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The volume identity label (volume-identity.md): a blob's own xxh128, written into its metadata **with** the
/// upload — atomic with the commit, because Azure stores labels but computes nothing, and a blob that misses its
/// moment is unlabelled forever. Equality of this label is the only thing that ever justifies not uploading a
/// volume; absence of it means "different" to every skip decision, which is what makes absence safe.
/// </summary>
public static class VolumeIdentity
{
    /// <summary>The metadata key (an <c>x-ms-meta-</c> name, so it must be a valid C# identifier).</summary>
    public const string MetaKey = "xxh128";

    /// <summary>Same value and format as <see cref="IFileHasher.FullHashAsync"/> would produce for a file holding
    /// exactly these bytes — one identity, whichever side computes it.</summary>
    public static string Compute(ReadOnlySpan<byte> bytes)
        => "xxh128:" + Convert.ToHexString(XxHash128.Hash(bytes)).ToLowerInvariant();

    /// <summary>The same label computed by streaming — for the compare side of a skip decision, where the file can
    /// be a raw source of arbitrary size and buffering it whole is not on. Goes through
    /// <see cref="FileHasher.OpenRead"/> like every source-file read in this project (the FIFO rule).</summary>
    public static async Task<string> ComputeAsync(string path, CancellationToken ct = default)
    {
        await using var stream = FileHasher.OpenRead(path);
        var hash = new XxHash128();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            hash.Append(buffer.AsSpan(0, read));
        return "xxh128:" + Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant();
    }
}
