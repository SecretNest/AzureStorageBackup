namespace AzureStorageBackup.Api.Models;

/// <summary>
/// 密钥环健康哨兵（单行）。存已知常量明文的密文，**不经任何 ValueConverter**，
/// 由 KeyringProbe 显式 Protect/Unprotect（设计 §3.2）。
/// </summary>
public class KeyringCanary
{
    public int Id { get; set; }
    public string Ciphertext { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
