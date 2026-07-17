namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 用官方 7-Zip 二进制实现的归档编解码（M4 决策 §13.1）。
/// 归档内固定单条目名 "content"。password 非空时 AES-256 + 头加密(-mhe=on)。
/// 通过 ArgumentList 传参（不经 shell），密码含空格/特殊字符也安全。
/// </summary>
public sealed class SevenZipArchiveCodec : IArchiveCodec
{
    private const string EntryName = "content";

    private readonly string _exe;
    private readonly string _tempRoot;

    public SevenZipArchiveCodec(string? executable = null, string? tempRoot = null)
    {
        _exe = executable ?? TryResolveExecutable()
            ?? throw new InvalidOperationException("No 7-Zip executable found on PATH.");
        _tempRoot = tempRoot ?? Path.Combine(Path.GetTempPath(), "asb-7z");
    }

    /// <summary>在 PATH 上探测 7-Zip 可执行文件（7zz→7z→7za），找不到返回 null。</summary>
    public static string? TryResolveExecutable() => SevenZipCli.TryResolveExecutable();

    public async Task<byte[]> EncodeAsync(byte[] content, string? password, CancellationToken ct = default)
    {
        var work = NewWorkDir();
        try
        {
            var input = Path.Combine(work, EntryName);
            var archive = Path.Combine(work, "out.7z");
            await File.WriteAllBytesAsync(input, content, ct);

            var args = new List<string> { "a", "-t7z", "-y", "-bso0", "-bsp0", "-mx9" }; // 最大压缩（PRD 3.3.2.1）
            if (!string.IsNullOrEmpty(password))
            {
                args.Add("-p" + password);
                args.Add("-mhe=on");
            }
            args.Add(archive);
            args.Add(input);

            await RunAsync(args, ct);
            return await File.ReadAllBytesAsync(archive, ct);
        }
        finally
        {
            TryDelete(work);
        }
    }

    public async Task<byte[]> DecodeAsync(byte[] archive, string? password, CancellationToken ct = default)
    {
        var work = NewWorkDir();
        try
        {
            var input = Path.Combine(work, "in.7z");
            var outDir = Path.Combine(work, "out");
            await File.WriteAllBytesAsync(input, archive, ct);

            var args = new List<string> { "x", "-y", "-bso0", "-bsp0" };
            if (!string.IsNullOrEmpty(password))
                args.Add("-p" + password);
            args.Add("-o" + outDir);
            args.Add(input);

            await RunAsync(args, ct);
            return await File.ReadAllBytesAsync(Path.Combine(outDir, EntryName), ct);
        }
        finally
        {
            TryDelete(work);
        }
    }

    private string NewWorkDir()
    {
        var dir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private Task RunAsync(IReadOnlyList<string> args, CancellationToken ct) =>
        SevenZipCli.RunAsync(_exe, args, ct);

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
