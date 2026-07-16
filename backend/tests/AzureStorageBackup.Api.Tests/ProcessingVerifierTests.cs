using System.Text;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class ProcessingVerifierTests : IDisposable
{
    private readonly string _dir;

    public ProcessingVerifierTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "asb-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string name, string content)
    {
        var full = Path.Combine(_dir, name);
        File.WriteAllText(full, content);
        return full;
    }

    private static readonly IFileHasher Hasher = new FileHasher();
    private static ProcessingVerifier Verifier() => new(Hasher);
    private static Task<string> Hash(string path) => Hasher.FullHashAsync(path);

    [Fact]
    public async Task Stable_File_Passes_On_First_Attempt()
    {
        var path = Write("a.txt", "content");
        var expected = await Hash(path);
        var processed = 0;

        var result = await Verifier().RunAsync(path, expected, _ => { processed++; return Task.CompletedTask; });

        Assert.Equal(ProcessingOutcome.Stable, result.Outcome);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, processed);
        Assert.Equal(expected, result.FullHash);
    }

    [Fact]
    public async Task Metadata_Only_Flutter_Is_Stable_Without_Reprocess()
    {
        var path = Write("a.txt", "content");
        var expected = await Hash(path);

        // 处理中仅 mtime 变，内容不变 → 重算 hash 相同 → 稳定，不重处理。
        var result = await Verifier().RunAsync(path, expected, _ =>
        {
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(10));
            return Task.CompletedTask;
        });

        Assert.Equal(ProcessingOutcome.Stable, result.Outcome);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(expected, result.FullHash);
    }

    [Fact]
    public async Task Content_Change_During_Processing_Triggers_Reprocess_Then_Stabilizes()
    {
        var path = Write("a.txt", "original");
        var expected = await Hash(path);
        var attempt = 0;

        var result = await Verifier().RunAsync(path, expected, _ =>
        {
            if (attempt++ == 0) // 只在第一次处理时把文件改掉
            {
                File.WriteAllText(path, "changed!!");
                File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
            }
            return Task.CompletedTask;
        });

        Assert.Equal(ProcessingOutcome.Stable, result.Outcome);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(await Hash(path), result.FullHash); // 收敛到新内容的 hash
    }

    [Fact]
    public async Task Endless_Change_Alarms_At_Threshold()
    {
        var path = Write("a.txt", "v0");
        var expected = await Hash(path);
        var n = 0;

        var result = await Verifier().RunAsync(path, expected, _ =>
        {
            n++;
            File.WriteAllText(path, "v" + n + Guid.NewGuid().ToString("N"));
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(n));
            return Task.CompletedTask;
        }, new VerificationOptions { MaxAttempts = 3 });

        Assert.Equal(ProcessingOutcome.Alarmed, result.Outcome);
        Assert.Equal(3, result.Attempts);
    }
}
