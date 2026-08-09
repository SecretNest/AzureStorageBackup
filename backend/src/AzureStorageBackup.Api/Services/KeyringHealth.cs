namespace AzureStorageBackup.Api.Services;

/// <summary>A singleton. Written very rarely (once at startup, once when recovery completes) and read often, so a volatile field suffices.</summary>
public sealed class KeyringHealth : IKeyringHealth
{
    private volatile int _status = (int)KeyringStatus.Healthy;

    public KeyringStatus Status => (KeyringStatus)_status;

    public void Set(KeyringStatus status) => _status = (int)status;
}
