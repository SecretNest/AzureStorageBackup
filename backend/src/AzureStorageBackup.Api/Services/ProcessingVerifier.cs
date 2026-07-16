namespace AzureStorageBackup.Api.Services;

public enum ProcessingOutcome
{
    /// <summary>处理后文件稳定（未变，或仅元数据抖动而内容不变）。</summary>
    Stable,

    /// <summary>反复变更达阈值，报警并以当前内容保存，停止重试。</summary>
    Alarmed,
}

public sealed record ProcessingResult(ProcessingOutcome Outcome, int Attempts, string FullHash);

public sealed record VerificationOptions
{
    /// <summary>反复重处理上限（默认 5，M4 §9，env 可配）。</summary>
    public int MaxAttempts { get; init; } = 5;

    public int HeadHashBytes { get; init; } = 4096;
}

/// <summary>
/// 处理后重校验（M4 设计 §9）：处理完重查 mtime/权限 → 变则重算 hash → hash 变则重处理。
/// 反复达阈值即报警、以当前版本保存、停重试。
/// </summary>
public sealed class ProcessingVerifier(IFileHasher hasher)
{
    public async Task<ProcessingResult> RunAsync(
        string path,
        string expectedFullHash,
        Func<CancellationToken, Task> process,
        VerificationOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new VerificationOptions();
        var expected = expectedFullHash;
        var attempts = 0;

        while (true)
        {
            attempts++;
            var before = Stat(path);
            await process(ct);
            var after = Stat(path);

            // mtime+权限+length 都没变 → 处理期间文件稳定。
            if (after == before)
                return new ProcessingResult(ProcessingOutcome.Stable, attempts, expected);

            // 元数据变了 → 重算 hash 判断内容是否真的变。
            var current = await hasher.FullHashAsync(path, ct);
            if (current == expected)
                return new ProcessingResult(ProcessingOutcome.Stable, attempts, expected);

            // 内容变了：以新内容为准重处理；达阈值则报警并停。
            expected = current;
            if (attempts >= options.MaxAttempts)
                return new ProcessingResult(ProcessingOutcome.Alarmed, attempts, expected);
        }
    }

    private static (long Mtime, long Length, int Mode) Stat(string path)
    {
        var info = new FileInfo(path);
        var mode = OperatingSystem.IsWindows() ? 0 : (int)File.GetUnixFileMode(path);
        return (info.LastWriteTimeUtc.Ticks, info.Length, mode);
    }
}
