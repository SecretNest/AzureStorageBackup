using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

public class AccountService(AppDbContext db) : IAccountService
{
    public async Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default) =>
        await db.Accounts.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);

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
}
