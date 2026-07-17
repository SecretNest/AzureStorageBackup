using System.IO.Hashing;

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

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

    private static string Format(byte[] hash) => "xxh128:" + Convert.ToHexString(hash).ToLowerInvariant();
}
