using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Local version index cache (design §3.3). The large second-level version indexes are normally read from local disk, which avoids downloading and extracting the cloud index on every backup/cleanup;
/// on a cache miss or an identity mismatch it falls back to the cloud and backfills. The cloud info file remains the authoritative source of truth, so it is not cached.
/// <para>
/// The bytes live in files (<see cref="VersionIndexFileStore"/>), not in SQLite. They used to be one row each, and a
/// single row can hold 100 MB — writing it took the database's one write lock for tens of seconds on a loaded disk,
/// which blocked every other writer in the process, a config edit included. See that class for the full account.
/// SQLite is still consulted for one thing only: draining rows written before the move.
/// </para>
/// </summary>
public interface ILocalIndexCache
{
    /// <summary>Read a version index: return it directly on a local hit (with matching identity), otherwise download from the cloud and backfill.
    /// <paramref name="indexVolumes"/> is only consulted on a miss; it comes from <see cref="BackupVersion.IndexVolumes"/>.</summary>
    Task<VersionIndex> ReadAsync(
        Account account, string container, int version, long identityTicks,
        string indexBlob, string? password, int indexVolumes = 1, CancellationToken ct = default);

    /// <summary>Write/update the cache for a version index (called after a backup finishes writing a new version).</summary>
    Task PutAsync(int accountId, string container, int version, long identityTicks, VersionIndex index, CancellationToken ct = default);

    /// <summary>Remove the cache for a version (called after the retention policy retires it).</summary>
    Task RemoveAsync(int accountId, string container, int version, CancellationToken ct = default);

    /// <summary>Remove every cached version index for a given (account, container) (called when a backup config is deleted), so that
    /// rebuilding a backup on the same account+container leaves no cached index under the old identity behind to mismatch the data.</summary>
    Task RemoveForContainerAsync(int accountId, string container, CancellationToken ct = default);
}

