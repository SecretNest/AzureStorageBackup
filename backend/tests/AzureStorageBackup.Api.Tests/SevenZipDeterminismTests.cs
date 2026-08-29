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
    /// The streamed single-file path is now deterministic **by construction**: 7z reading from a pipe cannot see
    /// the source mtime, so it used to stamp the moment of compression into the member's kMTime — the trailing
    /// header lives in the last volume, its CRC is recorded in volume 1's signature header, so two runs differed
    /// in exactly those two volumes (measured, 7-Zip 26.00). The stamp has no consumer in this product (display
    /// uses index metadata, restore resets times from the index), and <c>-mtm=off</c> removes it. Full byte
    /// identity is what the per-volume skip machinery prices its savings on (volume-identity.md), so this pins it:
    /// any 7z upgrade or switch change that breaks it must go red here, not on a user's bandwidth bill.
    /// </summary>
    [SkippableFact]
    public async Task Streamed_single_file_volumes_are_byte_identical_across_two_runs()
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

        // A second between the two runs: if a timestamp still made it into the archive, one-second resolution is enough to expose it.
        var first = await Once("blob1");
        await Task.Delay(1100);
        var second = await Once("blob2");

        Assert.True(first.Count > 1, $"expected a multi-volume archive, got {first.Count} volume(s).");
        AssertVolumesIdentical(first, second, "streamed single-file blob");
        // And the archive is still a working archive: suppressing the timestamp must cost nothing but the timestamp.
        Assert.Equal(payload.Length, await ExtractedLength(compressor, first[0]));
    }

    /// <summary>
    /// The mixture hazard outlives determinism: same content compressed under **different method switches** (here,
    /// different dictionary sizes — exactly what an operator changing <c>Backup__SevenZipMethodArgs</c> or a 7z
    /// upgrade produces) yields families that differ byte-wise, and a family spliced from both cannot be opened
    /// while the index calls it perfectly fine. This is why an unlabelled or label-mismatched cloud volume is
    /// always **overwritten**, never kept on faith (volume-identity.md § the one comparison rule).
    /// </summary>
    [SkippableFact]
    public async Task A_Family_Mixed_From_Two_Method_Configurations_Cannot_Be_Opened()
    {
        Skip.If(Exe is null, "7z executable not found on PATH.");
        var payload = Payload(8_000_000);

        async Task<IReadOnlyList<string>> Once(string tag, string? methodArgs)
        {
            var outDir = Path.Combine(_dir, tag);
            Directory.CreateDirectory(outDir);
            var r = await new SevenZipCompressor(Exe, methodArgs: methodArgs).CompressStreamAsync(
                new StreamCompressionRequest("big.bin", Path.Combine(outDir, "b.7z"),
                    Password: null, VolumeBytes: 1_000_000, StoreOnly: false, ExpectedBytes: payload.Length),
                async (stdin, token) =>
                {
                    await stdin.WriteAsync(payload, token);
                    return payload.Length;
                });
            return r.VolumeFiles;
        }

        var first = await Once("era1", "-md=1m");
        var second = await Once("era2", "-md=4m");
        Assert.True(first.Count > 1 && second.Count > 1,
            $"the eras must both split ({first.Count} and {second.Count} volumes).");

        // Each era opens on its own; the splice does not.
        var compressor = Compressor();
        Assert.Equal(payload.Length, await ExtractedLength(compressor, first[0]));
        Assert.Equal(payload.Length, await ExtractedLength(compressor, second[0]));

        // The cloud's real failure shape: the new era overwrote most volumes at their names, but one leftover of
        // the old era survived at its name — here, era1's first volume standing where era2's should be.
        var mixed = Path.Combine(_dir, "mixed");
        Directory.CreateDirectory(mixed);
        for (var i = 0; i < second.Count; i++)
            File.Copy(second[i], Path.Combine(mixed, Path.GetFileName(second[i])));
        File.Copy(first[0], Path.Combine(mixed, Path.GetFileName(second[0])), overwrite: true);

        var boom = await Record.ExceptionAsync(() =>
            ExtractedLength(compressor, Path.Combine(mixed, Path.GetFileName(second[0]))));
        Assert.NotNull(boom); // the index says it is perfectly fine, 7z says it cannot be opened — silent data corruption
    }

    private static async Task<long> ExtractedLength(SevenZipCompressor compressor, string firstVolume)
    {
        await using var sink = new MemoryStream();
        return await compressor.ExtractToStreamAsync(firstVolume, entryName: null, password: null, sink);
    }
}
