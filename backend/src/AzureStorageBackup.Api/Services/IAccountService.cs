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
}
