using System.Security.Cryptography;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 文件两级哈希（M4 决策 §13.2/§13.3，均 SHA-256）。
/// headHash 只覆盖文件头部一小段用于 diff 快速预筛；fullHash 覆盖整文件，兼作去重键。
/// </summary>
public interface IFileHasher
{
    Task<string> HeadHashAsync(string path, int headBytes, CancellationToken ct = default);
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
        return Format(SHA256.HashData(buffer.AsSpan(0, total)));
    }

    public async Task<string> FullHashAsync(string path, CancellationToken ct = default)
    {
        await using var stream = Open(path);
        return Format(await SHA256.HashDataAsync(stream, ct));
    }

    private static FileStream Open(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

    private static string Format(byte[] hash) => "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
}
