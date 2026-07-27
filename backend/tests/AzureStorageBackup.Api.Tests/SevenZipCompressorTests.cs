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
}
