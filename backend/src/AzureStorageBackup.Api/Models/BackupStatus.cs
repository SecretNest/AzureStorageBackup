namespace AzureStorageBackup.Api.Models;

/// <summary>备份配置的持久状态（决策 2）。瞬时态（备份中/还原中/检查中/修复中…）由 runner 派生，不落库。</summary>
public enum BackupStatus
{
    Normal = 0,
    Error = 1,
}
