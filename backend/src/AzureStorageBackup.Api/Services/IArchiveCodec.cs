namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 单内容归档编解码：把一段字节压缩（可选加密）成一个归档 blob，及其逆过程。
/// 用于信息记录文件与第二级索引的压缩/加密（M4 设计 §13.4）。
/// password 为空 → 仅压缩；非空 → 7z AES-256 + 头加密（跨设备可用备份密码解开）。
/// </summary>
public interface IArchiveCodec
{
    Task<byte[]> EncodeAsync(byte[] content, string? password, CancellationToken ct = default);
    Task<byte[]> DecodeAsync(byte[] archive, string? password, CancellationToken ct = default);
}
