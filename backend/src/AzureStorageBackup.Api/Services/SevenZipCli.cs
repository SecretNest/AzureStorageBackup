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

    /// <summary>运行 7z，参数经 ArgumentList 传递（密码含特殊字符也安全）。退出码 >=2 视为失败抛异常。
    /// 返回退出码供调用方自行验收：**1 不是"没事"**——7z 用它表示警告，而"读不了的成员被静默丢掉、
    /// 归档照样有效产出"正是这个退出码，只有比对归档实际内容才能发现。</summary>
    public static async Task<SevenZipRun> RunAsync(
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
        var stdout = await stdoutTask;

        // 7z 退出码：0=OK，1=警告，>=2=错误（含密码错误）。
        if (proc.ExitCode >= 2)
            throw new InvalidOperationException($"7-Zip failed (exit {proc.ExitCode}): {stderr.Trim()}");

        return new SevenZipRun(proc.ExitCode, stdout, stderr);
    }

    /// <summary>列出归档内的条目名（`l -slt`），分隔符归一化为 '/'。
    /// 加密备份用 -mhe=on（头也加密），不给密码连条目名都列不出来，所以密码必须一并传入。
    /// 分卷归档传首卷（.001），7z 自行找齐后续卷。</summary>
    public static async Task<HashSet<string>> ListEntriesAsync(
        string exe, string firstVolumePath, string? password, CancellationToken ct)
    {
        var args = new List<string> { "l", "-slt", "-y" };
        if (!string.IsNullOrEmpty(password))
            args.Add("-p" + password);
        args.Add(Path.GetFullPath(firstVolumePath));

        var run = await RunAsync(exe, args, ct);
        return ParseEntryPaths(run.StdOut);
    }

    /// <summary>解析 `l -slt` 的输出。归档**自身**的信息块也有一行 "Path = <归档文件名>"，
    /// 成员块则统一排在第一道 "----------" 分隔线之后——所以必须先跳到那道线，
    /// 否则归档文件名会被当成一个成员混进来。</summary>
    private static HashSet<string> ParseEntryPaths(string listing)
    {
        const string pathPrefix = "Path = ";
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var inEntries = false;

        foreach (var raw in listing.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!inEntries)
            {
                if (line.StartsWith("----------", StringComparison.Ordinal))
                    inEntries = true;
                continue;
            }
            if (line.StartsWith(pathPrefix, StringComparison.Ordinal))
                paths.Add(NormalizeEntryName(line[pathPrefix.Length..]));
        }
        return paths;
    }

    /// <summary>Windows 上 7z 用 '\' 列出条目名，而调用方给的条目名用 '/'。</summary>
    public static string NormalizeEntryName(string entry) => entry.Trim().Replace('\\', '/');
}

/// <summary>一次 7z 调用的结果。</summary>
internal sealed record SevenZipRun(int ExitCode, string StdOut, string StdErr);
