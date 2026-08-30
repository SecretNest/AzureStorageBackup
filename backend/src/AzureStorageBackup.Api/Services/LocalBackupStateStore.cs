using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>Reads and writes the locally authoritative info-file state (the serialised info file plus the cloud ETag) (design §3.3).</summary>
public interface ILocalBackupStateStore
{
    Task<(byte[] InfoBytes, string ETag)?> TryGetAsync(int accountId, string container, CancellationToken ct = default);
    Task PutAsync(int accountId, string container, byte[] infoBytes, string etag, CancellationToken ct = default);
    Task RemoveAsync(int accountId, string container, CancellationToken ct = default);
}

public sealed class LocalBackupStateStore(AppDbContext db) : ILocalBackupStateStore
{
    public async Task<(byte[] InfoBytes, string ETag)?> TryGetAsync(int accountId, string container, CancellationToken ct = default)
    {
        var row = await db.LocalBackupStates
            .FirstOrDefaultAsync(x => x.AccountId == accountId && x.Container == container, ct);
        return row is null ? null : (row.InfoBytes, row.ETag);
    }

    public async Task PutAsync(int accountId, string container, byte[] infoBytes, string etag, CancellationToken ct = default)
    {
        var row = await db.LocalBackupStates
            .FirstOrDefaultAsync(x => x.AccountId == accountId && x.Container == container, ct);
        if (row is null)
        {
            var added = new LocalBackupState
            {
                AccountId = accountId, Container = container,
                InfoBytes = infoBytes, ETag = etag, UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.LocalBackupStates.Add(added);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Two scopes cold-missed the same (account, container) — an ETag conflict had just cleared
                // the row, both of LoadAsync's backfills read "no row", and the loser's insert hit the unique
                // index. A harmless race on a locally cached copy, not an error (the same discipline as
                // LocalIndexCache.UpsertAsync): fall back to updating the winner's row rather than throwing
                // a bare 500 out of whatever read path happened to be backfilling.
                db.Entry(added).State = EntityState.Detached;
                var existing = await db.LocalBackupStates.FirstOrDefaultAsync(
                    x => x.AccountId == accountId && x.Container == container, ct);
                if (existing is null)
                    throw; // not the duplicate-insert race after all — surface the original failure
                existing.InfoBytes = infoBytes;
                existing.ETag = etag;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            return;
        }
        row.InfoBytes = infoBytes;
        row.ETag = etag;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(int accountId, string container, CancellationToken ct = default)
    {
        var row = await db.LocalBackupStates
            .FirstOrDefaultAsync(x => x.AccountId == accountId && x.Container == container, ct);
        if (row is not null)
        {
            db.LocalBackupStates.Remove(row);
            await db.SaveChangesAsync(ct);
        }
    }
}
