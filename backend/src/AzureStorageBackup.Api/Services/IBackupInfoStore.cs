using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 信息记录文件与第二级索引在 container 中的读写（M4 设计 §8）。
/// 组合 JSON 序列化 + 7z 编解码 + Azure blob；写入用「临时 blob→校验→切换正式名」保证原子性。
/// password 非空 ⟺ 加密（用 .enc 文件名）。
/// </summary>
public interface IBackupInfoStore
{
    /// <summary>读取信息记录文件；不存在返回 null。优先非加密（PRD 1.6）。</summary>
    Task<BackupInfoFile?> ReadInfoAsync(Account account, string container, string? password, CancellationToken ct = default);

    /// <summary>读取信息记录文件 + 其云端 ETag（用于本地权威缓存的同步/冲突检测）；不存在返回 null。</summary>
    Task<(BackupInfoFile Info, string ETag)?> ReadInfoWithETagAsync(Account account, string container, string? password, CancellationToken ct = default);

    /// <summary>原子写入信息记录文件（覆盖）。tier 为空则用默认（Hot）。</summary>
    Task WriteInfoAsync(Account account, string container, BackupInfoFile info, string? password, AccessTier? tier = null, CancellationToken ct = default);

    /// <summary>
    /// 带 ETag 乐观并发的原子写入，返回新 ETag。ifMatch 非空 → <c>If-Match</c>（外部改动则抛 RequestFailedException 412/409）；
    /// 为空 → 无条件覆盖（等同 <see cref="WriteInfoAsync"/>）。用于本地权威信息文件的提交（§3.3）。
    /// </summary>
    Task<string> WriteInfoConditionalAsync(Account account, string container, BackupInfoFile info, string? password, AccessTier? tier, string? ifMatch, CancellationToken ct = default);

    /// <summary>读取指定 blob 名的第二级索引。</summary>
    Task<VersionIndex> ReadIndexAsync(Account account, string container, string indexBlob, string? password, CancellationToken ct = default);

    /// <summary>写入某版本的第二级索引，返回其 blob 名（记入信息文件 versions[].indexBlob）。tier 为空则用默认。</summary>
    Task<string> WriteIndexAsync(Account account, string container, int version, VersionIndex index, string? password, AccessTier? tier = null, CancellationToken ct = default);
}
