using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// An **in-process** cache of deserialized version indexes (singleton, shared across requests).
/// <para>
/// Why it is needed: <see cref="LocalIndexCache"/> stores **serialized bytes** in SQLite, so every read has to rebuild the
/// entire index into objects. Measured on a 500k-entry index: one deserialization + one full scan by
/// <see cref="VersionTreeService.Children"/> = 939 ms / 350 MB allocated — and the restore dialog goes through that whole process every time a directory is expanded.
/// </para>
/// <para>
/// The price is resident memory: a 500k-entry index is roughly 190 MB on the heap. So the capacity is configurable, and
/// <c>Backup__IndexCacheSize=0</c> disables it entirely (low-memory machines), in which case the behavior is exactly what it was before this cache layer existed.
/// </para>
/// <para>
/// **Contract: the instance handed out is shared, and the caller must not modify it** (<see cref="VersionIndex.Entries"/> and
/// friends are all mutable collections). The only thing that currently mutates an index object is <see cref="BackupRepairer"/>,
/// and it reads from the cloud store rather than this layer; so as not to rest this agreement on "nobody gets it wrong later",
/// the write path (<see cref="LocalIndexCache.PutAsync"/>) always **invalidates** the matching entry instead of putting the caller's object in — the price is merely one fewer hit next time.
/// </para>
/// </summary>
public sealed class VersionIndexMemoryCache(int capacity)
{
    private readonly record struct Key(int AccountId, string Container, int Version, long IdentityTicks);

    // Capacity is counted in items (usually 1–2), so a list ordered by recency of use is enough; a dedicated LRU structure isn't worth it.
    private readonly List<(Key Key, VersionIndex Index)> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>Upper bound on the number of cached items; 0 = disabled.</summary>
    public int Capacity { get; } = Math.Max(0, capacity);

    public bool Enabled => Capacity > 0;

    public bool TryGet(int accountId, string container, int version, long identityTicks, out VersionIndex index)
    {
        index = null!;
        if (!Enabled)
            return false;

        var key = new Key(accountId, container, version, identityTicks);
        lock (_gate)
        {
            var i = _entries.FindIndex(e => e.Key == key);
            if (i < 0)
                return false;

            var hit = _entries[i];
            _entries.RemoveAt(i);
            _entries.Add(hit); // Moved to the end = most recently used
            index = hit.Index;
            return true;
        }
    }

    public void Set(int accountId, string container, int version, long identityTicks, VersionIndex index)
    {
        if (!Enabled)
            return;

        var key = new Key(accountId, container, version, identityTicks);
        lock (_gate)
        {
            var i = _entries.FindIndex(e => e.Key == key);
            if (i >= 0)
                _entries.RemoveAt(i);
            _entries.Add((key, index));
            while (_entries.Count > Capacity)
                _entries.RemoveAt(0); // Least recently used
        }
    }

    /// <summary>A version's index has changed (backup wrote a new version, repair rewrote it, version retired) → discard it, never keep a stale copy.
    /// identityTicks takes no part in the match: after a container is rebuilt, entries under the old identity have to go as well.</summary>
    public void Invalidate(int accountId, string container, int version)
    {
        if (!Enabled)
            return;
        lock (_gate)
            _entries.RemoveAll(e => e.Key.AccountId == accountId
                && e.Key.Container == container && e.Key.Version == version);
    }

    /// <summary>Invalidate every version of a given (account, container) (backup config deleted / container rebuilt).</summary>
    public void InvalidateContainer(int accountId, string container)
    {
        if (!Enabled)
            return;
        lock (_gate)
            _entries.RemoveAll(e => e.Key.AccountId == accountId && e.Key.Container == container);
    }
}
