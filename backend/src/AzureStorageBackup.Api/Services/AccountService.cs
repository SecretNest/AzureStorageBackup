using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

public class AccountService(AppDbContext db) : IAccountService
{
    public async Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default) =>
        // NOCASE：SQLite 默认按码点比，大写字母会整体排在小写字母前面（见 BackupConfigService.ListAsync）。
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
        // 一次取回全部再在内存里分组：列表页要的是**所有**账户的占用情况，逐个账户查就是 N+1。
        // 个人备份的规模（账户个位数、备份几十条）下这一趟的代价可以忽略。
        var rows = await db.BackupConfigs.AsNoTracking()
            .Select(c => new { c.AccountId, c.Name })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.AccountId)
            .ToDictionary(
                g => g.Key,
                // 按名字排序：这串要原样进界面的悬浮提示，顺序不稳定会让同一个页面每次刷新都换个样。
                g => (IReadOnlyList<string>)[.. g.Select(r => r.Name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)]);
    }
}
