using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// "Same input, same switches, and the volumes 7z produces are byte-for-byte identical" — this assertion is not idle
/// trivia, it is the **only** grounds on which the plaintext path of
/// <see cref="BackupOrchestrator.ClearLeftoverVolumesAsync"/> dares to bail out early.
/// <para>
/// Per-volume upload is if-missing: retrying a multi-volume archive skips the volumes the first attempt already
/// landed, and the gaps are filled by the output of the second compression. If the two compressions are not
/// byte-for-byte identical, that family of volumes in the cloud is a **mixture of two compressions** — it cannot be
/// opened, while the index claims it is perfectly fine. That is silent data corruption, not a performance issue.
/// </para>
/// <para>
/// The suspend gate (Task 7) turned this path from "you need a whole run to crash once to hit it" into "every flaky
/// retry walks it", so it is nailed down here: change the 7z version, touch the -m switches, alter the dictionary
/// computation — the moment any one of those makes the output non-deterministic, this test must go red first, rather than waiting for some user's restore to fail.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SevenZipDeterminismTests : IDisposable
{
    private readonly string _dir;
    private static readonly string? Exe = SevenZipArchiveCodec.TryResolveExecutable();

    public SevenZipDeterminismTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "asb-det-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private SevenZipCompressor Compressor()
    {
        Skip.If(Exe is null, "7z executable not found on PATH.");
        return new SevenZipCompressor(Exe);
    }

    /// <summary>
    /// Content that compresses, but does not compress away: the first half is fixed-seed pseudo-random bytes
    /// (incompressible), the second half is a straight copy of the first (LZMA eats it with a single match). The ratio
    /// therefore stays steady around 2:1 — all zeros would compress down to a few KB and never split into multiple
    /// volumes at all, and what this test asks about is precisely whether **multi-volume** output is deterministic.
    /// </summary>
    private static byte[] Payload(int size, int seed = 20260807)
    {
        var bytes = new byte[size];
        var half = size / 2;
        new Random(seed).NextBytes(bytes.AsSpan(0, half));
        bytes.AsSpan(0, size - half).CopyTo(bytes.AsSpan(half));
        return bytes;
    }

    private static void AssertVolumesIdentical(IReadOnlyList<string> first, IReadOnlyList<string> second, string what)
    {
        Assert.True(first.Count > 1, $"{what}: expected a multi-volume archive, got {first.Count} volume(s).");
        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            var a = File.ReadAllBytes(first[i]);
            var b = File.ReadAllBytes(second[i]);
            Assert.True(a.AsSpan().SequenceEqual(b),
                $"{what}: volume {i + 1} differs between two compressions of identical input "
                + $"({a.Length} vs {b.Length} bytes). Retrying a partially-uploaded volume set would mix them.");
        }
    }

    // The pack path: ProcessPackAsync → CompressPackAsync → CompressAsync (compressing by file name, through argv).
    [SkippableFact]
    public async Task Pack_compression_is_byte_identical_across_two_runs()
    {
        var compressor = Compressor();
        var src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "a.bin"), Payload(5_000_000));
        File.WriteAllBytes(Path.Combine(src, "b.bin"), Payload(3_000_000, seed: 7));

        async Task<IReadOnlyList<string>> Once(string tag)
        {
            var outDir = Path.Combine(_dir, tag);
            Directory.CreateDirectory(outDir);
            var r = await compressor.CompressAsync(new CompressionRequest(
                src, ["a.bin", "b.bin"], Path.Combine(outDir, "p0001.7z"),
                Password: null, VolumeBytes: 1_000_000, StoreOnly: false));
            return r.VolumeFiles;
        }

        AssertVolumesIdentical(await Once("pack1"), await Once("pack2"), "pack");
    }

    /// <summary>
    /// The single-file blob path (HandleBlobAsync → CompressStreamAsync, <c>-si</c> reading from a pipe) is **not**
    /// deterministic, and a mixed family of volumes simply cannot be opened. This is the measured basis for
    /// <see cref="BackupOrchestrator.ClearLeftoverVolumesAsync"/> having to clear leftovers for plaintext multi-volume output too.
    /// <para>
    /// Measured (7-Zip 26.00): when stdin is a pipe, 7z cannot get the source file's mtime, so it writes **the moment
    /// of compression** into the member's kMTime. The trailing header lives in the last volume, and its CRC is
    /// recorded in the signature header of volume 1 — so between two compressions both volume 1 and the last volume
    /// differ. Taking volume 1 from the previous attempt and the rest from this one is exactly the family of volumes that really shows up in the cloud when a per-volume if-missing upload is retried.
    /// </para>
    /// <para>
    /// If some day a different 7z makes this path deterministic too, this test will go red — that is good news, not a
    /// regression: switch it to <see cref="AssertVolumesIdentical"/> and the leftover-clearing stays correct (it also
    /// guards against leftovers across runs, a layer that has nothing to do with determinism).
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Streamed_single_file_volumes_differ_across_runs_and_a_mixed_set_cannot_be_opened()
    {
        var compressor = Compressor();
        var payload = Payload(8_000_000);

        async Task<IReadOnlyList<string>> Once(string tag)
        {
            var outDir = Path.Combine(_dir, tag);
            Directory.CreateDirectory(outDir);
            var r = await compressor.CompressStreamAsync(
                new StreamCompressionRequest("big.bin", Path.Combine(outDir, "b.7z"),
                    Password: null, VolumeBytes: 1_000_000, StoreOnly: false, ExpectedBytes: payload.Length),
                async (stdin, token) =>
                {
                    await stdin.WriteAsync(payload, token);
                    return payload.Length;
                });
            return r.VolumeFiles;
        }

        // A second between the two runs: if a timestamp made it into the archive, one-second resolution is enough to expose it.
        var first = await Once("blob1");
        await Task.Delay(1100);
        var second = await Once("blob2");

        Assert.True(first.Count > 1, $"expected a multi-volume archive, got {first.Count} volume(s).");
        Assert.Equal(first.Count, second.Count);

        var differing = Enumerable.Range(0, first.Count)
            .Where(i => !File.ReadAllBytes(first[i]).AsSpan().SequenceEqual(File.ReadAllBytes(second[i])))
            .ToList();
        Assert.NotEmpty(differing);   // non-deterministic: precisely why we cannot bail out early

        // Each family of volumes is fine on its own — what is broken is mixing them.
        Assert.Equal(payload.Length, await ExtractedLength(compressor, first[0]));
        Assert.Equal(payload.Length, await ExtractedLength(compressor, second[0]));

        // Mixed set: volume 1 from the previous attempt, the rest from this one (exactly what the cloud holds on an if-missing retry).
        var mixed = Path.Combine(_dir, "mixed");
        Directory.CreateDirectory(mixed);
        for (var i = 0; i < first.Count; i++)
            File.Copy(i == 0 ? first[i] : second[i], Path.Combine(mixed, Path.GetFileName(first[i])));

        var boom = await Record.ExceptionAsync(() =>
            ExtractedLength(compressor, Path.Combine(mixed, Path.GetFileName(first[0]))));
        Assert.NotNull(boom); // the index says it is perfectly fine, 7z says it cannot be opened — this is silent data corruption
    }

    private static async Task<long> ExtractedLength(SevenZipCompressor compressor, string firstVolume)
    {
        await using var sink = new MemoryStream();
        return await compressor.ExtractToStreamAsync(firstVolume, entryName: null, password: null, sink);
    }
}
