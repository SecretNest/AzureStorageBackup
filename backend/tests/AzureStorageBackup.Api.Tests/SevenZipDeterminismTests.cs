using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 「同样的输入配同样的参数，7z 压出来的卷逐字节相同」——这条断言不是趣味考据，它是
/// <see cref="BackupOrchestrator.ClearLeftoverVolumesAsync"/> 明文路径敢于早退的**唯一**依据。
/// <para>
/// 逐卷上传是 if-missing 的：重试一次多卷归档时，第 1 次尝试已经落地的卷会被跳过，缺口由
/// 第 2 次压缩的产物填上。两次压缩若不逐字节相同，云上那一族卷就是**两次压缩的混合体**——
/// 解不开，而索引却声称它好好的。这是静默的数据损坏，不是性能问题。
/// </para>
/// <para>
/// 挂起闸门（Task 7）把这条路从"要整轮运行崩一次才踩得到"变成"每次抖动重试都要走一遍"，
/// 所以这里把它钉死：换 7z 版本、动 -m 参数、改词典推算，任何一处让产出不再确定，
/// 这个测试必须先红，而不是等某个用户的还原失败。
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
    /// 可压缩、但压不没的内容：前一半是定种子伪随机字节（压不动），后一半照抄前一半（LZMA 一句
    /// 匹配就吃掉）。压缩率因此稳定在 2:1 上下——全 0 会压成几 KB，根本切不出多卷，而这个测试
    /// 要问的恰恰是**多卷**产出确不确定。
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

    // pack 路径：ProcessPackAsync → CompressPackAsync → CompressAsync（按文件名压，走 argv）。
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
    /// 单文件 blob 路径（HandleBlobAsync → CompressStreamAsync，<c>-si</c> 从管道读）**不**确定，
    /// 而且混起来的一族卷根本打不开。这一条是 <see cref="BackupOrchestrator.ClearLeftoverVolumesAsync"/>
    /// 明文多卷也必须清残留的实测依据。
    /// <para>
    /// 实测（7-Zip 26.00）：stdin 是管道时 7z 拿不到源文件的 mtime，就把**压缩的那一刻**写进成员的
    /// kMTime。归档尾部的头存在最后一卷里，而它的 CRC 又记在第 1 卷的签名头里——于是两次压缩的
    /// 第 1 卷和最后一卷都不同。把第 1 卷取自上一次尝试、其余取自这一次，就是逐卷 if-missing 上传
    /// 重试时云上真会出现的那族卷。
    /// </para>
    /// <para>
    /// 若某天换了个 7z 让这条路也确定了，这个测试会红——那是好消息，不是回归：改成
    /// <see cref="AssertVolumesIdentical"/> 即可，清残留那一笔仍然是对的（它同时挡着跨轮残留，
    /// 那一层跟确不确定无关）。
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

        // 两次之间隔开一秒：时间戳若进了归档，1 秒的分辨率足以让它露出来。
        var first = await Once("blob1");
        await Task.Delay(1100);
        var second = await Once("blob2");

        Assert.True(first.Count > 1, $"expected a multi-volume archive, got {first.Count} volume(s).");
        Assert.Equal(first.Count, second.Count);

        var differing = Enumerable.Range(0, first.Count)
            .Where(i => !File.ReadAllBytes(first[i]).AsSpan().SequenceEqual(File.ReadAllBytes(second[i])))
            .ToList();
        Assert.NotEmpty(differing);   // 不确定：这正是不能早退的理由

        // 两族卷各自都是好的——坏的只是把它们混起来。
        Assert.Equal(payload.Length, await ExtractedLength(compressor, first[0]));
        Assert.Equal(payload.Length, await ExtractedLength(compressor, second[0]));

        // 混装：第 1 卷来自上一次尝试，其余来自这一次（if-missing 重传时云上就是这样）。
        var mixed = Path.Combine(_dir, "mixed");
        Directory.CreateDirectory(mixed);
        for (var i = 0; i < first.Count; i++)
            File.Copy(i == 0 ? first[i] : second[i], Path.Combine(mixed, Path.GetFileName(first[i])));

        var boom = await Record.ExceptionAsync(() =>
            ExtractedLength(compressor, Path.Combine(mixed, Path.GetFileName(first[0]))));
        Assert.NotNull(boom); // 索引会说它好好的，7z 说打不开——这就是静默的数据损坏
    }

    private static async Task<long> ExtractedLength(SevenZipCompressor compressor, string firstVolume)
    {
        await using var sink = new MemoryStream();
        return await compressor.ExtractToStreamAsync(firstVolume, entryName: null, password: null, sink);
    }
}
