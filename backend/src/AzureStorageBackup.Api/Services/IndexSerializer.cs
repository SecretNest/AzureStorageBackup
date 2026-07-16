using System.Text.Json;
using System.Text.Json.Serialization;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 信息记录文件与第二级索引的 JSON 序列化（M4 设计 §13.4：JSON，可读）。
/// 压缩/加密（7z）由独立的编解码层负责，不在此。
/// </summary>
public static class IndexSerializer
{
    /// <summary>当前支持的 schema 版本。</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static byte[] SerializeInfoFile(BackupInfoFile info) =>
        JsonSerializer.SerializeToUtf8Bytes(info, Options);

    public static BackupInfoFile DeserializeInfoFile(byte[] bytes)
    {
        var info = JsonSerializer.Deserialize<BackupInfoFile>(bytes, Options)
            ?? throw new InvalidDataException("Info file JSON deserialized to null.");

        if (info.SchemaVersion > CurrentSchemaVersion)
            throw new NotSupportedException(
                $"Info file schemaVersion {info.SchemaVersion} is newer than supported {CurrentSchemaVersion}.");

        return info;
    }

    public static byte[] SerializeIndex(VersionIndex index) =>
        JsonSerializer.SerializeToUtf8Bytes(index, Options);

    public static VersionIndex DeserializeIndex(byte[] bytes) =>
        JsonSerializer.Deserialize<VersionIndex>(bytes, Options)
            ?? throw new InvalidDataException("Version index JSON deserialized to null.");
}
