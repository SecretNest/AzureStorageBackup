using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 流式解压（<see cref="IFileCompressor.ExtractToStreamAsync"/>）与保序列举。
/// 检查器的免落盘校验建立在这两条 7z 行为上，所以它们必须被钉死：
/// 列举顺序 = `x -so` 的拼接顺序；成员不存在时输出为空且**不报错**。
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

        // 加密 + 头加密 + 分卷：三者同时开着仍要能逐成员流式取出（这是本期的核心验收）。
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

        // 条目名有意不按字典序给出，且掺一个空文件：归档内的排列由 7z 自己定，
        // 我们只能相信"列举顺序 = 输出顺序"，所以这里就是钉住这条的用例。
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
        // 这正是必须防的坑：7z 对不存在的成员**不报错**，只是什么都不输出。
        // 所以这个断言不是"期望的好行为"，而是记录一个必须由调用方兜住的陷阱。
        var written = await compressor.ExtractToStreamAsync(result.VolumeFiles[0], "absent.bin", null, sink);

        Assert.Equal(0, written);
        Assert.Equal(0, hasher.Length);
    }

    [SkippableTheory]
    [InlineData(null, null, false)]      // 纯压缩
    [InlineData("pw", null, false)]      // 加密 + 头加密
    [InlineData("pw", 64 * 1024L, false)] // 加密 + 分卷
    [InlineData(null, null, true)]       // store-only（不压缩，仍走 7z 封装）
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

        // 条目名保留完整相对路径——还原与检查定位成员的逻辑因此不必区分归档是怎么产出来的。
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
                await Task.Delay(Timeout.Infinite, token); // 停在这里等取消打断
                return 0L;
            }, cts.Token);

        await Task.Delay(300);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        // 半截的归档是合法的 7z 文件，留下来就会被暂存区当成产物收走并上传。
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
