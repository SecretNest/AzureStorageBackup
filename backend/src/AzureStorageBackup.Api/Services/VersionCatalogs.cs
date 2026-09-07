using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Implements <see cref="IVersionCatalogs"/>'s lazy migration: a version is pulled into the SQLite catalog the
/// first time something needs it, from whichever of the older homes still has it — never eagerly, and never more
/// than once, so a container nobody has touched since the upgrade never pays for a migration at all.
/// <para>
/// <see cref="EnsureVersionAsync"/> probes read-only first and only takes the container's write lock, and opens the
/// catalog read-write, when the probe comes back empty or stale. That keeps the hot path — a version already
/// migrated — off <see cref="VersionCatalogStore"/>'s once-per-process <c>PRAGMA quick_check</c> (paid only on the
/// first read-write open of a path), and off the lock every other writer of the same container contends for.
/// </para>
/// </summary>
public sealed class VersionCatalogs(
    VersionCatalogStore catalogs, VersionIndexFileStore legacyFiles, AppDbContext db, IBackupInfoStore store,
    ILogger<VersionCatalogs>? logger = null) : IVersionCatalogs
{
    public Task<VersionCatalog> OpenAsync(int accountId, string container, bool readOnly, CancellationToken ct = default) =>
        catalogs.OpenAsync(accountId, container, readOnly, ct);

    public Task<IDisposable> LockForWriteAsync(int accountId, string container, CancellationToken ct = default) =>
        catalogs.LockForWriteAsync(accountId, container, ct);

    public async Task EnsureVersionAsync(
        Account account, string container, BackupVersion version, long identityTicks, string? password, CancellationToken ct = default)
    {
        // An <c>.idx</c> file existing at all means it was written **after** the catalog last held this version:
        // TryImportFromIdxFileAsync deletes the file it consumes, and no run writes one any more. What is left
        // writing them rewrites an index out of band — a repair marking content unrecoverable is the one that
        // matters, and its marks are what the next backup reads to exclude a damaged address from dedup and heal it
        // in passing. So a file, when there is one, outranks the row already in the catalog, and the probe below is
        // not allowed to short-circuit past it. One File.Exists per version per run, which answers no on every
        // ordinary run; the locked path is the one that reads it properly.
        var rewritten = File.Exists(legacyFiles.PathFor(account.Id, container, version.Version));

        // Read-only probe: no quick_check, no write lock. A container whose catalog has never been opened for
        // writing in this process yet has no file at all — that is not an error here, just a sign the locked path
        // below has work to do.
        try
        {
            await using var probe = await catalogs.OpenAsync(account.Id, container, readOnly: true, ct);
            if (!rewritten
                && await probe.GetVersionAsync(version.Version, ct) is { } row && row.Identity == identityTicks)
                return;
        }
        catch (FileNotFoundException)
        {
            // No catalog yet; fall through to the locked path, which creates one.
        }

        using var _ = await catalogs.LockForWriteAsync(account.Id, container, ct);
        await using var catalog = await catalogs.OpenAsync(account.Id, container, readOnly: false, ct);

        // 1. the .idx file, ahead of the catalog row for the reason above
        if (await TryImportFromIdxFileAsync(catalog, account.Id, container, version.Version, identityTicks, ct))
            return;

        if (await catalog.GetVersionAsync(version.Version, ct) is { } again && again.Identity == identityTicks)
        {
            // Either the catalog always had it, or another caller imported it while this one waited for the lock.
            // Any file still sitting there survived the import attempt above, which means it is under a superseded
            // identity — nobody can ever use it, and left in place it would send every later run down this locked
            // path to be told the same thing. (An unparseable one is already deleted by the attempt itself.)
            if (rewritten)
                legacyFiles.Remove(account.Id, container, version.Version);
            return;
        }

        // 2. the legacy row (pre-.idx, still in app.db)
        var legacy = await db.CachedVersionIndexes.AsNoTracking().FirstOrDefaultAsync(
            x => x.AccountId == account.Id && x.Container == container && x.Version == version.Version, ct);
        if (legacy is not null)
        {
            if (legacy.IdentityTicks == identityTicks)
            {
                using var reader = new IndexStreamReader(new MemoryStream(legacy.Bytes));
                await catalog.ImportVersionAsync(version.Version, identityTicks, reader, ct);
            }

            // Dropped either way: a row under a superseded identity is dead weight once the catalog is about to go
            // to the cloud for the current one regardless.
            await db.CachedVersionIndexes.Where(x => x.Id == legacy.Id).ExecuteDeleteAsync(ct);
            if (legacy.IdentityTicks == identityTicks)
                return;
        }

        // 3. the cloud — the source of truth every other branch above exists only to avoid re-downloading from.
        var temp = Path.Combine(Path.GetTempPath(), "asb-index", Guid.NewGuid().ToString("N") + ".idx");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            await store.ReadIndexToFileAsync(account, container, version.IndexBlob, password, version.IndexVolumes, temp, ct);
            await using var file = File.OpenRead(temp);
            using var reader = new IndexStreamReader(file);
            await catalog.ImportVersionAsync(version.Version, identityTicks, reader, ct);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* temp space; nothing further to do about a failed cleanup */ }
        }
    }

    /// <summary>Tries the version's cached <c>.idx</c> file. Returns false (leaving the file untouched) when there is
    /// no file, or its identity does not match — today's <see cref="VersionIndexFileStore.ReadAsync"/> treats that
    /// the same way, as a plain miss the next successful write will eventually overwrite. A file whose header
    /// matches but whose body cannot be parsed (truncated by a prior crash mid-write, say) is different: the import
    /// throws out of a rolled-back transaction, so the catalog gains nothing from it, but the bad file itself would
    /// keep failing forever if left in place — so that case, and only that case, deletes it before falling through.</summary>
    private async Task<bool> TryImportFromIdxFileAsync(
        VersionCatalog catalog, int accountId, string container, int version, long identityTicks, CancellationToken ct)
    {
        if (await legacyFiles.OpenBodyAsync(accountId, container, version, identityTicks, ct) is not { } body)
            return false;

        try
        {
            await using (body)
            using (var reader = new IndexStreamReader(body))
                await catalog.ImportVersionAsync(version, identityTicks, reader, ct);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or InvalidDataException)
        {
            logger?.LogWarning(ex,
                "The cached index file for account {AccountId} container {Container} version {Version} failed to parse; deleting it.",
                accountId, container, version);
            legacyFiles.Remove(accountId, container, version);
            return false;
        }

        legacyFiles.Remove(accountId, container, version);
        return true;
    }

    public async Task RemoveVersionAsync(int accountId, string container, int version, CancellationToken ct = default)
    {
        using var _ = await catalogs.LockForWriteAsync(accountId, container, ct);
        await using var catalog = await catalogs.OpenAsync(accountId, container, readOnly: false, ct);
        await catalog.RemoveVersionAsync(version, ct);
        legacyFiles.Remove(accountId, container, version);
        await DropLegacyRowAsync(accountId, container, version, ct);
    }

    public async Task RemoveContainerAsync(int accountId, string container, CancellationToken ct = default)
    {
        // Not called while holding LockForWriteAsync: VersionCatalogStore.RemoveContainerAsync takes the same
        // (non-reentrant) semaphore internally.
        await catalogs.RemoveContainerAsync(accountId, container, ct);
        legacyFiles.RemoveForContainer(accountId, container);

        if (await db.CachedVersionIndexes.AnyAsync(x => x.AccountId == accountId && x.Container == container, ct))
            await db.CachedVersionIndexes
                .Where(x => x.AccountId == accountId && x.Container == container)
                .ExecuteDeleteAsync(ct);
    }

    /// <summary>Guarded by a read for the same reason <c>LocalIndexCache.DropLegacyRowAsync</c> is: once the
    /// migration off <c>CachedVersionIndexes</c> is behind a container, every call here takes the read branch, and a
    /// read needs no write lock — where an unconditional <c>ExecuteDelete</c> would open one every time.</summary>
    private async Task DropLegacyRowAsync(int accountId, string container, int version, CancellationToken ct)
    {
        if (!await db.CachedVersionIndexes.AnyAsync(
                x => x.AccountId == accountId && x.Container == container && x.Version == version, ct))
            return;

        await db.CachedVersionIndexes
            .Where(x => x.AccountId == accountId && x.Container == container && x.Version == version)
            .ExecuteDeleteAsync(ct);
    }
}
