using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using Microsoft.Data.Sqlite;
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
/// <param name="tempRoot">Where the cloud import's decoded index is written before it is streamed into the catalog.
/// It is the same directory <see cref="BackupInfoStore"/> stages its own decode under (<c>{tempPath}/index</c>), and
/// for the same reason: a multi-million-entry index is hundreds of MB decoded, and the system temp dir on a NAS is
/// quite often a small tmpfs. <c>BackupInfoStore.ClearStale</c> already sweeps it at startup.</param>
public sealed class VersionCatalogs(
    VersionCatalogStore catalogs, VersionIndexFileStore legacyFiles, AppDbContext db, IBackupInfoStore store,
    ILogger<VersionCatalogs>? logger = null, string? tempRoot = null) : IVersionCatalogs
{
    private readonly string _tempRoot = tempRoot ?? Path.Combine(Path.GetTempPath(), "asb-index");

    public Task<VersionCatalog> OpenAsync(int accountId, string container, bool readOnly, CancellationToken ct = default) =>
        catalogs.OpenAsync(accountId, container, readOnly, ct);

    public Task<VersionCatalog> OpenForWriteAsync(
        CatalogWriteLock held, int accountId, string container, CancellationToken ct = default) =>
        catalogs.OpenForWriteAsync(held, accountId, container, ct);

    public Task<CatalogWriteLock> LockForWriteAsync(int accountId, string container, CancellationToken ct = default) =>
        catalogs.LockForWriteAsync(accountId, container, ct);

    public async Task EnsureVersionAsync(
        Account account, string container, BackupVersion version, long identityTicks, string? password, CancellationToken ct = default)
    {
        // Read-only probe: no quick_check, no write lock. A container whose catalog has never been opened for
        // writing in this process yet has no file at all — that is not an error here, just a sign the locked path
        // below has work to do.
        try
        {
            await using var probe = await catalogs.OpenAsync(account.Id, container, readOnly: true, ct);
            if (await probe.GetVersionAsync(version.Version, ct) is { } row && row.Identity == identityTicks)
                return;
        }
        catch (FileNotFoundException)
        {
            // No catalog yet; fall through to the locked path, which creates one.
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 /* SQLITE_CORRUPT */ or 26 /* SQLITE_NOTADB */)
        {
            // The file on disk is not something SQLite can read: a page torn by a power loss, or bytes that are not
            // a database at all. A read-only handle is never the one to fix that (it must not delete a file a
            // writer may be holding), so this is a miss, not a failure — the locked path below opens the same file
            // for writing, which is where the recovery lives. Without this the container would be unusable until an
            // operator deleted the file by hand: every backup, check, restore, retention round and UI browse of it
            // goes through this probe.
            //
            // And if this process already opened this exact path for writing once before — quick_check paid for
            // and passed, back when the file was still good — the write open below would trust that and skip the
            // check, open the damaged file straight through, and hand back a catalog whose first real query throws
            // this same error uncaught. Forgetting the mark makes the write open re-earn it.
            catalogs.ForgetChecked(account.Id, container);
            logger?.LogWarning(ex,
                "The catalog for account {AccountId} container {Container} is unreadable; rebuilding it from the cloud.",
                account.Id, container);
        }

        using var held = await catalogs.LockForWriteAsync(account.Id, container, ct);
        await using var catalog = await catalogs.OpenForWriteAsync(held, account.Id, container, ct);
        await ImportMissingAsync(catalog, account, container, version, identityTicks, password, ct);
    }

    /// <summary>Versions missing from the catalog at or above which <see cref="EnsureVersionsAsync"/> takes the
    /// content-keyed indexes down for the duration. One missing version is the routine case (the run that just
    /// finished writes its own, and the next run's probe finds everything else in place); the indexes are kept
    /// live for it, because rebuilding three trees over the whole history costs more than one version's random
    /// inserts. Two or more is a migration — a container whose catalog has yet to be built — and there the trade
    /// reverses: the rebuild is one sort per index, while the inserts are a random page read per row per index
    /// over every version already in.</summary>
    internal const int BulkImportThreshold = 2;

    public async Task EnsureVersionsAsync(
        Account account, string container, IReadOnlyList<BackupVersion> versions, long identityTicks, string? password,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        // One read-only probe for the lot, on the same terms as EnsureVersionAsync's: a missing file and an
        // unreadable one are both "everything is missing", and the write open below is where the latter is fixed.
        var missing = new List<BackupVersion>();
        try
        {
            await using var probe = await catalogs.OpenAsync(account.Id, container, readOnly: true, ct);
            foreach (var version in versions)
                if (await probe.GetVersionAsync(version.Version, ct) is not { } row || row.Identity != identityTicks)
                    missing.Add(version);
        }
        catch (FileNotFoundException)
        {
            missing = [.. versions];
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 /* SQLITE_CORRUPT */ or 26 /* SQLITE_NOTADB */)
        {
            catalogs.ForgetChecked(account.Id, container);
            logger?.LogWarning(ex,
                "The catalog for account {AccountId} container {Container} is unreadable; rebuilding it from the cloud.",
                account.Id, container);
            missing = [.. versions];
        }

        progress?.Report(versions.Count - missing.Count);
        if (missing.Count == 0)
            return;

        var done = versions.Count - missing.Count;
        if (missing.Count < BulkImportThreshold)
        {
            foreach (var version in missing)
            {
                await EnsureVersionAsync(account, container, version, identityTicks, password, ct);
                progress?.Report(++done);
            }

            return;
        }

        using var held = await catalogs.LockForWriteAsync(account.Id, container, ct);
        await using var catalog = await catalogs.OpenForWriteAsync(held, account.Id, container, ct);

        // Dropped for the duration and rebuilt at the end — see CatalogSql.GlobalIndexNames for the measurement
        // behind this. Not rebuilt on the way out of a failure or a stop: the rebuild takes as long as the
        // history is big, and a stop pressed during a migration wants the run to end, not to sort three indexes
        // first. The next write open's schema pass rebuilds them; in between, queries are slower and still right.
        await catalog.DropGlobalIndexesAsync(ct);
        foreach (var version in missing)
        {
            await ImportMissingAsync(catalog, account, container, version, identityTicks, password, ct);
            progress?.Report(++done);
        }

        await catalog.RebuildGlobalIndexesAsync(ct);
    }

    /// <summary>The migration chain proper, on a catalog already open for writing under the container's lock:
    /// <c>.idx</c> file → legacy row → cloud. A version another caller imported while this one waited for the
    /// lock is found by the first check and costs nothing further.</summary>
    private async Task ImportMissingAsync(
        VersionCatalog catalog, Account account, string container, BackupVersion version, long identityTicks,
        string? password, CancellationToken ct)
    {
        // Either the catalog always had it, or another caller imported it while this one waited for the lock.
        if (await catalog.GetVersionAsync(version.Version, ct) is { } again && again.Identity == identityTicks)
            return;

        // 1. the .idx file an older build left behind
        if (await TryImportFromIdxFileAsync(catalog, account.Id, container, version.Version, identityTicks, ct))
            return;

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
        // In its own directory under the temp root, not a bare file beside it: that is the shape
        // BackupInfoStore.ClearStale sweeps at startup (one directory per call, deleted whole), so a process killed
        // mid-download does not leave a hundreds-of-MB decoded index behind forever.
        var work = Path.Combine(_tempRoot, "catalog-import-" + Guid.NewGuid().ToString("N"));
        var temp = Path.Combine(work, "index.idx");
        Directory.CreateDirectory(work);
        try
        {
            await store.ReadIndexToFileAsync(account, container, version.IndexBlob, password, version.IndexVolumes, temp, ct);
            await using var file = File.OpenRead(temp);
            using var reader = new IndexStreamReader(file);
            await catalog.ImportVersionAsync(version.Version, identityTicks, reader, ct);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* temp space; nothing further to do about a failed cleanup */ }
        }
    }

    /// <summary>Tries the version's cached <c>.idx</c> file. Returns false (leaving the file untouched) when there is
    /// no file, or its identity does not match — a file under a superseded identity is a plain miss, and clearing it
    /// away is <see cref="RemoveVersionAsync"/>'s and <see cref="RemoveContainerAsync"/>'s business, not this one's.
    /// A file whose header matches but whose body cannot be parsed (truncated by a prior crash mid-write, say) is
    /// different: the import throws out of a rolled-back transaction, so the catalog gains nothing from it, but the
    /// bad file itself would keep failing forever if left in place — so that case, and only that case, deletes it
    /// before falling through.</summary>
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
        using var held = await catalogs.LockForWriteAsync(accountId, container, ct);
        await using var catalog = await catalogs.OpenForWriteAsync(held, accountId, container, ct);
        await RemoveVersionCoreAsync(catalog, accountId, container, version, ct);
    }

    public async Task ReconcileAsync(
        int accountId, string container, IReadOnlyCollection<int> keepVersions, CancellationToken ct = default)
    {
        var keep = keepVersions as IReadOnlySet<int> ?? new HashSet<int>(keepVersions);

        using var held = await catalogs.LockForWriteAsync(accountId, container, ct);
        await using var catalog = await catalogs.OpenForWriteAsync(held, accountId, container, ct);

        // Listed first, then removed: RemoveVersionCoreAsync writes to the same connection the listing streams off,
        // and one handle cannot be doing both at once.
        var stale = (await catalog.ListVersionsAsync(ct)).Select(v => v.Version).Where(v => !keep.Contains(v)).ToList();
        foreach (var version in stale)
        {
            logger?.LogWarning(
                "Version {Version} of container {Container} is in the local catalog but not in the backup's info file; "
                + "dropping it. Its blobs may already have been deleted from the cloud by an interrupted cleanup.",
                version, container);
            await RemoveVersionCoreAsync(catalog, accountId, container, version, ct);
        }
    }

    /// <summary>The removal itself, on a catalog already open for writing under a held lock — the one place that
    /// knows a version leaves the catalog and both of its older homes together, shared by
    /// <see cref="RemoveVersionAsync"/> (which opens for one) and <see cref="ReconcileAsync"/> (which opens once for
    /// however many the info file has stopped listing).</summary>
    private async Task RemoveVersionCoreAsync(
        VersionCatalog catalog, int accountId, string container, int version, CancellationToken ct)
    {
        await catalog.RemoveVersionAsync(version, ct);
        legacyFiles.Remove(accountId, container, version);
        await DropLegacyRowAsync(accountId, container, version, ct);
    }

    public async Task ApplyPatchesOrInvalidateAsync(
        int accountId, string container, IReadOnlyList<CatalogPatch> patches, ILogger? log,
        CancellationToken ct = default)
    {
        if (patches.Count == 0)
            return;

        try
        {
            using var held = await catalogs.LockForWriteAsync(accountId, container, ct);
            await using var catalog = await catalogs.OpenForWriteAsync(held, accountId, container, ct);
            await catalog.ApplyPatchesAsync(patches, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var versions = patches.Select(p => p.Version).Distinct().Order().ToList();
            (log ?? logger)?.LogError(ex,
                "Could not record the check/repair marks for version(s) {Versions} of container {Container} in the local "
                + "catalog ({Message}). The rewritten index is already in the cloud, so those versions are dropped from "
                + "the catalog and will be re-imported from the cloud on first use.",
                string.Join(", ", versions), container, ex.Message);

            try
            {
                foreach (var version in versions)
                    await RemoveVersionAsync(accountId, container, version, ct);
            }
            catch (Exception removal) when (removal is not OperationCanceledException)
            {
                // Whatever stopped the patch from being written — a read-only file, a full disk, an I/O error — is
                // just as likely to stop the row from being deleted, and then the stale rows would survive exactly
                // as if nothing had been attempted. So the last resort is the bluntest one available and the one
                // that cannot fail for the same reason: drop the container's catalog file outright. It is a cache
                // of what the cloud holds, deleting it costs downloads and never data, and it is one directory
                // entry rather than a write into a file SQLite has just refused.
                (log ?? logger)?.LogError(removal,
                    "The affected versions of container {Container} could not be dropped from the local catalog either "
                    + "({Message}); deleting the container's catalog file so nothing reads the pre-repair rows again.",
                    container, removal.Message);
                await RemoveContainerAsync(accountId, container, ct);
            }

            // The marks are safe — they are in the cloud already, and the catalog no longer holds the rows that
            // disagreed with it — but the operation itself still failed, and a caller mid-repair or mid-check has
            // more work queued that assumes those rows are still there to patch or to read back. Swallowing this
            // used to let a repair run to "success" having repaired nothing past the first catalog write it could
            // not make (see BackupRepairer's single long-lived read handle): rethrow so the run stops exactly where
            // it always did before the catalog ever went local.
            throw;
        }
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

    /// <summary>Guarded by a read: once the migration off <c>CachedVersionIndexes</c> is behind a container, every
    /// call here takes the read branch, and a read needs no write lock — where an unconditional
    /// <c>ExecuteDelete</c> would open one every time.</summary>
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
