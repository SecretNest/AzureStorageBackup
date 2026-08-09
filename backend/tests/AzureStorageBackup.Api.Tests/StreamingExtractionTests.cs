using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Streaming extraction (<see cref="IFileCompressor.ExtractToStreamAsync"/>) and order-preserving listing.
/// The checker's disk-free verification rests on these two 7z behaviors, so they have to be nailed down:
/// listing order = the concatenation order of `x -so`; a member that does not exist yields empty output and **no error**.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StreamingExtractionTests : IDisposable
{
    private readonly string _dir;
    private readonly string _src;

    public StreamingExtractionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "asb-sx-" + Guid.NewGuid().ToString("N"));
        _src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(_src);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static bool SevenZip() => SevenZipArchiveCodec.TryResolveExecutable() is not null;

    private async Task<string> WriteFileAsync(string relPath, int size)
    {
        var full = Path.Combine(_src, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        await File.WriteAllBytesAsync(full, bytes);
        return full;
    }

    [SkippableFact]
    public async Task Streams_A_Named_Member_From_An_Encrypted_Split_Archive()
    {
        Skip.IfNot(SevenZip(), "7z not found");

        var one = await WriteFileAsync("a/b/one.bin", 300_000);
        var two = await WriteFileAsync("c/two.bin", 120_000);
        var compressor = new SevenZipCompressor();
        var archive = Path.Combine(_dir, "enc.7z");

        // Encryption + header encryption + volume splitting: with all three on at once we must still stream out member by member (the core acceptance criterion of this phase).
        var result = await compressor.CompressAsync(new CompressionRequest(
            _src, ["a/b/one.bin", "c/two.bin"], archive, Password: "pw", VolumeBytes: 64 * 1024));
        Assert.True(result.VolumeFiles.Count > 1, "expected a split archive");

        foreach (var (entry, source) in new[] { ("a/b/one.bin", one), ("c/two.bin", two) })
        {
            var hasher = new StreamingHasher(0, 0);
            await using var sink = new HashingStream(hasher);
            var written = await compressor.ExtractToStreamAsync(result.VolumeFiles[0], entry, "pw", sink);

            Assert.Equal(new FileInfo(source).Length, written);
            Assert.Equal(written, hasher.Length);
            Assert.Equal(await new FileHasher().FullHashAsync(source), hasher.FullHash);
        }
    }

    [SkippableFact]
    public async Task Whole_Archive_Stream_Concatenates_In_Listing_Order()
    {
        Skip.IfNot(SevenZip(), "7z not found");

        // The entry names are deliberately not in lexicographic order, and an empty file is mixed in: 7z decides the
        // layout inside the archive itself, so all we can lean on is "listing order = output order" — this case nails that down.
        var files = new[] { "z.bin", "a/b/one.bin", "empty.bin", "m/two.bin" };
        var sizes = new[] { 5_000, 300_000, 0, 77 };
        for (var i = 0; i < files.Length; i++)
            await WriteFileAsync(files[i], sizes[i]);

        var compressor = new SevenZipCompressor();
        var archive = Path.Combine(_dir, "pack.7z");
        var result = await compressor.CompressAsync(new CompressionRequest(_src, files, archive, Password: "pw"));

        var listing = await compressor.ListEntriesAsync(result.VolumeFiles[0], "pw");
        var members = listing.Where(e => !e.IsDirectory).Select(e => (e.Name, e.Size)).ToList();
        Assert.Equal(files.Length, members.Count);

        var got = new Dictionary<string, (long Length, string Hash)>(StringComparer.Ordinal);
        var splitter = new SegmentHashingStream(members, (n, l, h) => got[n] = (l, h));
        await using (splitter)
        {
            await compressor.ExtractToStreamAsync(result.VolumeFiles[0], entryName: null, "pw", splitter);
            splitter.Finish();
        }

        Assert.Equal(0, splitter.ExtraBytes);
        Assert.Equal(members.Count, splitter.CompletedSegments);
        var hasher = new FileHasher();
        foreach (var rel in files)
        {
            var full = Path.Combine(_src, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(
                (new FileInfo(full).Length, await hasher.FullHashAsync(full)),
                got[rel]);
        }
    }

    [SkippableFact]
    public async Task Missing_Member_Yields_Empty_Output_Without_Failing()
    {
        Skip.IfNot(SevenZip(), "7z not found");

        await WriteFileAsync("present.bin", 1_000);
        var compressor = new SevenZipCompressor();
        var archive = Path.Combine(_dir, "one.7z");
        var result = await compressor.CompressAsync(new CompressionRequest(_src, ["present.bin"], archive));

        var hasher = new StreamingHasher(0, 0);
        await using var sink = new HashingStream(hasher);
        // This is exactly the pitfall to guard against: for a member that does not exist 7z reports **no error**, it simply outputs nothing.
        // So this assertion is not "the nice behavior we want" but a record of a trap the caller has to catch itself.
        var written = await compressor.ExtractToStreamAsync(result.VolumeFiles[0], "absent.bin", null, sink);

        Assert.Equal(0, written);
        Assert.Equal(0, hasher.Length);
    }

    [SkippableTheory]
    [InlineData(null, null, false)]      // plain compression
    [InlineData("pw", null, false)]      // encryption + header encryption
    [InlineData("pw", 64 * 1024L, false)] // encryption + volume splitting
    [InlineData(null, null, true)]       // store-only (no compression, still wrapped by 7z)
    public async Task Streamed_Archive_Holds_Exactly_The_Bytes_That_Were_Fed(
        string? password, long? volumeBytes, bool storeOnly)
    {
        Skip.IfNot(SevenZip(), "7z not found");

        var source = await WriteFileAsync("a/b/payload.bin", 250_000);
        var compressor = new SevenZipCompressor();
        var archive = Path.Combine(_dir, $"s{password}{volumeBytes}{storeOnly}.7z");

        var fed = new StreamingHasher(4096, 4096);
        var result = await compressor.CompressStreamAsync(
            new StreamCompressionRequest("a/b/payload.bin", archive, password, volumeBytes, storeOnly,
                ExpectedBytes: new FileInfo(source).Length),
            async (stdin, token) =>
            {
                await using var input = FileHasher.OpenRead(source);
                await using var sink = new HashingStream(fed, stdin);
                await input.CopyToAsync(sink, token);
                return fed.Length;
            });

        // The entry name keeps the full relative path — so the restore and check logic can locate a member without caring how the archive was produced.
        var entry = Assert.Single(
            await compressor.ListEntriesAsync(result.VolumeFiles[0], password), e => !e.IsDirectory);
        Assert.Equal("a/b/payload.bin", entry.Name);

        var back = new StreamingHasher(4096, 4096);
        await using (var sink = new HashingStream(back))
            await compressor.ExtractToStreamAsync(result.VolumeFiles[0], "a/b/payload.bin", password, sink);

        var file = new FileHasher();
        Assert.Equal(await file.FullHashAsync(source), back.FullHash);
        Assert.Equal(await file.HeadHashAsync(source, 4096), fed.HeadHash);
        Assert.Equal(await file.TailHashAsync(source, 4096), fed.TailHash);
        Assert.Equal(new FileInfo(source).Length, back.Length);
    }

    [SkippableFact]
    public async Task Canceling_A_Streaming_Compression_Leaves_No_Half_Written_Archive()
    {
        Skip.IfNot(SevenZip(), "7z not found");

        using var cts = new CancellationTokenSource();
        var archive = Path.Combine(_dir, "half.7z");
        var task = new SevenZipCompressor().CompressStreamAsync(
            new StreamCompressionRequest("x.bin", archive),
            async (stdin, token) =>
            {
                await stdin.WriteAsync(new byte[64 * 1024], token);
                await Task.Delay(Timeout.Infinite, token); // park here and wait for cancellation to break in
                return 0L;
            }, cts.Token);

        await Task.Delay(300);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        // A half-written archive is still a valid 7z file; leave it lying around and the staging area picks it up as an artifact and uploads it.
        Assert.Empty(Directory.EnumerateFiles(_dir, "half.7z*"));
    }

    [SkippableFact]
    public async Task A_Source_Failure_While_Feeding_Is_Not_Swallowed()
    {
        Skip.IfNot(SevenZip(), "7z not found");

        var archive = Path.Combine(_dir, "failed.7z");
        await Assert.ThrowsAsync<IOException>(() => new SevenZipCompressor().CompressStreamAsync(
            new StreamCompressionRequest("x.bin", archive),
            async (stdin, token) =>
            {
                await stdin.WriteAsync(new byte[64 * 1024], token);
                throw new IOException("the source went away mid-read");
            }));

        Assert.Empty(Directory.EnumerateFiles(_dir, "failed.7z*"));
    }

    [SkippableFact]
    public async Task Listing_Reports_Sizes_And_Directories()
    {
        Skip.IfNot(SevenZip(), "7z not found");

        await WriteFileAsync("d/inner.bin", 2_048);
        Directory.CreateDirectory(Path.Combine(_src, "hollow"));

        var compressor = new SevenZipCompressor();
        var archive = Path.Combine(_dir, "dirs.7z");
        var result = await compressor.CompressAsync(new CompressionRequest(_src, ["d", "hollow"], archive));

        var listing = await compressor.ListEntriesAsync(result.VolumeFiles[0], null);
        var file = Assert.Single(listing, e => e.Name == "d/inner.bin");
        Assert.False(file.IsDirectory);
        Assert.Equal(2_048, file.Size);
        Assert.True(Assert.Single(listing, e => e.Name == "hollow").IsDirectory);
    }
}
