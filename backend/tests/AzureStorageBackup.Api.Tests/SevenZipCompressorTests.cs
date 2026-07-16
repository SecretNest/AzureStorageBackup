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
}
