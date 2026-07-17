namespace AzureStorageBackup.Api.Models;

/// <summary>
/// 本地缓存的第二级版本索引（设计 §3.3 本地状态缓存）。加速备份对比 / 清理引用扫描——
/// 大的版本索引平时从本地读，避免每次备份都下载解压云端索引。云端信息文件仍为权威。
/// 版本索引一旦写入即不可变，故按 (AccountId, Container, Version) 缓存；
/// <see cref="IdentityTicks"/>=备份创建时间戳，用于识别 container 被删后重建（版本号复用但内容不同）。
/// </summary>
public class CachedVersionIndex
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string Container { get; set; } = string.Empty;
    public int Version { get; set; }

    /// <summary>备份身份（信息文件 Backup.CreatedAt.UtcTicks）；不匹配即视为缓存失效。</summary>
    public long IdentityTicks { get; set; }

    /// <summary>序列化后的版本索引字节（IndexSerializer 输出，未压缩）。</summary>
    public byte[] Bytes { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; }
}
