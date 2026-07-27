using System.IO.Hashing;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 文件两级哈希。用 XxHash128（非加密、极快、16 字节）——比 SHA-256 更快更短以减小索引体积；
/// 128 位对个人备份规模的内容寻址去重碰撞概率可忽略（远优于 CRC）。
/// headHash 覆盖文件头部一小段用于 diff 快速预筛；fullHash 覆盖整文件，兼作去重键。
/// </summary>
public interface IFileHasher
{
    Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default);

    /// <summary>文件末段（最多 tailBytes 字节）的 hash。与 headHash 一起作为去重碰撞检测的强化元数据：
    /// 内容不同却 fullHash+长度+头 全等的残余碰撞，若差异不在文件中段即可被尾部 hash 识破。</summary>
    Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default);

    Task<string> FullHashAsync(string path, CancellationToken ct = default);
}

public sealed class FileHasher : IFileHasher
{
    public async Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default)
    {
        await using var stream = Open(path);
        var buffer = new byte[headBytes];
        var total = 0;
        while (total < headBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, headBytes - total), ct);
            if (read == 0)
                break;
            total += read;
        }
        return Format(XxHash128.Hash(buffer.AsSpan(0, total)));
    }

    public async Task<string> TailHashAsync(string path, int tailBytes, CancellationToken ct = default)
    {
        await using var stream = Open(path);
        var len = stream.Length;
        var take = (int)Math.Min(tailBytes, len);
        if (take > 0)
            stream.Seek(-take, SeekOrigin.End);
        var buffer = new byte[take];
        var total = 0;
        while (total < take)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, take - total), ct);
            if (read == 0)
                break;
            total += read;
        }
        return Format(XxHash128.Hash(buffer.AsSpan(0, total)));
    }

    public async Task<string> FullHashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = Open(path);
        var hash = new XxHash128();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            hash.Append(buffer.AsSpan(0, read));
        return Format(hash.GetCurrentHash());
    }

    /// <summary>
    /// 打开待哈希的文件。Unix 上**必须**用 O_NONBLOCK。
    /// <para>
    /// 一个 FIFO（命名管道）会让常规的 <c>File.OpenRead</c> **永久阻塞**在 open() 里等待写入端。
    /// 那是阻塞在系统调用中，<c>CancellationToken</c> 够不着：整轮备份就此挂死、无法取消，
    /// 忙碌锁被永久占用，该配置此后拒绝一切操作、定时任务全部跳过，界面上只剩一个不动的百分比。
    /// 而 .NET 在 Unix 上**无法**把它认出来——实测 FIFO 的 <c>FileAttributes</c> 同样是 Normal、
    /// <c>Length</c> 同样是 0，与普通空文件毫无差别。
    /// </para>
    /// <para>
    /// 所以：非阻塞打开使 FIFO 立即返回而不是挂住，再用 <c>CanSeek</c> 把它与普通文件区分开
    /// （普通文件——含空文件——恒为 true，FIFO/管道为 false；socket 更早一步，open 直接以 ENXIO 失败）。
    /// 判为非普通文件就抛 IOException，落进既有的「读不开」处理：不进上传计划，
    /// 因此 7z 也永远不会去打开它——7z 是独立进程、不带这个标志，一旦碰上会同样挂住。
    /// O_NONBLOCK 对普通文件的读取语义没有影响（POSIX 保证），所以正常路径分毫不变。
    /// </para>
    /// <para>
    /// 公开出去（<see cref="OpenRead"/>）是因为流式备份也要读源文件：一条 FIFO 对它同样是死局，
    /// 而"读源文件"这件事在项目里必须只有一种打开方式，否则这道保护迟早会被绕过去一次。
    /// </para>
    /// </summary>
    public static FileStream OpenRead(string path) => Open(path);

    private static FileStream Open(string path)
    {
        if (OperatingSystem.IsWindows())
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

        var fd = open(path, O_RDONLY | (OperatingSystem.IsMacOS() ? O_NONBLOCK_BSD : O_NONBLOCK_LINUX));
        if (fd < 0)
            throw new IOException(
                $"Cannot open '{path}' for reading (errno {Marshal.GetLastPInvokeError()}).");

        var stream = new FileStream(new SafeFileHandle(fd, ownsHandle: true), FileAccess.Read, 81920);
        if (stream.CanSeek)
            return stream;

        // 管道/FIFO 这类不可定位的东西：它没有「文件内容」可言，读它只会等一个可能永不到来的写入端。
        stream.Dispose();
        throw new IOException($"'{path}' is not a regular file (named pipe, device or similar).");
    }

    private const int O_RDONLY = 0;
    private const int O_NONBLOCK_LINUX = 0x800;  // Linux: 0o4000
    private const int O_NONBLOCK_BSD = 0x4;      // macOS/BSD

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int open(string pathname, int flags);

    private static string Format(byte[] hash) => "xxh128:" + Convert.ToHexString(hash).ToLowerInvariant();
}
