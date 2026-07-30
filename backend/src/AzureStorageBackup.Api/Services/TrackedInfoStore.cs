using Azure;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 本地权威的信息文件读写（设计 §3.3）。正常备份**不从云端读信息文件**（它可能落 Cold，取回有费）：
/// 本地有序列化副本就用本地；写入用 ETag <c>If-Match</c> 检测外部改动（多机/container 重建），冲突则清本地状态并报错重跑重同步。
/// 仅本地无副本时（首次/导入前）才读云端并回填。
/// </summary>
public sealed class TrackedInfoStore(IBackupInfoStore store, ILocalBackupStateStore state)
{
    /// <summary>本地是否已有权威状态（此备份由本工具建立并同步过）。为 true 时去重可纯本地判断，不读云端。</summary>
    public async Task<bool> HasLocalAsync(Account account, string container, CancellationToken ct = default) =>
        await state.TryGetAsync(account.Id, container, ct) is not null;

    /// <summary>加载信息文件：本地有则用本地（不读云端）；否则读云端并回填。均无返回 null（→ 新建）。</summary>
    public async Task<BackupInfoFile?> LoadAsync(Account account, string container, string? password, CancellationToken ct = default)
    {
        var local = await state.TryGetAsync(account.Id, container, ct);
        if (local is not null)
            return IndexSerializer.DeserializeInfoFile(local.Value.InfoBytes);

        var cloud = await store.ReadInfoWithETagAsync(account, container, password, ct);
        if (cloud is null)
            return null;

        await state.PutAsync(account.Id, container, IndexSerializer.SerializeInfoFile(cloud.Value.Info), cloud.Value.ETag, ct);
        return cloud.Value.Info;
    }

    /// <summary>提交信息文件：以本地记录的 ETag 做 If-Match 写云端，成功后更新本地。外部已改动 → 清本地并抛异常。</summary>
    public async Task WriteAsync(
        Account account, string container, BackupInfoFile info, string? password, AccessTier? tier, CancellationToken ct = default)
    {
        var local = await state.TryGetAsync(account.Id, container, ct);
        try
        {
            var newEtag = await store.WriteInfoConditionalAsync(
                account, container, info, password, tier, ifMatch: local?.ETag, ct);
            await state.PutAsync(account.Id, container, IndexSerializer.SerializeInfoFile(info), newEtag, ct);
        }
        // 412 = ETag 对不上，409 BlobAlreadyExists = 我们以为没有、它却已经在了。两者都确实是
        // "信息文件被别处改过"。
        //
        // 但**不能**把 409 一律收下：BlobArchived 也是 409，它说的是"这个 blob 已归档、动不了"，
        // 与"被别处改了"毫无关系。混在一起会给出一条彻底误导的错误，还顺手把本地权威状态清掉——
        // 而那份状态正是下一次备份免于读云端的依据，清了就得重新回填。
        catch (RequestFailedException ex) when (ex.Status == 412 || ex.ErrorCode == "BlobAlreadyExists")
        {
            await state.RemoveAsync(account.Id, container, ct);
            throw new InvalidOperationException(
                "Backup info file was modified elsewhere since last sync; local state cleared — re-run to re-sync.", ex);
        }
    }

    /// <summary>导入：用云端信息文件回填本地权威状态（供之后备份不再读云端）。返回读到的信息文件。</summary>
    public async Task<(BackupInfoFile Info, string ETag)?> SeedFromCloudAsync(
        Account account, string container, string? password, CancellationToken ct = default)
    {
        var cloud = await store.ReadInfoWithETagAsync(account, container, password, ct);
        if (cloud is not null)
            await state.PutAsync(account.Id, container, IndexSerializer.SerializeInfoFile(cloud.Value.Info), cloud.Value.ETag, ct);
        return cloud;
    }
}
