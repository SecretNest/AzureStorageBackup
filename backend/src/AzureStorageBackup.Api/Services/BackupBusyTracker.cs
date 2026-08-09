namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Tracks whether each backup (identified by account/container) is currently running an operation (backup/restore/check/cleanup). Singleton, thread-safe.
/// When a scheduled task fires on a busy target it records a warning and skips, rather than interrupting the running task.
/// </summary>
public sealed class BackupBusyTracker
{
    // key → the transient-state label of the current operation (BackingUp/Checking/CleaningUp/Repairing…), so the derived activity displays accurately.
    private readonly Dictionary<string, string> _busy = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    private static string Key(int accountId, string container) => $"{accountId}/{container}";

    /// <summary>Tries to mark it busy and record the operation label; returns false if it is already busy (nothing is acquired).</summary>
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

    /// <summary>Releases the busy mark.</summary>
    public void Release(int accountId, string container)
    {
        lock (_lock)
            _busy.Remove(Key(accountId, container));
    }

    /// <summary>Whether it is busy (query only).</summary>
    public bool IsBusy(int accountId, string container)
    {
        lock (_lock)
            return _busy.ContainsKey(Key(accountId, container));
    }

    /// <summary>Label of the operation currently holding this target; null when not busy. Used to derive the transient state (so a scheduled backup/cleanup is not mislabeled as Checking).</summary>
    public string? CurrentActivity(int accountId, string container)
    {
        lock (_lock)
            return _busy.GetValueOrDefault(Key(accountId, container));
    }
}
