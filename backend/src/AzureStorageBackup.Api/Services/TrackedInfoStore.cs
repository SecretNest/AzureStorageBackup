using Azure;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Locally authoritative reads and writes of the info file (design §3.3). A normal backup **never reads the info file from the cloud** (it may sit in Cold, where retrieval costs money):
/// if a serialized copy exists locally, use the local one; writes use an ETag <c>If-Match</c> to detect external modification (several machines / a rebuilt container), and on conflict the local state is cleared and an error tells you to re-run to re-sync.
/// Only when there is no local copy (first run / before an import) does it read the cloud and backfill.
/// </summary>
public sealed class TrackedInfoStore(IBackupInfoStore store, ILocalBackupStateStore state)
{
    /// <summary>Whether authoritative state already exists locally (this backup was created and synced by this tool). When true, dedup can be decided purely locally without reading the cloud.</summary>
    public async Task<bool> HasLocalAsync(Account account, string container, CancellationToken ct = default) =>
        await state.TryGetAsync(account.Id, container, ct) is not null;

    /// <summary>Loads the info file: if a local copy exists, use it (no cloud read); otherwise read the cloud and backfill. If neither exists, returns null (→ create a new one).</summary>
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

    /// <summary>Commits the info file: writes to the cloud with the locally recorded ETag as If-Match, then updates the local copy on success. Changed externally → clear the local state and throw.</summary>
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
        // 412 = the ETag does not match; 409 BlobAlreadyExists = we assumed it was absent and it was already there. Both really do mean
        // "the info file was changed somewhere else".
        //
        // But we must **not** take every 409: BlobArchived is a 409 too, and it says "this blob is archived, you cannot touch it",
        // which has nothing to do with "changed somewhere else". Lumping them together produces a thoroughly misleading error and wipes
        // the local authoritative state on the way out — and that state is exactly what lets the next backup skip reading the cloud; clear it and it all has to be backfilled again.
        catch (RequestFailedException ex) when (ex.Status == 412 || ex.ErrorCode == "BlobAlreadyExists")
        {
            await state.RemoveAsync(account.Id, container, ct);
            throw new InvalidOperationException(
                "Backup info file was modified elsewhere since last sync; local state cleared — re-run to re-sync.", ex);
        }
    }

    /// <summary>Import: backfills the local authoritative state from the cloud info file (so later backups no longer read the cloud). Returns the info file that was read.</summary>
    public async Task<(BackupInfoFile Info, string ETag)?> SeedFromCloudAsync(
        Account account, string container, string? password, CancellationToken ct = default)
    {
        var cloud = await store.ReadInfoWithETagAsync(account, container, password, ct);
        if (cloud is not null)
            await state.PutAsync(account.Id, container, IndexSerializer.SerializeInfoFile(cloud.Value.Info), cloud.Value.ETag, ct);
        return cloud;
    }
}
