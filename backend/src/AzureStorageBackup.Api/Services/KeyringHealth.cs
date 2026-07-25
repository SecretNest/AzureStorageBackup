namespace AzureStorageBackup.Api.Services;

/// <summary>单例。写入极少（启动一次 + 恢复完成一次），读取频繁，用 volatile 字段即可。</summary>
public sealed class KeyringHealth : IKeyringHealth
{
    private volatile int _status = (int)KeyringStatus.Healthy;

    public KeyringStatus Status => (KeyringStatus)_status;

    public void Set(KeyringStatus status) => _status = (int)status;
}
