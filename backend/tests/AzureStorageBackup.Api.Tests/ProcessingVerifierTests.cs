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

        var result = await Verifier().RunAsync(path, expected, (_, _) => { processed++; return Task.CompletedTask; });

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
        var result = await Verifier().RunAsync(path, expected, (_, _) =>
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

        var result = await Verifier().RunAsync(path, expected, (_, _) =>
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

        var result = await Verifier().RunAsync(path, expected, (_, _) =>
        {
            n++;
            File.WriteAllText(path, "v" + n + Guid.NewGuid().ToString("N"));
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(n));
            return Task.CompletedTask;
        }, new VerificationOptions { MaxAttempts = 3 });

        Assert.Equal(ProcessingOutcome.Alarmed, result.Outcome);
        Assert.Equal(3, result.Attempts);
    }

    [Fact]
    public async Task Process_Receives_Current_Hash_Which_Updates_After_Content_Change()
    {
        var path = Write("a.txt", "original");
        var original = await Hash(path);
        var attempt = 0;
        var hashesSeen = new List<string>();

        var result = await Verifier().RunAsync(path, original, (hash, _) =>
        {
            hashesSeen.Add(hash);
            if (attempt++ == 0)
            {
                File.WriteAllText(path, "changed!!");
                File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
            }
            return Task.CompletedTask;
        });

        var changed = await Hash(path);
        Assert.Equal(ProcessingOutcome.Stable, result.Outcome);
        Assert.Equal(original, hashesSeen[0]);           // 首次用 diff 时的 hash
        Assert.Equal(changed, hashesSeen[1]);            // 内容变后用新 hash 重处理（决定 blob 名）
        Assert.Equal(changed, result.FullHash);
    }

    [Fact]
    public async Task Alarm_Processes_Final_Content_Under_Its_Own_Hash()
    {
        var path = Write("a.txt", "v0");
        var original = await Hash(path);
        var n = 0;
        var hashesSeen = new List<string>();

        var result = await Verifier().RunAsync(path, original, (hash, _) =>
        {
            hashesSeen.Add(hash);
            n++;
            if (n <= 3) // 前三次处理都改内容；第四次（收尾落库）保持不变
            {
                File.WriteAllText(path, "v" + n);
                File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(n));
            }
            return Task.CompletedTask;
        }, new VerificationOptions { MaxAttempts = 3 });

        Assert.Equal(ProcessingOutcome.Alarmed, result.Outcome);
        // 报警后仍以最终内容的 hash 再处理一次，保证 blob 以正确名字落库、无悬挂引用。
        Assert.Equal(result.FullHash, hashesSeen[^1]);
        Assert.Equal(await Hash(path), result.FullHash);
    }
}
