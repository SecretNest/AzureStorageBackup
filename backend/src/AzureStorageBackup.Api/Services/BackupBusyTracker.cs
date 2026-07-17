namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 跟踪各备份（按 账户/container 标识）是否正在执行操作（备份/还原/检查/清理）。单例、线程安全。
/// 计划任务触发时若目标忙碌，则记录报警并跳过，不打断正在执行的任务。
/// </summary>
public sealed class BackupBusyTracker
{
    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    private static string Key(int accountId, string container) => $"{accountId}/{container}";

    /// <summary>尝试标记忙碌；已忙碌返回 false（不获取）。</summary>
    public bool TryAcquire(int accountId, string container)
    {
        lock (_lock)
            return _busy.Add(Key(accountId, container));
    }

    /// <summary>释放忙碌标记。</summary>
    public void Release(int accountId, string container)
    {
        lock (_lock)
            _busy.Remove(Key(accountId, container));
    }

    /// <summary>是否忙碌（仅查询）。</summary>
    public bool IsBusy(int accountId, string container)
    {
        lock (_lock)
            return _busy.Contains(Key(accountId, container));
    }
}
