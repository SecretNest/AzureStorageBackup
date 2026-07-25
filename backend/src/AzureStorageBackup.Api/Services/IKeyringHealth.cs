namespace AzureStorageBackup.Api.Services;

public enum KeyringStatus
{
    Healthy = 0,
    Lost = 1,
}

/// <summary>进程级密钥环状态。启动时判定一次并缓存；重设流程完成时显式翻转（设计 §3.2）。</summary>
public interface IKeyringHealth
{
    KeyringStatus Status { get; }
    void Set(KeyringStatus status);
}
