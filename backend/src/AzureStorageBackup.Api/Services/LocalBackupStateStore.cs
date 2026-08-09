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
            db.LocalBackupStates.Add(new LocalBackupState
            {
                AccountId = accountId, Container = container,
                InfoBytes = infoBytes, ETag = etag, UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            row.InfoBytes = infoBytes;
            row.ETag = etag;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
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
