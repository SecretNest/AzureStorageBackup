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
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // 取消 WaitForExitAsync 只是「不再等」——7z 不会因此停下，Process.Dispose 也不杀它。
            // 用户在界面上按了 Stop，压缩进程却还在 NAS 上啃着几十 GB、继续往临时目录写分卷：
            // CPU/IO 一直被占，而调用方紧接着要删的正是那个目录，会撞上一个仍在被写的文件。
            // 所以取消时显式把整棵进程树杀掉——7z 会 fork 出子进程，只杀父进程留得下孤儿。
            KillTree(proc);
            // 不带 ct 地等它真收尾：带上就又被立刻取消，清理还是会撞上活着的进程。
            await proc.WaitForExitAsync(CancellationToken.None);
            await ObserveAsync(stdoutTask, stderrTask);
            throw;
        }
        var stderr = await stderrTask;
        var stdout = await stdoutTask;

        // 7z 退出码：0=OK，1=警告，>=2=错误（含密码错误）。
        if (proc.ExitCode >= 2)
            throw new InvalidOperationException($"7-Zip failed (exit {proc.ExitCode}): {stderr.Trim()}");

        return new SevenZipRun(proc.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// 运行 7z，把 stdin/stdout **原样**交给调用方处理，不缓冲成字符串。
    /// <para>
    /// <see cref="RunAsync"/> 用 <c>ReadToEndAsync</c> 收 stdout，对 `x -so` 是灾难：那条流上跑的
    /// 是成员内容本身，一个几十 GB 的单文件 blob 会被整个读进内存——比先落盘再读还糟。
    /// 反方向的 `a -si` 同理：源文件的字节要一路喂进 stdin，中间不能有一个"先攒起来"的环节。
    /// </para>
    /// <para>
    /// 三条管道都必须有人管。stdout 由调用方读，读完（或提前不读了）剩下的由这里排空；
    /// 排空动作和写 stdin 并发进行——真让 7z 在写 stdout 上把管道塞满而我们正卡在写 stdin，
    /// 两边就一起死等。stderr 照旧整读（消息量小，不读同样会把管道写满）。
    /// </para>
    /// </summary>
    public static async Task<SevenZipRun> RunStreamingAsync(
        string exe, IReadOnlyList<string> args, CancellationToken ct,
        Func<Stream, CancellationToken, Task>? writeStdin = null,
        Func<Stream, CancellationToken, Task>? readStdout = null,
        string? workingDirectory = null)
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

        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        var stdoutTask = Task.Run(async () =>
        {
            if (readStdout is not null)
                await readStdout(proc.StandardOutput.BaseStream, ct);
            await proc.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct);
        }, ct);

        try
        {
            // 不喂 stdin 就立刻给 EOF：与 RunAsync 同理，缺密码时让它失败而非挂起等输入。
            if (writeStdin is not null)
                await writeStdin(proc.StandardInput.BaseStream, ct);
            proc.StandardInput.Close();   // 关闭＝告诉 7z"内容到此为止"，必须在写完之后
            await stdoutTask;
            await proc.WaitForExitAsync(ct);
        }
        catch
        {
            // 取消、读写失败、调用方自己抛——结局都一样：这个 7z 进程还在啃磁盘，而调用方
            // 紧接着要删的正是它在写/读的临时目录。整棵树杀掉再让异常继续往上走（同 RunAsync）。
            // 尤其是喂 stdin 时读源文件失败：这里绝不能吞，否则一次半截的压缩会被当成成功。
            KillTree(proc);
            await proc.WaitForExitAsync(CancellationToken.None);
            try { proc.StandardInput.Close(); } catch { /* 已随进程一起没了 */ }
            await ObserveAsync(stdoutTask, stderrTask);
            throw;
        }

        var stderr = await stderrTask;
        if (proc.ExitCode >= 2)
            throw new InvalidOperationException($"7-Zip failed (exit {proc.ExitCode}): {stderr.Trim()}");

        // stdout 已经交给调用方，这里没有可回填的内容。
        return new SevenZipRun(proc.ExitCode, "", stderr);
    }

    /// <summary>杀掉进程及其子进程。已经退出（竞态）或杀不动都不算错——取消本身已经在往外抛了，
    /// 不能让收尾动作再盖一个不相干的异常上去。</summary>
    private static void KillTree(Process proc)
    {
        try
        {
            proc.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { } // 在 Kill 之前它自己退出了
        catch (System.ComponentModel.Win32Exception) { } // 系统拒绝（权限/已回收）：只能作罢
    }

    /// <summary>观察掉两个读取任务的结局。进程被杀后它们要么随 ct 取消，要么因管道关闭而报错；
    /// 不 await 就成了未观察的任务异常。这里只吞这两种预期结局，别的照抛。</summary>
    private static async Task ObserveAsync(params Task[] tasks)
    {
        foreach (var t in tasks)
        {
            try
            {
                await t;
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    /// <summary>列出归档内的条目名（`l -slt`），分隔符归一化为 '/'。
    /// 加密备份用 -mhe=on（头也加密），不给密码连条目名都列不出来，所以密码必须一并传入。
    /// 分卷归档传首卷（.001），7z 自行找齐后续卷。</summary>
    public static async Task<HashSet<string>> ListEntriesAsync(
        string exe, string firstVolumePath, string? password, CancellationToken ct)
        => [.. (await ListEntryDetailsAsync(exe, firstVolumePath, password, ct)).Select(e => e.Name)];

    /// <summary>
    /// 列出归档成员，**保持归档内顺序**并带上尺寸与目录标记。
    /// 顺序是承重的：`x -so` 不带成员名时，各成员的内容正是按这个顺序首尾相接输出的，
    /// 按尺寸切段才能还原出每个成员。注意这个顺序未必等于当初压缩时给出的参数顺序。
    /// </summary>
    public static async Task<IReadOnlyList<ArchiveEntry>> ListEntryDetailsAsync(
        string exe, string firstVolumePath, string? password, CancellationToken ct)
    {
        var args = new List<string> { "l", "-slt", "-y" };
        if (!string.IsNullOrEmpty(password))
            args.Add("-p" + password);
        args.Add(Path.GetFullPath(firstVolumePath));

        var run = await RunAsync(exe, args, ct);
        return ParseEntryDetails(run.StdOut);
    }

    /// <summary>解析 `l -slt` 的输出。归档**自身**的信息块也有一行 "Path = &lt;归档文件名&gt;"，
    /// 成员块则统一排在第一道 "----------" 分隔线之后——所以必须先跳到那道线，
    /// 否则归档文件名会被当成一个成员混进来。每遇到一行 "Path = " 就开一个新成员块。</summary>
    private static IReadOnlyList<ArchiveEntry> ParseEntryDetails(string listing)
    {
        const string pathPrefix = "Path = ";
        const string sizePrefix = "Size = ";
        const string attrPrefix = "Attributes = ";
        const string folderPrefix = "Folder = ";

        var entries = new List<ArchiveEntry>();
        var inEntries = false;
        string? name = null;
        long size = 0;
        var isDir = false;

        void FlushPending()
        {
            if (name is not null)
                entries.Add(new ArchiveEntry(name, size, isDir));
            name = null;
            size = 0;
            isDir = false;
        }

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
            {
                FlushPending();
                name = NormalizeEntryName(line[pathPrefix.Length..]);
            }
            else if (line.StartsWith(sizePrefix, StringComparison.Ordinal))
            {
                _ = long.TryParse(line[sizePrefix.Length..].Trim(), out size);
            }
            else if (line.StartsWith(attrPrefix, StringComparison.Ordinal))
            {
                // 目录的属性串以 'D' 打头（Unix: "D drwxr-xr-x"，Windows: "D...."）。
                var attr = line[attrPrefix.Length..].Trim();
                isDir |= attr.StartsWith('D');
            }
            else if (line.StartsWith(folderPrefix, StringComparison.Ordinal))
            {
                isDir |= line[folderPrefix.Length..].Trim() == "+"; // 部分 7z 版本用这个字段
            }
        }
        FlushPending();
        return entries;
    }

    /// <summary>Windows 上 7z 用 '\' 列出条目名，而调用方给的条目名用 '/'。</summary>
    public static string NormalizeEntryName(string entry) => entry.Trim().Replace('\\', '/');
}

/// <summary>一次 7z 调用的结果。</summary>
internal sealed record SevenZipRun(int ExitCode, string StdOut, string StdErr);
