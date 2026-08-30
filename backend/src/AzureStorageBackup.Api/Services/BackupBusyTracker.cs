namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Tracks whether each backup (identified by account/container) is currently running an operation (backup/restore/check/cleanup). Singleton, thread-safe.
/// When a scheduled task fires on a busy target it records a warning and skips, rather than interrupting the running task.
/// </summary>
public sealed class BackupBusyTracker
{
    // key → the transient-state label of the current operation (BackingUp/Checking/CleaningUp/Repairing…), so the derived activity displays accurately.
    private readonly Dictionary<string, string> _busy = new(StringComparer.Ordinal);

    // key → count of active READERS (restores). A restore deliberately does not take the exclusive mark —
    // it must coexist with a backup (user requirement; a backup never rewrites objects an existing version
    // references). But it must NOT coexist with the activities that rewrite or delete referenced objects
    // in place: a repair/compaction overwriting a volume family mid-download hands the restore a mix of
    // old and new volumes, and a retention/config deletion 404s it. Both directions are decided under the
    // one lock below, so there is no window where a reader and a rewriter both slip in.
    private readonly Dictionary<string, int> _readers = new(StringComparer.Ordinal);

    /// <summary>The activities that rewrite or delete objects a live version references — the ones a
    /// reader can never safely overlap. Everything else (BackingUp/Checking/ChangingRoot/Creating) only
    /// adds new objects or touches nothing a restore reads.</summary>
    private static readonly string[] RewritingActivities = ["Repairing", "CleaningUp", "Deleting"];

    private readonly Lock _lock = new();

    private static string Key(int accountId, string container) => $"{accountId}/{container}";

    /// <summary>Tries to mark it busy and record the operation label; returns false if it is already busy (nothing is acquired).
    /// <paramref name="refuseWhenReaders"/>: set by the rewriting activities — the acquisition also fails while any
    /// reader (restore) is active on the target, atomically with the readers' own check of <see cref="RewritingActivities"/>.</summary>
    public bool TryAcquire(int accountId, string container, string activity = "Checking", bool refuseWhenReaders = false)
    {
        lock (_lock)
        {
            var key = Key(accountId, container);
            if (_busy.ContainsKey(key))
                return false;
            if (refuseWhenReaders && _readers.ContainsKey(key))
                return false;
            _busy[key] = activity;
            return true;
        }
    }

    /// <summary>Register a reader (a restore). Refused — with the conflicting label reported — while a
    /// rewriting activity holds the target; freely granted alongside a backup/check or other readers.</summary>
    public bool TryAddReader(int accountId, string container, out string? conflictingActivity)
    {
        lock (_lock)
        {
            var key = Key(accountId, container);
            if (_busy.TryGetValue(key, out var activity) && RewritingActivities.Contains(activity))
            {
                conflictingActivity = activity;
                return false;
            }
            _readers[key] = _readers.GetValueOrDefault(key) + 1;
            conflictingActivity = null;
            return true;
        }
    }

    /// <summary>Unregister a reader; the target frees for rewriters when the last one leaves.</summary>
    public void RemoveReader(int accountId, string container)
    {
        lock (_lock)
        {
            var key = Key(accountId, container);
            if (!_readers.TryGetValue(key, out var count))
                return;
            if (count <= 1)
                _readers.Remove(key);
            else
                _readers[key] = count - 1;
        }
    }

    /// <summary>Whether any reader (restore) is active on the target (query only) — what retention cleanup
    /// consults to stand down for a round rather than deleting blobs out from under a running restore.</summary>
    public bool HasReaders(int accountId, string container)
    {
        lock (_lock)
            return _readers.ContainsKey(Key(accountId, container));
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
