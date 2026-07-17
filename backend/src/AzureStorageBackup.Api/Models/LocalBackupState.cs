namespace AzureStorageBackup.Api.Models;

/// <summary>
/// 本地权威的信息文件缓存（设计 §3.3）。信息文件平时不从云端读——它可能落在 Cold tier，读取内容有取回费。
/// 本地保存序列化的信息文件 + 其云端 ETag；备份写入用 <c>If-Match</c> 乐观并发检测外部改动（多机/重建），冲突则清本地重同步。
/// </summary>
public class LocalBackupState
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string Container { get; set; } = string.Empty;

    /// <summary>序列化后的信息文件字节（IndexSerializer 输出，未压缩）。</summary>
    public byte[] InfoBytes { get; set; } = [];

    /// <summary>云端信息文件 blob 的 ETag，用于下次写入的 If-Match。</summary>
    public string ETag { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}
