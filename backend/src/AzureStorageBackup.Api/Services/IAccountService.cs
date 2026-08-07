using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>Azure Storage Account 的增删改查。敏感字段加解密对调用方透明。</summary>
public interface IAccountService
{
    Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default);

    Task<Account?> GetAsync(int id, CancellationToken ct = default);

    Task<Account> CreateAsync(Account account, CancellationToken ct = default);

    /// <summary>更新账户；不存在返回 null。</summary>
    Task<Account?> UpdateAsync(int id, Account update, CancellationToken ct = default);

    /// <summary>删除账户；不存在返回 false。</summary>
    Task<bool> DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// 每个账户被哪些备份配置占用（账户 id → 备份名，按名字排序）。**只**含真有占用的账户。
    /// <para>
    /// 库里 <c>BackupConfig.AccountId</c> 只是个 int——既没有导航属性，也没有外键约束，所以
    /// 删账户在数据库那一层是拦不住的，删完只会留下一批 AccountId 指向空号的孤儿配置。它们
    /// 一直到下次真跑起来才炸（<c>BackupRunner</c>/<c>CheckRunner</c>/<c>RestoreRunner</c> 那三处
    /// <c>?? throw new InvalidOperationException($"Account {id} not found.")</c>），定时任务的话
    /// 就是半夜失败、第二天才看见，还原更是等到真要恢复数据时才发现配置是坏的。
    /// 这个查询就是那道拦截的依据。
    /// </para>
    /// </summary>
    Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> GetBackupUsageAsync(CancellationToken ct = default);
}
