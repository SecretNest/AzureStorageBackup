using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 本地版本索引缓存（设计 §3.3）。大的第二级版本索引平时从本地 SQLite 读，避免每次备份/清理都下载解压云端索引；
/// 缓存未命中或身份不符时回落到云端并回填。云端信息文件仍为权威真相源，故不缓存。
/// </summary>
public interface ILocalIndexCache
{
    /// <summary>读取某版本索引：本地命中（且身份匹配）直接返回，否则下载云端并回填。</summary>
    Task<VersionIndex> ReadAsync(
        Account account, string container, int version, long identityTicks,
        string indexBlob, string? password, CancellationToken ct = default);

    /// <summary>写入/更新某版本索引缓存（备份写完新版本后调用）。</summary>
    Task PutAsync(int accountId, string container, int version, long identityTicks, VersionIndex index, CancellationToken ct = default);

    /// <summary>移除某版本缓存（版本被保留策略退役后调用）。</summary>
    Task RemoveAsync(int accountId, string container, int version, CancellationToken ct = default);
}

public sealed class LocalIndexCache(AppDbContext db, IBackupInfoStore store) : ILocalIndexCache
{
    public async Task<VersionIndex> ReadAsync(
        Account account, string container, int version, long identityTicks,
        string indexBlob, string? password, CancellationToken ct = default)
    {
        var row = await db.CachedVersionIndexes
            .FirstOrDefaultAsync(x => x.AccountId == account.Id && x.Container == container && x.Version == version, ct);

        if (row is not null && row.IdentityTicks == identityTicks)
            return IndexSerializer.DeserializeIndex(row.Bytes);

        // 未命中或 container 已重建（身份不符）→ 下载云端并回填。
        var index = await store.ReadIndexAsync(account, container, indexBlob, password, ct);
        await UpsertAsync(row, account.Id, container, version, identityTicks, index, ct);
        return index;
    }

    public async Task PutAsync(
        int accountId, string container, int version, long identityTicks, VersionIndex index, CancellationToken ct = default)
    {
        var row = await db.CachedVersionIndexes
            .FirstOrDefaultAsync(x => x.AccountId == accountId && x.Container == container && x.Version == version, ct);
        await UpsertAsync(row, accountId, container, version, identityTicks, index, ct);
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
