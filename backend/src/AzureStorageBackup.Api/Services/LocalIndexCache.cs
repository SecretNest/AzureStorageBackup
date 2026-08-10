using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Local version index cache (design §3.3). The large second-level version indexes are normally read from local SQLite, which avoids downloading and extracting the cloud index on every backup/cleanup;
/// on a cache miss or an identity mismatch it falls back to the cloud and backfills. The cloud info file remains the authoritative source of truth, so it is not cached.
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
    AppDbContext db, IBackupInfoStore store, VersionIndexMemoryCache? memory = null) : ILocalIndexCache
{
    // Omitting it disables the in-process layer: unit tests care about the SQLite layer and should not be disturbed by a cross-request cache.
    // In production DI injects an instance configured from Backup__IndexCacheSize.
    private readonly VersionIndexMemoryCache _memory = memory ?? new VersionIndexMemoryCache(0);

    public async Task<VersionIndex> ReadAsync(
        Account account, string container, int version, long identityTicks,
        string indexBlob, string? password, int indexVolumes = 1, CancellationToken ct = default)
    {
        // The row holds **serialized bytes**, so even a SQLite hit still has to rebuild the entire index into objects (measured
        // at roughly 0.9 s / 350 MB allocated for 500k entries). The restore dialog goes through it every time a directory is
        // expanded, hence one more layer of in-process object cache on top; at capacity 0 that layer is bypassed entirely and the behavior matches what it was before (VersionIndexMemoryCache).
        if (_memory.TryGet(account.Id, container, version, identityTicks, out var cached))
            return cached;

        var row = await db.CachedVersionIndexes
            .FirstOrDefaultAsync(x => x.AccountId == account.Id && x.Container == container && x.Version == version, ct);

        if (row is not null && row.IdentityTicks == identityTicks)
        {
            var fromRow = IndexSerializer.DeserializeIndex(row.Bytes);
            _memory.Set(account.Id, container, version, identityTicks, fromRow);
            return fromRow;
        }

        // Miss, or the container has been rebuilt (identity mismatch) → download from the cloud and backfill.
        var index = await store.ReadIndexAsync(account, container, indexBlob, password, indexVolumes, ct);
        await UpsertAsync(row, account.Id, container, version, identityTicks, index, ct);
        _memory.Set(account.Id, container, version, identityTicks, index);
        return index;
    }

    public async Task PutAsync(
        int accountId, string container, int version, long identityTicks, VersionIndex index, CancellationToken ct = default)
    {
        var row = await db.CachedVersionIndexes
            .FirstOrDefaultAsync(x => x.AccountId == accountId && x.Container == container && x.Version == version, ct);
        await UpsertAsync(row, accountId, container, version, identityTicks, index, ct);
        // Store the bytes, and do **not** put the caller's object into the memory cache: index objects are mutable (BackupRepairer
        // adds things to UnrecoverablePaths, for one), and sharing an instance the caller still holds will bite eventually. The
        // price of invalidating is merely one fewer hit on the next read.
        _memory.Invalidate(accountId, container, version);
    }

    public async Task RemoveAsync(int accountId, string container, int version, CancellationToken ct = default)
    {
        var row = await db.CachedVersionIndexes
            .FirstOrDefaultAsync(x => x.AccountId == accountId && x.Container == container && x.Version == version, ct);
        if (row is not null)
        {
            db.CachedVersionIndexes.Remove(row);
            await db.SaveChangesAsync(ct);
        }
        _memory.Invalidate(accountId, container, version);
    }

    public async Task RemoveForContainerAsync(int accountId, string container, CancellationToken ct = default)
    {
        await db.CachedVersionIndexes
            .Where(x => x.AccountId == accountId && x.Container == container)
            .ExecuteDeleteAsync(ct);
        _memory.InvalidateContainer(accountId, container);
    }

    private async Task UpsertAsync(
        CachedVersionIndex? row, int accountId, string container, int version, long identityTicks,
        VersionIndex index, CancellationToken ct)
    {
        var bytes = IndexSerializer.SerializeIndex(index);
        if (row is null)
        {
            db.CachedVersionIndexes.Add(new CachedVersionIndex
            {
                AccountId = accountId, Container = container, Version = version,
                IdentityTicks = identityTicks, Bytes = bytes, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            row.IdentityTicks = identityTicks;
            row.Bytes = bytes;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }
}
