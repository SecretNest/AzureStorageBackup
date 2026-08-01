using System.Diagnostics;
using System.Text;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public sealed class SevenZipCompressorTests : IDisposable
{
    private readonly string _dir;
    private static readonly string? Exe = SevenZipArchiveCodec.TryResolveExecutable();

    public SevenZipCompressorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "asb-zc-" + Guid.NewGuid().ToString("N"));
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

    private string SourceDir()
    {
        var d = Path.Combine(_dir, "src");
        Directory.CreateDirectory(d);
        return d;
    }

    private static string WriteInto(string dir, string name, byte[] content)
    {
        var full = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    [SkippableFact]
    public async Task Compresses_Multiple_Files_And_Extracts_RoundTrip()
    {
        var compressor = Compressor();
        var src = SourceDir();
        WriteInto(src, "a.txt", Encoding.UTF8.GetBytes("alpha"));
        WriteInto(src, "sub/b.txt", Encoding.UTF8.GetBytes("bravo"));

        var archive = Path.Combine(_dir, "out.7z");
        var result = await compressor.CompressAsync(new CompressionRequest(src, ["a.txt", "sub/b.txt"], archive));

        Assert.Single(result.VolumeFiles);

        var outDir = Path.Combine(_dir, "extracted");
        await compressor.ExtractAsync(result.VolumeFiles[0], outDir, password: null);

        Assert.Equal("alpha", File.ReadAllText(Path.Combine(outDir, "a.txt")));
        Assert.Equal("bravo", File.ReadAllText(Path.Combine(outDir, "sub", "b.txt")));
    }

    [SkippableFact]
    public async Task Splits_Into_Volumes_When_Volume_Size_Set()
    {
        var compressor = Compressor();
        var src = SourceDir();
        // 25KB 存储模式（不压缩），10KB 分卷 → 3 卷。
        WriteInto(src, "big.bin", new byte[25_000]);

        var archive = Path.Combine(_dir, "vol.7z");
        var result = await compressor.CompressAsync(
            new CompressionRequest(src, ["big.bin"], archive, VolumeBytes: 10_000, StoreOnly: true));

        Assert.True(result.VolumeFiles.Count >= 2, $"expected multiple volumes, got {result.VolumeFiles.Count}");
        Assert.All(result.VolumeFiles, f => Assert.Matches(@"\.7z\.\d{3}$", f));

        var outDir = Path.Combine(_dir, "extracted");
        await compressor.ExtractAsync(result.VolumeFiles[0], outDir, password: null);
        Assert.Equal(25_000, new FileInfo(Path.Combine(outDir, "big.bin")).Length);
    }

    [SkippableFact]
    public async Task Encrypted_Archive_RoundTrips_And_Requires_Password()
    {
        var compressor = Compressor();
        var src = SourceDir();
        WriteInto(src, "secret.txt", Encoding.UTF8.GetBytes("classified"));

        var archive = Path.Combine(_dir, "enc.7z");
        var result = await compressor.CompressAsync(new CompressionRequest(src, ["secret.txt"], archive, Password: "pw"));

        var ok = Path.Combine(_dir, "ok");
        await compressor.ExtractAsync(result.VolumeFiles[0], ok, password: "pw");
        Assert.Equal("classified", File.ReadAllText(Path.Combine(ok, "secret.txt")));

        var bad = Path.Combine(_dir, "bad");
        await Assert.ThrowsAnyAsync<Exception>(() => compressor.ExtractAsync(result.VolumeFiles[0], bad, password: null));
    }

    /// <summary>7z 对读不了的成员只报**警告**：退出码 1、把成员静默丢掉、仍产出一个完全有效的归档。
    /// 只看退出码（此前只有 >= 2 才算失败）就会让一个缺成员的包被当成正常产物上传，而索引声称该成员
    /// 在里面——只有还原或深度检查时才暴露。压缩器必须自己验收归档的实际内容。</summary>
    [SkippableFact]
    public async Task Reports_A_Member_The_Archiver_Silently_Dropped()
    {
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");
        var compressor = Compressor();
        var src = SourceDir();
        WriteInto(src, "readable.txt", Encoding.UTF8.GetBytes("fine"));
        var locked = WriteInto(src, "locked.txt", Encoding.UTF8.GetBytes("cannot be read"));
        WriteInto(src, "other.txt", Encoding.UTF8.GetBytes("also fine"));
        File.SetUnixFileMode(locked, UnixFileMode.None); // 真实权限拒绝，不是替身抛的假异常

        try
        {
            var archive = Path.Combine(_dir, "dropped.7z");
            var ex = await Assert.ThrowsAsync<ArchiveMembersMissingException>(() =>
                compressor.CompressAsync(new CompressionRequest(
                    src, ["readable.txt", "locked.txt", "other.txt"], archive)));

            // 只报真正缺席的那一个：读得到的成员不能被牵连（否则整组都会被无谓地重压/降级）。
            Assert.Equal(["locked.txt"], ex.MissingEntries);

            // 那个残缺归档不该留在磁盘上：它不可用，留着只会占空间并可能被误当成产物。
            Assert.False(File.Exists(archive));
        }
        finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    /// <summary>最坏情形，同样由真实探针确认：**所有**成员都读不了时 7z 依旧退出 1，
    /// 并产出一个有效的空归档。没有验收的话，上传的就是一个"看起来正常"的空包。</summary>
    [SkippableFact]
    public async Task Reports_Every_Member_When_The_Archive_Comes_Out_Empty()
    {
        Skip.If(OperatingSystem.IsWindows(), "Relies on Unix permission bits.");
        var compressor = Compressor();
        var src = SourceDir();
        var a = WriteInto(src, "a.txt", Encoding.UTF8.GetBytes("aaa"));
        var b = WriteInto(src, "b.txt", Encoding.UTF8.GetBytes("bbb"));
        File.SetUnixFileMode(a, UnixFileMode.None);
        File.SetUnixFileMode(b, UnixFileMode.None);

        try
        {
            var ex = await Assert.ThrowsAsync<ArchiveMembersMissingException>(() =>
                compressor.CompressAsync(new CompressionRequest(src, ["a.txt", "b.txt"], Path.Combine(_dir, "empty.7z"))));

            Assert.Equal(["a.txt", "b.txt"], ex.MissingEntries.OrderBy(m => m, StringComparer.Ordinal));
        }
        finally
        {
            File.SetUnixFileMode(a, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(b, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>加密备份用 -mhe=on（连头都加密），不给密码连条目名都列不出来。验收逻辑必须把密码
    /// 一并传给 `l`，否则加密备份要么永远验收失败、要么在 7z 等待密码输入时卡住。</summary>
    [SkippableFact]
    public async Task Accepts_A_Complete_Encrypted_Archive_Without_False_Alarm()
    {
        var compressor = Compressor();
        var src = SourceDir();
        WriteInto(src, "s1.txt", Encoding.UTF8.GetBytes("one"));
        WriteInto(src, "s2.txt", Encoding.UTF8.GetBytes("two"));

        var archive = Path.Combine(_dir, "enc-ok.7z");
        var result = await compressor.CompressAsync(
            new CompressionRequest(src, ["s1.txt", "s2.txt"], archive, Password: "pw"));

        Assert.Single(result.VolumeFiles); // 齐全的加密归档照常产出，不被误判为缺成员
    }
    /// <summary>压缩方法参数可调（Backup__SevenZipMethodArgs）：NAS 上内存和 CPU 都紧张，
    /// 换算法、缩字典、限线程都是实际诉求。归档自描述，改了不影响已有版本还原。</summary>
    [SkippableFact]
    public async Task Method_Args_From_Configuration_Are_Passed_To_7z()
    {
        Skip.If(Exe is null, "7z executable not found on PATH.");
        var src = SourceDir();
        // 高度可压的内容：-mx9 与 -mx0 的产物大小必然拉开差距。
        WriteInto(src, "z.bin", new byte[200_000]);

        var packed = await new SevenZipCompressor(Exe).CompressAsync(
            new CompressionRequest(src, ["z.bin"], Path.Combine(_dir, "default.7z")));
        var stored = await new SevenZipCompressor(Exe, "-mx0").CompressAsync(
            new CompressionRequest(src, ["z.bin"], Path.Combine(_dir, "stored.7z")));

        Assert.True(new FileInfo(stored.VolumeFiles[0]).Length > new FileInfo(packed.VolumeFiles[0]).Length,
            "-mx0 should not have compressed the input");
    }

    /// <summary>只收 -m 开头的参数。其余开关决定的是我们怎么和 7z 对话（-y/-bso0/-si/-t7z），
    /// 一次手滑就能毁掉输出解析或产出还原不了的归档——所以在构造时就拦，启动即失败。</summary>
    [Theory]
    [InlineData("-o/tmp/evil")]
    [InlineData("-mx9 -y")]
    [InlineData("-m")]
    public void Non_Method_Arguments_Are_Rejected_Up_Front(string args)
    {
        var ex = Assert.Throws<ArgumentException>(() => new SevenZipCompressor(Exe ?? "/bin/true", args));
        Assert.Contains("-m", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>配置里显式给了 -md 就照配置来：自动推算的词典只是"没人管时的默认"，
    /// 不该盖掉运维按自己机器内存定下的值。</summary>
    [SkippableFact]
    public async Task Explicit_Dictionary_Size_Survives_Streaming_Compression()
    {
        Skip.If(Exe is null, "7z executable not found on PATH.");
        var compressor = new SevenZipCompressor(Exe, "-mx9 -md=1m");
        var payload = new byte[300_000];

        var result = await compressor.CompressStreamAsync(
            new StreamCompressionRequest("s.bin", Path.Combine(_dir, "stream-md.7z"), ExpectedBytes: payload.Length),
            async (stdin, ct) => { await stdin.WriteAsync(payload, ct); return payload.Length; });

        var entries = await compressor.ListEntriesAsync(result.VolumeFiles[0], null);
        Assert.Equal(payload.Length, Assert.Single(entries).Size);
    }

    /// <summary>
    /// 降优先级不能把正常路径搞坏——压缩、列举、解压三条都得照常。
    /// <para>
    /// 不回读 <c>PriorityClass</c> 做断言：那是竞态（进程可能已经退出），而且 nice 值本身
    /// 在容器/CI 里未必调得动。这里守的是另一件事：多出来的那个 setpriority 调用，以及它
    /// 周围的 try/catch，不会让任何一条 7z 路径失败。设不上时它必须**静默**，因为一个
    /// 性能偏好绝不该炸掉一次备份。
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task Priority_Setting_Does_Not_Disturb_Any_Path()
    {
        Skip.If(Exe is null, "7z executable not found on PATH.");
        var compressor = new SevenZipCompressor(Exe, priority: () => ProcessPriorityClass.Idle);
        var src = SourceDir();
        WriteInto(src, "a.txt", Encoding.UTF8.GetBytes("alpha"));

        var archive = Path.Combine(_dir, "prio.7z");
        var result = await compressor.CompressAsync(new CompressionRequest(src, ["a.txt"], archive));

        var entries = await compressor.ListEntriesAsync(result.VolumeFiles[0], null);
        Assert.Equal("a.txt", Assert.Single(entries).Name);

        var outDir = Path.Combine(_dir, "out");
        await compressor.ExtractAsync(result.VolumeFiles[0], outDir, null);
        Assert.Equal("alpha", await File.ReadAllTextAsync(Path.Combine(outDir, "a.txt")));
    }

    /// <summary>优先级委托抛出来时也不能影响压缩：这条路上的异常来自读设置（数据库），
    /// 和归档内容没有一点关系。</summary>
    [SkippableFact]
    public async Task Throwing_Priority_Provider_Does_Not_Fail_Compression()
    {
        Skip.If(Exe is null, "7z executable not found on PATH.");
        var compressor = new SevenZipCompressor(
            Exe, priority: () => throw new TimeoutException("settings unavailable"));
        var src = SourceDir();
        WriteInto(src, "a.txt", Encoding.UTF8.GetBytes("alpha"));

        var result = await compressor.CompressAsync(
            new CompressionRequest(src, ["a.txt"], Path.Combine(_dir, "prio-throw.7z")));

        var entries = await compressor.ListEntriesAsync(result.VolumeFiles[0], null);
        Assert.Equal("a.txt", Assert.Single(entries).Name);
    }
}
