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
/// <para>
/// <paramref name="process"/> 接收「本次应使用的 fullHash」：内容寻址的存储名由该 hash 决定，
/// 因此内容在处理中变化时会用新 hash 重处理（重命名 blob），避免存储名与内容不符（§9、PRD 特别说明 D）。
/// 报警前仍以最终内容的 hash 再处理一次，保证 blob 以正确名字落库、不留悬挂引用。
/// </para>
/// <para>
/// **单文件 blob 不再走这里**：那条路径已改为边读边压，hash 算的就是压进归档的那些字节，
/// 两者按构造不可能对不上，没有需要追平的差距。分组打包仍是先 hash 后压，重校验照旧必须做——
/// 编排器把它写在成组循环里（要以"整组"为单位排除成员、重新入队），因此那边用的不是
/// <see cref="RunAsync"/>，而是把本类的**存在与否**当作"要不要做压缩后重校验"的开关。
/// </para>
/// </summary>
public sealed class ProcessingVerifier(IFileHasher hasher)
{
    public async Task<ProcessingResult> RunAsync(
        string path,
        string expectedFullHash,
        Func<string, CancellationToken, Task> process,
        VerificationOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new VerificationOptions();
        var expected = expectedFullHash;
        var before = Stat(path);
        var attempts = 0;

        while (true)
        {
            attempts++;
            await process(expected, ct);
            var after = Stat(path);

            // mtime+权限+length 都没变 → 处理期间文件稳定。
            if (after == before)
                return new ProcessingResult(ProcessingOutcome.Stable, attempts, expected);

            // 元数据变了 → 重算 hash 判断内容是否真的变。
            string current;
            try
            {
                current = await hasher.FullHashAsync(path, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读不开就无法确认内容是否稳定，继续重试没有意义：直接报警收场。
                return new ProcessingResult(ProcessingOutcome.Alarmed, attempts, expected);
            }
            if (current == expected)
                return new ProcessingResult(ProcessingOutcome.Stable, attempts, expected);

            // 内容变了：达阈值则以最终内容的 hash 再处理一次后报警；否则以新内容为准重处理。
            if (attempts >= options.MaxAttempts)
            {
                await process(current, ct);
                return new ProcessingResult(ProcessingOutcome.Alarmed, attempts, current);
            }

            before = after;
            expected = current;
        }
    }

    private static (long Mtime, long Length, int Mode) Stat(string path)
    {
        var info = new FileInfo(path);
        var mode = OperatingSystem.IsWindows() ? 0 : (int)File.GetUnixFileMode(path);
        return (info.LastWriteTimeUtc.Ticks, info.Length, mode);
    }
}
