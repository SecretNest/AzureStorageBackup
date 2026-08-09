using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>CRUD for Azure Storage Accounts. Encryption and decryption of the sensitive fields is transparent to callers.</summary>
public interface IAccountService
{
    Task<IReadOnlyList<Account>> ListAsync(CancellationToken ct = default);

    Task<Account?> GetAsync(int id, CancellationToken ct = default);

    Task<Account> CreateAsync(Account account, CancellationToken ct = default);

    /// <summary>Update an account; returns null if it does not exist.</summary>
    Task<Account?> UpdateAsync(int id, Account update, CancellationToken ct = default);

    /// <summary>Delete an account; returns false if it does not exist.</summary>
    Task<bool> DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Which backup configs use each account (account id → backup names, sorted by name). Contains **only** accounts that really are in use.
    /// <para>
    /// <c>BackupConfig.AccountId</c> is just an int in the database — no navigation property, no foreign key constraint, so
    /// deleting an account cannot be stopped at the database layer; all it leaves behind is a pile of orphan configs whose
    /// AccountId points at nothing. They only blow up on the next real run (those three
    /// <c>?? throw new InvalidOperationException($"Account {id} not found.")</c> sites in <c>BackupRunner</c>/<c>CheckRunner</c>/<c>RestoreRunner</c>);
    /// for a scheduled task that means failing at 3am and noticing the next day, and restore is worse still — you find out the config is broken exactly when you need the data back.
    /// This query is what that refusal is based on.
    /// </para>
    /// </summary>
    Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> GetBackupUsageAsync(CancellationToken ct = default);
}
