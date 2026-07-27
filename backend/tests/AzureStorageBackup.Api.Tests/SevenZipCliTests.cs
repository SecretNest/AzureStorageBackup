using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>7z 调用层的取消语义。用 /bin/sh 而不是真 7z 当被测进程：要断言的是「取消后子进程
/// 还活着吗」，而这需要一个跑得够久、且会留下可观测痕迹的进程——7z 压什么才够慢是不可控的。</summary>
[Trait("Category", "Integration")]
public sealed class SevenZipCliTests : IDisposable
{
    private readonly string _dir;

    public SevenZipCliTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "asb-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    /// <summary>用户按下 Stop 之后，压缩进程必须真的停下。只取消 WaitForExitAsync 是不够的：
    /// 那只是「不再等」，7z 会继续啃满 CPU、继续往即将被清理的临时目录里写分卷。
    /// 而且 7z 会 fork 子进程，所以杀的必须是整棵树——这里的标记文件由孙子进程写出，
    /// 只杀父进程的话它照样会出现。</summary>
    [SkippableFact]
    public async Task Canceling_A_Run_Kills_The_Whole_Process_Tree()
    {
        Skip.IfNot(File.Exists("/bin/sh"), "POSIX shell not available.");
        var marker = Path.Combine(_dir, "still-running.txt");

        using var cts = new CancellationTokenSource();
        var run = SevenZipCli.RunAsync(
            "/bin/sh", ["-c", $"( sleep 2; echo alive > '{marker}' ) & wait"], cts.Token);

        await Task.Delay(300); // 让进程树真的起来，否则杀的是个还没 fork 的壳
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await Task.Delay(2500); // 越过它本该写出标记的时刻
        Assert.False(File.Exists(marker), "the child process outlived the cancellation");
    }

    /// <summary>取消要在进程真正收尾之后才返回：调用方紧接着就要删掉工作目录，
    /// 若进程还活着，删除会撞上一个正在被写的文件（或删完又被写回来）。</summary>
    [SkippableFact]
    public async Task Cancellation_Returns_Only_After_The_Process_Is_Gone()
    {
        Skip.IfNot(File.Exists("/bin/sh"), "POSIX shell not available.");
        var work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);

        using var cts = new CancellationTokenSource();
        var run = SevenZipCli.RunAsync(
            "/bin/sh", ["-c", "sleep 5"], cts.Token, workingDirectory: work);

        await Task.Delay(300);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // 取消已经返回 → 工作目录必须能立刻删掉，不需要任何等待/重试。
        Directory.Delete(work, recursive: true);
        Assert.False(Directory.Exists(work));
    }
}
