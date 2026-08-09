namespace AzureStorageBackup.Api.Services;

public enum KeyringStatus
{
    Healthy = 0,
    Lost = 1,
}

/// <summary>Process-wide key ring status. Judged once at startup and cached; flipped explicitly when the reset flow completes (design §3.2).</summary>
public interface IKeyringHealth
{
    KeyringStatus Status { get; }
    void Set(KeyringStatus status);
}
