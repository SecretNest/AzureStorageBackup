using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Streaming three-segment hashing and segment splitting. The load-bearing point: <see cref="StreamingHasher"/> and
/// <see cref="FileHasher"/> must produce the **same string** — hashes in the index can be written by either path, and being off by even a little means comparison across the whole store stops working.
/// </summary>
public sealed class StreamingHashTests : IDisposable
{
    private readonly string _dir;

    public StreamingHashTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "asb-sh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    // The sizes deliberately straddle both sides of the head/tail segment length (64), and cover the empty file and exact-length cases.
    [InlineData(0, 64)]
    [InlineData(1, 64)]
    [InlineData(63, 64)]
    [InlineData(64, 64)]
    [InlineData(65, 64)]
    [InlineData(100_000, 64)]
    [InlineData(100_000, 4096)]
    public async Task Streaming_Hashes_Match_FileHasher(int size, int segmentBytes)
    {
        var bytes = new byte[size];
        Random.Shared.NextBytes(bytes);
        var path = Path.Combine(_dir, $"f{size}-{segmentBytes}.bin");
        await File.WriteAllBytesAsync(path, bytes);

        var file = new FileHasher();
        var expectedHead = await file.HeadHashAsync(path, segmentBytes);
        var expectedTail = await file.TailHashAsync(path, segmentBytes);
        var expectedFull = await file.FullHashAsync(path);

        // Fed in chunks whose lengths are deliberately not integer multiples of the segment length, to force the ring buffer down its "wraps around" branch.
        var streaming = new StreamingHasher(segmentBytes, segmentBytes);
        var offset = 0;
        var chunk = 7;
        while (offset < bytes.Length)
        {
            var take = Math.Min(chunk, bytes.Length - offset);
            streaming.Append(bytes.AsSpan(offset, take));
            offset += take;
            chunk = chunk * 3 % 1000 + 1;
        }

        Assert.Equal(size, streaming.Length);
        Assert.Equal(expectedHead, streaming.HeadHash);
        Assert.Equal(expectedTail, streaming.TailHash);
        Assert.Equal(expectedFull, streaming.FullHash);
    }

    [Fact]
    public async Task Streaming_Hashes_Match_FileHasher_When_Fed_In_One_Go()
    {
        var bytes = new byte[300_000];
        Random.Shared.NextBytes(bytes);
        var path = Path.Combine(_dir, "one-go.bin");
        await File.WriteAllBytesAsync(path, bytes);

        var streaming = new StreamingHasher(1024, 1024);
        streaming.Append(bytes);

        var file = new FileHasher();
        Assert.Equal(await file.HeadHashAsync(path, 1024), streaming.HeadHash);
        Assert.Equal(await file.TailHashAsync(path, 1024), streaming.TailHash);
        Assert.Equal(await file.FullHashAsync(path), streaming.FullHash);
    }

    [Fact]
    public async Task HashingStream_Forwards_To_Inner_And_Hashes()
    {
        var bytes = new byte[50_000];
        Random.Shared.NextBytes(bytes);
        var source = Path.Combine(_dir, "src.bin");
        var copy = Path.Combine(_dir, "copy.bin");
        await File.WriteAllBytesAsync(source, bytes);

        var hasher = new StreamingHasher(16, 16);
        await using (var dest = File.Create(copy))
        await using (var sink = new HashingStream(hasher, dest))
        await using (var src = File.OpenRead(source))
            await src.CopyToAsync(sink);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(copy));
        Assert.Equal(await new FileHasher().FullHashAsync(source), hasher.FullHash);
    }

    [Fact]
    public async Task SegmentHashingStream_Splits_By_Length()
    {
        var segments = new (string Name, long Length)[] { ("a", 10), ("empty", 0), ("b", 5000), ("c", 1) };
        var payload = new byte[10 + 0 + 5000 + 1];
        Random.Shared.NextBytes(payload);

        var got = new Dictionary<string, (long Length, string Hash)>(StringComparer.Ordinal);
        var splitter = new SegmentHashingStream(segments, (n, l, h) => got[n] = (l, h));
        await using (var source = new MemoryStream(payload))
            await source.CopyToAsync(splitter, bufferSize: 13); // Small chunks, deliberately misaligned with the segment boundaries
        splitter.Finish();

        Assert.Equal(0, splitter.ExtraBytes);
        Assert.Equal(segments.Length, splitter.CompletedSegments);

        var offset = 0;
        foreach (var (name, length) in segments)
        {
            var expected = new StreamingHasher(0, 0);
            expected.Append(payload.AsSpan(offset, (int)length));
            Assert.Equal((length, expected.FullHash), got[name]);
            offset += (int)length;
        }
    }

    [Fact]
    public async Task SegmentHashingStream_Flags_Extra_Bytes()
    {
        var splitter = new SegmentHashingStream([("a", 4)], (_, _, _) => { });
        await using (var source = new MemoryStream(new byte[10]))
            await source.CopyToAsync(splitter);
        splitter.Finish();

        Assert.Equal(6, splitter.ExtraBytes);
    }

    [Fact]
    public async Task SegmentHashingStream_Leaves_Unfilled_Segment_Unreported()
    {
        var got = new List<string>();
        var splitter = new SegmentHashingStream([("a", 4), ("b", 100)], (n, _, _) => got.Add(n));
        await using (var source = new MemoryStream(new byte[10]))
            await source.CopyToAsync(splitter);
        splitter.Finish();

        // "b" only received 6 bytes; unfilled, it must not count as verified — the caller treats a lookup miss as a mismatch.
        Assert.Equal(["a"], got);
        Assert.Equal(1, splitter.CompletedSegments);
    }
}
