using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>Cancellation semantics of the 7z invocation layer. The process under test is /bin/sh rather than a real 7z: what has to be asserted is "is the child process
/// still alive after the cancellation", and that needs a process which runs long enough and leaves an observable trace — what 7z would have to compress to be slow enough is not controllable.</summary>
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

    /// <summary>After the user presses Stop, the compression process must really stop. Cancelling WaitForExitAsync alone is not enough:
    /// that only means "stop waiting", and 7z keeps saturating the CPU and keeps writing volumes into a temp directory that is about to be cleaned up.
    /// 7z also forks children, so what must be killed is the whole tree — the marker file here is written by a grandchild process,
    /// and killing only the parent lets it show up anyway.</summary>
    [SkippableFact]
    public async Task Canceling_A_Run_Kills_The_Whole_Process_Tree()
    {
        Skip.IfNot(File.Exists("/bin/sh"), "POSIX shell not available.");
        var marker = Path.Combine(_dir, "still-running.txt");

        using var cts = new CancellationTokenSource();
        var run = SevenZipCli.RunAsync(
            "/bin/sh", ["-c", $"( sleep 2; echo alive > '{marker}' ) & wait"], cts.Token);

        await Task.Delay(300); // let the process tree really come up, otherwise we kill a shell that has not forked yet
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await Task.Delay(2500); // past the moment it would have written the marker
        Assert.False(File.Exists(marker), "the child process outlived the cancellation");
    }

    /// <summary>Streaming compression feeds the source file's bytes straight into 7z's stdin, so the cancellation happens **mid-write**.
    /// This path likewise has to kill the whole process tree: let go the moment the writing side is interrupted and the surviving process keeps writing volumes
    /// into a temp directory that is about to be cleaned up.</summary>
    [SkippableFact]
    public async Task Canceling_While_Feeding_Stdin_Kills_The_Whole_Process_Tree()
    {
        Skip.IfNot(File.Exists("/bin/sh"), "POSIX shell not available.");
        var marker = Path.Combine(_dir, "stdin-still-running.txt");

        using var cts = new CancellationTokenSource();
        var run = SevenZipCli.RunStreamingAsync(
            "/bin/sh", ["-c", $"( sleep 2; echo alive > '{marker}' ) & cat > /dev/null; wait"], cts.Token,
            writeStdin: async (stdin, token) =>
            {
                var chunk = new byte[64 * 1024];
                while (true) // keep feeding until cancellation interrupts it
                {
                    await stdin.WriteAsync(chunk, token);
                    await Task.Delay(10, token);
                }
            });

        await Task.Delay(300); // let the process tree really come up
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await Task.Delay(2500); // past the moment it would have written the marker
        Assert.False(File.Exists(marker), "the child process outlived the cancellation");
    }

    /// <summary>A source read failure while feeding stdin must propagate unchanged. Swallow it and a compression that only got half the file written
    /// counts as a success — the output is a perfectly legal 7z archive, indistinguishable by exit code alone.</summary>
    [SkippableFact]
    public async Task A_Failure_While_Feeding_Stdin_Is_Not_Swallowed()
    {
        Skip.IfNot(File.Exists("/bin/sh"), "POSIX shell not available.");

        await Assert.ThrowsAsync<IOException>(() => SevenZipCli.RunStreamingAsync(
            "/bin/sh", ["-c", "cat > /dev/null"], CancellationToken.None,
            writeStdin: async (stdin, token) =>
            {
                await stdin.WriteAsync(new byte[1024], token);
                throw new IOException("the source went away mid-read");
            }));
    }

    /// <summary>Cancellation must only return once the process has really finished: the caller deletes the work directory right afterwards,
    /// and if the process is still alive the deletion runs into a file that is being written (or the file comes back after the delete).</summary>
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

        // Cancellation has returned → the work directory must be deletable immediately, with no waiting or retrying.
        Directory.Delete(work, recursive: true);
        Assert.False(Directory.Exists(work));
    }
}
