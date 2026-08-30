using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

public class AccountService(AppDbContext db) : IAccountService
{
    // One writer at a time through the alias check-then-save: the check reads on one context, the insert
    // commits an await later, and two concurrent creates (a double-click, two admin sessions) could both
    // pass the check before either lands. There is deliberately no DB unique index to backstop this —
    // pre-existing duplicates in old databases must keep migrating (see RejectEndpointAliasAsync) — so the
    // serialization has to happen here. Static: the service is scoped, one instance per request.
    private static readonly SemaphoreSlim _endpointWriteGate = new(1, 1);

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

        await _endpointWriteGate.WaitAsync(ct);
        try
        {
            await RejectEndpointAliasAsync(account.BlobEndpoint, exceptId: null, ct);
            db.Accounts.Add(account);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            _endpointWriteGate.Release();
        }
        return account;
    }

    /// <summary>Two account records pointing at one endpoint are two names for the same real storage account —
    /// and everything that serializes work per container (the busy tracker, most of all) keys on the LOCAL
    /// record id, so the alias would let a backup on one record and a retention cleanup on the other run
    /// against the literal same cloud container at once, the cleanup deleting what the backup is uploading.
    /// The operator's ruling: one endpoint, one record ("直接禁止同一个endpoint被添加超过一次"). The comparison is
    /// normalized (case, trailing slash) so a cosmetic variation cannot slip past. Existing duplicates in an
    /// old database are left alone — only new additions and edits are gated.</summary>
    private async Task RejectEndpointAliasAsync(string endpoint, int? exceptId, CancellationToken ct)
    {
        static string Normalize(string e) => e.TrimEnd('/').ToLowerInvariant();
        var normalized = Normalize(endpoint);
        var clash = (await db.Accounts.AsNoTracking().Select(a => new { a.Id, a.BlobEndpoint, a.Name }).ToListAsync(ct))
            .FirstOrDefault(a => a.Id != exceptId && Normalize(a.BlobEndpoint) == normalized);
        if (clash is not null)
            throw new InvalidOperationException(
                $"The endpoint {endpoint} is already registered by the account \"{clash.Name}\" — one storage account, one entry (a duplicate would let two operations run against the same container at once).");
    }

    public async Task<Account?> UpdateAsync(int id, Account update, CancellationToken ct = default)
    {
        var existing = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (existing is null)
            return null;

        await _endpointWriteGate.WaitAsync(ct);
        try
        {
            await RejectEndpointAliasAsync(update.BlobEndpoint, exceptId: id, ct);
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
        }
        finally
        {
            _endpointWriteGate.Release();
        }
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
