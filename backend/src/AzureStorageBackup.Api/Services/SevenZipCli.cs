using System.Diagnostics;

namespace AzureStorageBackup.Api.Services;

/// <summary>官方 7-Zip 二进制的共享调用层：PATH 探测 + 进程运行（不经 shell）。</summary>
internal static class SevenZipCli
{
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

    /// <summary>运行 7z，参数经 ArgumentList 传递（密码含特殊字符也安全）。退出码 >=2 视为失败抛异常。</summary>
    public static async Task RunAsync(
        string exe, IReadOnlyList<string> args, CancellationToken ct, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{exe}'.");

        // 关闭 stdin：若归档加密而未提供密码，7z 会等待输入 —— 给它 EOF 使其失败而非挂起。
        proc.StandardInput.Close();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stderr = await stderrTask;
        await stdoutTask;

        // 7z 退出码：0=OK，1=警告，>=2=错误（含密码错误）。
        if (proc.ExitCode >= 2)
            throw new InvalidOperationException($"7-Zip failed (exit {proc.ExitCode}): {stderr.Trim()}");
    }
}
