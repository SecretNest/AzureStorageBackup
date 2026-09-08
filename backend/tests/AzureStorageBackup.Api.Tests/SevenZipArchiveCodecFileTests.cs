using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// The file-based members exist because an index is about to be a file that can run into the hundreds of MB —
/// too large to hold twice in memory just to hand it to the byte-array codec. These tests only prove the two
/// halves are interchangeable with the byte-array halves through the same "content" entry name; the byte-array
/// round trip itself is covered by <see cref="SevenZipArchiveCodecTests"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SevenZipArchiveCodecFileTests : IDisposable
{
    private static readonly string? Exe = SevenZipArchiveCodec.TryResolveExecutable();

    private readonly string _dir;

    public SevenZipArchiveCodecFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "asb-codec-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static SevenZipArchiveCodec Codec()
    {
        Skip.IfNot(Exe is not null, "7z not found");
        return new SevenZipArchiveCodec(Exe);
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    [SkippableFact]
    public async Task EncodeFile_then_DecodeAsync_round_trips()
    {
        var codec = Codec();
        var content = new byte[3 * 1024 * 1024];
        Random.Shared.NextBytes(content);
        var inputPath = Path_("input.bin");
        await File.WriteAllBytesAsync(inputPath, content);
        var archivePath = Path_("out.7z");

        await codec.EncodeFileAsync(inputPath, archivePath, password: null);
        var archiveBytes = await File.ReadAllBytesAsync(archivePath);
        var back = await codec.DecodeAsync(archiveBytes, password: null);

        Assert.Equal(content, back);
    }

    [SkippableFact]
    public async Task EncodeAsync_then_DecodeFile_round_trips()
    {
        var codec = Codec();
        var content = new byte[1024];
        Random.Shared.NextBytes(content);

        var archiveBytes = await codec.EncodeAsync(content, password: "corr3ct h0rse!");
        var archivePath = Path_("out.7z");
        await File.WriteAllBytesAsync(archivePath, archiveBytes);
        var outputPath = Path_("output.bin");

        await codec.DecodeFileAsync(archivePath, outputPath, password: "corr3ct h0rse!");
        var back = await File.ReadAllBytesAsync(outputPath);

        Assert.Equal(content, back);
    }
}
