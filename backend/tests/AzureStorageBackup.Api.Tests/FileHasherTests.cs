using System.IO.Hashing;
using System.Text;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class FileHasherTests : IDisposable
{
    private readonly string _dir;

    public FileHasherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "asb-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string name, byte[] content)
    {
        var full = Path.Combine(_dir, name);
        File.WriteAllBytes(full, content);
        return full;
    }

    private static string Xxh128Hex(byte[] data) =>
        "xxh128:" + Convert.ToHexString(XxHash128.Hash(data)).ToLowerInvariant();

    [Fact]
    public async Task FullHash_Is_Xxh128_Of_Whole_File()
    {
        var content = Encoding.UTF8.GetBytes("some content to hash");
        var path = Write("a.bin", content);

        var hash = await new FileHasher().FullHashAsync(path);

        Assert.Equal(Xxh128Hex(content), hash);
    }

    [Fact]
    public async Task HeadHash_Covers_Only_First_N_Bytes()
    {
        var head = new byte[8];
        for (var i = 0; i < head.Length; i++) head[i] = (byte)i;
        var a = Write("a.bin", head.Concat(new byte[] { 1, 1, 1 }).ToArray());
        var b = Write("b.bin", head.Concat(new byte[] { 2, 2, 2 }).ToArray());

        var hasher = new FileHasher();
        var headA = await hasher.HeadHashAsync(a, 8);
        var headB = await hasher.HeadHashAsync(b, 8);

        Assert.Equal(headA, headB);
        Assert.Equal(Xxh128Hex(head), headA);
        Assert.NotEqual(await hasher.FullHashAsync(a), await hasher.FullHashAsync(b));
    }

    [Fact]
    public async Task HeadHash_Equals_FullHash_When_File_Smaller_Than_Window()
    {
        var path = Write("tiny.bin", Encoding.UTF8.GetBytes("tiny"));
        var hasher = new FileHasher();

        Assert.Equal(await hasher.FullHashAsync(path), await hasher.HeadHashAsync(path, 4096));
    }

    /// <summary>
    /// A FIFO makes an ordinary File.OpenRead **block forever** inside open() waiting for a writer, and that block sits inside a
    /// syscall out of CancellationToken's reach — the whole backup run wedges and cannot be canceled, the busy lock is held forever, and the UI is left with a percentage that never moves.
    /// .NET cannot recognize one: FileAttributes is likewise Normal and Length is likewise 0, indistinguishable from an ordinary empty file.
    /// <para>It has to fail as "can't be opened" rather than hang, and must not be treated as an empty file either — treated as an
    /// empty file it would enter the upload plan, and 7z would hang just the same when it went to open it (a separate process, without O_NONBLOCK).</para>
    /// <para>The test carries its own timeout: before the fix this hangs forever, and without a timeout it would drag the entire test suite down with it.</para>
    /// </summary>
    [SkippableFact]
    public async Task A_Named_Pipe_Fails_To_Read_Instead_Of_Hanging_Forever()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX FIFO semantics.");

        var fifo = Path.Combine(_dir, "some.pipe");
        using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", fifo))
        {
            await mkfifo.WaitForExitAsync();
            Skip.If(mkfifo.ExitCode != 0, "mkfifo unavailable.");
        }
        Assert.True(File.Exists(fifo)); // As far as .NET is concerned it is just a "file" — which is precisely the problem

        var hasher = new FileHasher();
        var attempt = Task.Run(() => hasher.FullHashAsync(fifo));

        Assert.True(await Task.WhenAny(attempt, Task.Delay(TimeSpan.FromSeconds(10))) == attempt,
            "FullHashAsync hung on a FIFO — this is the bug that wedges an entire backup.");
        await Assert.ThrowsAnyAsync<IOException>(() => attempt);
    }

    /// <summary>The control for the case above: an empty **regular** file and a FIFO look identical to .NET (Normal, Length 0),
    /// so the fix has to rest on CanSeek rather than on length — otherwise ordinary empty files get judged unreadable along with it.</summary>
    [Fact]
    public async Task An_Empty_Regular_File_Still_Hashes_Normally()
    {
        var empty = Write("empty.bin", []);
        Assert.Equal(Xxh128Hex([]), await new FileHasher().FullHashAsync(empty));
    }
}