public sealed class LocalIndexCache(
    AppDbContext db, IBackupInfoStore store, VersionIndexFileStore files, VersionIndexMemoryCache? memory = null)
    : ILocalIndexCache
{
    // Omitting it disables the in-process layer: unit tests care about the persistent layer and should not be disturbed by a cross-request cache.
    // In production DI injects an instance configured from Backup__IndexCacheSize.
    private readonly VersionIndexMemoryCache _memory = memory ?? new VersionIndexMemoryCache(0);

    public async Task<VersionIndex> ReadAsync(
        Account account, string container, int version, long identityTicks,
        string indexBlob, string? password, int indexVolumes = 1, CancellationToken ct = default)
    {
        // The persistent layer holds **serialized bytes**, so even a hit still has to rebuild the entire index into objects (measured
        // at roughly 0.9 s / 350 MB allocated for 500k entries). The restore dialog goes through it every time a directory is
        // expanded, hence one more layer of in-process object cache on top; at capacity 0 that layer is bypassed entirely and the behavior matches what it was before (VersionIndexMemoryCache).
        if (_memory.TryGet(account.Id, container, version, identityTicks, out var cached))
            return cached;

        if (await files.ReadAsync(account.Id, container, version, identityTicks, ct) is { } bytes
            && Rebuild(bytes) is { } fromFile)
        {
            _memory.Set(account.Id, container, version, identityTicks, fromFile);
            return fromFile;
        }

        // A row left by the version before this cache moved out of SQLite. Migrated here rather than in a startup pass:
        // the pass would have to read every cached index in the database — hundreds of MB — before the app served its
        // first request, whereas this reads exactly the one index a caller was about to read anyway, and only once.
        if (await MigrateLegacyRowAsync(account.Id, container, version, identityTicks, ct) is { } migrated)
        {
            _memory.Set(account.Id, container, version, identityTicks, migrated);
            return migrated;
        }

        // Miss, or the container has been rebuilt (identity mismatch) -> download from the cloud and backfill.
        var index = await store.ReadIndexAsync(account, container, indexBlob, password, indexVolumes, ct);
        await files.WriteAsync(account.Id, container, version, identityTicks, IndexSerializer.SerializeIndex(index), ct);
        _memory.Set(account.Id, container, version, identityTicks, index);
        return index;
    }

    public async Task PutAsync(
        int accountId, string container, int version, long identityTicks, VersionIndex index, CancellationToken ct = default)
    {
        await files.WriteAsync(accountId, container, version, identityTicks, IndexSerializer.SerializeIndex(index), ct);
        await DropLegacyRowAsync(accountId, container, version, ct);
        // Store the bytes, and do **not** put the caller's object into the memory cache: index objects are mutable (BackupRepairer
        // adds things to UnrecoverablePaths, for one), and sharing an instance the caller still holds will bite eventually. The
        // price of invalidating is merely one fewer hit on the next read.
        _memory.Invalidate(accountId, container, version);
    }

    public async Task RemoveAsync(int accountId, string container, int version, CancellationToken ct = default)
    {
        files.Remove(accountId, container, version);
        await DropLegacyRowAsync(accountId, container, version, ct);
        _memory.Invalidate(accountId, container, version);
    }

    public async Task RemoveForContainerAsync(int accountId, string container, CancellationToken ct = default)
    {
        files.RemoveForContainer(accountId, container);
        // Guarded by a read for the same reason as DropLegacyRowAsync: once the migration is behind us this costs a
        // query and no write lock, where an unconditional ExecuteDelete would open a write transaction every time.
        if (await db.CachedVersionIndexes.AnyAsync(x => x.AccountId == accountId && x.Container == container, ct))
            await db.CachedVersionIndexes
                .Where(x => x.AccountId == accountId && x.Container == container)
                .ExecuteDeleteAsync(ct);
        _memory.InvalidateContainer(accountId, container);
    }

    /// <summary>
    /// Hand one pre-move row over to the file store and drop it. Returns the index when the row was still valid, null
    /// when there was no row or its identity no longer matches (in which case it is deleted anyway — a row under a
    /// superseded identity is dead weight, and the caller is on its way to the cloud regardless).
    /// </summary>
    private async Task<VersionIndex?> MigrateLegacyRowAsync(
        int accountId, string container, int version, long identityTicks, CancellationToken ct)
    {
        var row = await db.CachedVersionIndexes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.AccountId == accountId && x.Container == container && x.Version == version, ct);
        if (row is null)
            return null;

        var index = row.IdentityTicks == identityTicks ? Rebuild(row.Bytes) : null;
        if (index is not null)
            await files.WriteAsync(accountId, container, version, identityTicks, row.Bytes, ct);
        await DropLegacyRowAsync(accountId, container, version, ct);
        return index;
    }

    /// <summary>
    /// Delete a pre-move row if one is still there. The <c>Any</c> probe ahead of the delete is the point: after the
    /// migration has run once, every call takes the read branch — and a read needs no write lock, so the write path
    /// this whole change exists to unblock stays free of one. An unconditional ExecuteDelete would quietly hand the
    /// lock back to it on every backup commit.
    /// </summary>
    private async Task DropLegacyRowAsync(int accountId, string container, int version, CancellationToken ct)
    {
        if (!await db.CachedVersionIndexes.AnyAsync(
                x => x.AccountId == accountId && x.Container == container && x.Version == version, ct))
            return;

        await db.CachedVersionIndexes
            .Where(x => x.AccountId == accountId && x.Container == container && x.Version == version)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Deserialize, treating any failure as a miss. A cache entry that cannot be read is a slower read, never a failed
    /// restore: whatever is wrong with these bytes, the cloud copy is authoritative and one download away. Written as a
    /// catch-all deliberately — the ways a byte array can fail to be an index are not a list worth trying to enumerate,
    /// and getting that list wrong would turn a corrupt cache entry into a broken restore.
    /// </summary>
    private static VersionIndex? Rebuild(byte[] bytes)
    {
        try { return IndexSerializer.DeserializeIndex(bytes); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }
}
