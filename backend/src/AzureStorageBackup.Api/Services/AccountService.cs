using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

public class AccountService(AppDbContext db) : IAccountService
{
    public async Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default) =>
        // NOCASE: SQLite compares by code point by default, sorting every uppercase letter before every lowercase one (see BackupConfigService.ListAsync).
        await db.Accounts.AsNoTracking()
            .OrderBy(a => EF.Functions.Collate(a.Name, "NOCASE")).ToListAsync(ct);

    public async Task<Account?> GetAsync(int id, CancellationToken ct = default) =>
        await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<Account> CreateAsync(Account account, CancellationToken ct = default)
    {
        if (account.CreatedAt == default)
            account.CreatedAt = DateTimeOffset.UtcNow;

        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task<Account?> UpdateAsync(int id, Account update, CancellationToken ct = default)
    {
        var existing = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (existing is null)
            return null;

        existing.Name = update.Name;
        existing.Description = update.Description;
        existing.BlobEndpoint = update.BlobEndpoint;
        existing.Region = update.Region;
        existing.AccountKeyProtected = update.AccountKeyProtected;
        existing.UseProxy = update.UseProxy;
        existing.ProxyMode = update.ProxyMode;
        existing.ProxyHost = update.ProxyHost;
        existing.ProxyPort = update.ProxyPort;
        existing.ProxyUsername = update.ProxyUsername;
        existing.ProxyPasswordProtected = update.ProxyPasswordProtected;

        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        var existing = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (existing is null)
            return false;

        db.Accounts.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> GetBackupUsageAsync(
        CancellationToken ct = default)
    {
        // Fetch everything once and group in memory: the list page wants occupancy for **every** account,
        // and querying per account would be N+1. At personal-backup scale (single-digit accounts, a few
        // dozen backups) this single pass costs nothing worth measuring.
        var rows = await db.BackupConfigs.AsNoTracking()
            .Select(c => new { c.AccountId, c.Name })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.AccountId)
            .ToDictionary(
                g => g.Key,
                // Sorted by name: this string goes into a UI tooltip as-is, and an unstable order would make the same page look different on every refresh.
                g => (IReadOnlyList<string>)[.. g.Select(r => r.Name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)]);
    }
}
