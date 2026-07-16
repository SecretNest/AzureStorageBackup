using System.Diagnostics;

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
    public static string? TryResolveExecutable()
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var candidate in new[] { "7zz", "7z", "7za" })
        {
            foreach (var dir in dirs)
            {
                var full = Path.Combine(dir, candidate);
                if (File.Exists(full))
                    return full;
            }
        }
        return null;
    }

    public async Task<byte[]> EncodeAsync(byte[] content, string? password, CancellationToken ct = default)
    {
        var work = NewWorkDir();
        try
        {
            var input = Path.Combine(work, EntryName);
            var archive = Path.Combine(work, "out.7z");
            await File.WriteAllBytesAsync(input, content, ct);

            var args = new List<string> { "a", "-t7z", "-y", "-bso0", "-bsp0" };
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

    private async Task RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{_exe}'.");

        // 关闭 stdin：若归档加密而未提供密码，7z 会等待输入 —— 给它 EOF 使其失败而非挂起。
        proc.StandardInput.Close();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stderr = await stderrTask;
        await stdoutTask;

        // 7z 退出码：0=OK，1=警告，>=2=错误（含密码错误）。
        if (proc.ExitCode >= 2)
            throw new InvalidOperationException(
                $"7-Zip failed (exit {proc.ExitCode}): {stderr.Trim()}");
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
