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
    /// 一个 FIFO 会让常规的 File.OpenRead **永久阻塞**在 open() 里等待写入端，而那是阻塞在系统调用中、
    /// CancellationToken 够不着——整轮备份挂死且无法取消，忙碌锁被永久占用，界面只剩一个不动的百分比。
    /// .NET 认不出它：FileAttributes 同样是 Normal、Length 同样是 0，与普通空文件毫无差别。
    /// <para>必须以「读不开」失败，而不是挂住，也不能被当成空文件——当成空文件的话它会进上传计划，
    /// 7z 再去打开它时同样会挂（那是独立进程，不带 O_NONBLOCK）。</para>
    /// <para>测试自带超时：修复前这里会永久挂起，没有超时就会把整个测试套件一起拖死。</para>
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
        Assert.True(File.Exists(fifo)); // 对 .NET 来说它就是个「文件」——这正是问题所在

        var hasher = new FileHasher();
        var attempt = Task.Run(() => hasher.FullHashAsync(fifo));

        Assert.True(await Task.WhenAny(attempt, Task.Delay(TimeSpan.FromSeconds(10))) == attempt,
            "FullHashAsync hung on a FIFO — this is the bug that wedges an entire backup.");
        await Assert.ThrowsAnyAsync<IOException>(() => attempt);
    }

    /// <summary>上一条的对照：空的**普通**文件与 FIFO 在 .NET 眼里长得一样（Normal、Length 0），
    /// 所以修复必须靠 CanSeek 而不是靠长度——否则会把普通空文件一并判成读不开。</summary>
    [Fact]
    public async Task An_Empty_Regular_File_Still_Hashes_Normally()
    {
        var empty = Write("empty.bin", []);
        Assert.Equal(Xxh128Hex([]), await new FileHasher().FullHashAsync(empty));
    }
}
