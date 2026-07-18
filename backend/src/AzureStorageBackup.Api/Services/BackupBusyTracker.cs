namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 跟踪各备份（按 账户/container 标识）是否正在执行操作（备份/还原/检查/清理）。单例、线程安全。
/// 计划任务触发时若目标忙碌，则记录报警并跳过，不打断正在执行的任务。
/// </summary>
public sealed class BackupBusyTracker
{
    // key → 当前操作的瞬时态标签（BackingUp/Checking/CleaningUp/Repairing…），供派生活动准确显示。
    private readonly Dictionary<string, string> _busy = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    private static string Key(int accountId, string container) => $"{accountId}/{container}";

    /// <summary>尝试标记忙碌并记录操作标签；已忙碌返回 false（不获取）。</summary>
    public bool TryAcquire(int accountId, string container, string activity = "Checking")
    {
        lock (_lock)
        {
            var key = Key(accountId, container);
            if (_busy.ContainsKey(key))
                return false;
            _busy[key] = activity;
            return true;
        }
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
            return _busy.ContainsKey(Key(accountId, container));
    }

    /// <summary>当前占用该目标的操作标签；不忙则 null。供瞬时态派生（避免把计划备份/清理误标为 Checking）。</summary>
    public string? CurrentActivity(int accountId, string container)
    {
        lock (_lock)
            return _busy.GetValueOrDefault(Key(accountId, container));
    }
}
