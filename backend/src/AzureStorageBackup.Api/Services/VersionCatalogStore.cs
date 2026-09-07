using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Maps a container's identity — <c>(accountId, container)</c> — to its <c>catalog.db</c> file, and owns the three
/// things a lone <see cref="VersionCatalog"/> cannot own about itself: that only one writer may hold a given
/// container's file at a time, that a file SQLite cannot read is not a fatal error but a cache miss, and that a
/// full-file <c>PRAGMA quick_check</c> scan (a catalog can run to gigabytes) is worth paying for once per path per
/// process rather than on every open.
/// <para>
/// The path layout mirrors <see cref="VersionIndexFileStore"/>'s — <c>{root}/{accountId}/{Safe(container)}/</c> —
/// so the catalog lands next to the index blobs it replaces the row-store half of, sharing the same volume and the
/// same "safe to lose, will re-download" contract with the deployment's backup story.
/// </para>
/// </summary>
public sealed class VersionCatalogStore(string rootDir, ILogger<VersionCatalogStore>? logger = null)
{
    /// <summary>One <see cref="SemaphoreSlim"/> per container path, created on first use and kept for the process's
    /// lifetime — cheap (a handful of bytes each), and simpler than tearing one down while a caller might still be
    /// waiting on it. Shared by <see cref="LockForWriteAsync"/>, <see cref="RemoveContainerAsync"/> and a write
    /// open's corrupt-file recovery — see their doc comments for why they must not be nested on the same caller.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>Paths this process has already run <see cref="VersionCatalog.QuickCheckAsync"/> against successfully.
    /// A corrupt-recovery removes its path so the freshly recreated file gets checked once too (trivially, since it
    /// is empty) rather than being trusted purely because the old, bad file at that path once passed.</summary>
    private readonly ConcurrentDictionary<string, byte> _checkedPaths = new();

    /// <summary>Mirrors <see cref="VersionIndexFileStore"/>'s character-safety rules verbatim: the two stores share a
    /// directory, so a container name that is unsafe for one filename must be made unsafe the same way for the
    /// other, or the same container would resolve to two different directories depending on which store asked.</summary>
    private static string Safe(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0 || chars[i] is '/' or '\\')
                chars[i] = '_';
        var flat = new string(chars);
        return flat.Length > 0 && flat.All(c => c == '.') ? new string('_', flat.Length) : flat;
    }

    private string DirFor(int accountId, string container) => Path.Combine(rootDir, accountId.ToString(), Safe(container));

    /// <summary>The container's catalog file: <c>{root}/{accountId}/{Safe(container)}/catalog.db</c>.</summary>
    public string PathFor(int accountId, string container) => Path.Combine(DirFor(accountId, container), "catalog.db");

    private SemaphoreSlim GateFor(string path) => _locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Opens the container's catalog. A read-only open never creates anything — a missing file is a
    /// <see cref="FileNotFoundException"/>, not an empty catalog conjured for a caller who only meant to look — and
    /// never runs <c>quick_check</c> (a reader that hits a bad page simply fails the query that touches it, and is
    /// never the one to delete and recreate the file).
    /// <para>
    /// A write open creates the directory, opens the file (creating it and its schema if needed, per
    /// <see cref="VersionCatalog.OpenAsync"/>), and — the first time this process opens this exact path — runs
    /// <see cref="VersionCatalog.QuickCheckAsync"/> once. If the open or the check finds the file unreadable
    /// (<c>SQLITE_NOTADB</c> from <see cref="CatalogSql.ApplyPragmas"/>'s own statements on a file that isn't a
    /// database at all, or <c>SQLITE_CORRUPT</c> from a bad page <c>quick_check</c> found), the file is not treated
    /// as a fatal error: it is a cache of what the cloud already holds, so it gets deleted and rebuilt empty. That
    /// deletion takes the container's write lock — the same one <see cref="LockForWriteAsync"/> hands out — so two
    /// callers racing to open the same corrupt file cannot both delete it out from under each other; whichever loses
    /// the race re-opens what the winner already fixed instead of deleting a second time. A caller that already
    /// holds that lock via <see cref="LockForWriteAsync"/> for this container must not call this method re-entrantly
    /// while holding it — <see cref="SemaphoreSlim"/> is not reentrant, and the corrupt-recovery branch awaiting the
    /// same lock again would deadlock forever. In practice this is not a real constraint: a corrupt file is caught
    /// on the first open of a container, before any lock has been taken for it.
    /// </para>
    /// </summary>
    public async Task<VersionCatalog> OpenAsync(int accountId, string container, bool readOnly, CancellationToken ct)
    {
        var path = PathFor(accountId, container);
        if (readOnly)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"No catalog for container '{container}' under account {accountId}.", path);
            return await VersionCatalog.OpenAsync(path, readOnly: true, ct);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            return await OpenCheckedAsync(path, ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 /* SQLITE_CORRUPT */ or 26 /* SQLITE_NOTADB */)
        {
            return await RecoverAsync(path, ct);
        }
    }

    /// <summary>Opens a write catalog and, only the first time this process opens this exact path, runs
    /// <see cref="VersionCatalog.QuickCheckAsync"/>. A failed check disposes the connection (so the file has no open
    /// handle by the time a caller deletes it) and forgets the path was ever checked, before rethrowing.</summary>
    private async Task<VersionCatalog> OpenCheckedAsync(string path, CancellationToken ct)
    {
        var catalog = await VersionCatalog.OpenAsync(path, readOnly: false, ct);
        if (_checkedPaths.TryAdd(path, 0))
        {
            try
            {
                await catalog.QuickCheckAsync(ct);
            }
            catch
            {
                _checkedPaths.TryRemove(path, out _);
                await catalog.DisposeAsync();
                throw;
            }
        }

        return catalog;
    }

    /// <summary>The corrupt-file path: takes the container's write lock, then re-tries the open in case another
    /// caller already fixed it while this one waited for the lock, and only deletes-and-recreates if it is still
    /// broken.</summary>
    private async Task<VersionCatalog> RecoverAsync(string path, CancellationToken ct)
    {
        var gate = GateFor(path);
        await gate.WaitAsync(ct);
        try
        {
            try
            {
                return await OpenCheckedAsync(path, ct);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 11 /* SQLITE_CORRUPT */ or 26 /* SQLITE_NOTADB */)
            {
                logger?.LogWarning(ex,
                    "Catalog {Path} is unreadable; deleting it. It is a cache and will be rebuilt from the cloud on demand.", path);
                DeleteContainerFiles(path);
                _checkedPaths.TryRemove(path, out _);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                return await OpenCheckedAsync(path, ct);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reserves the container's single write slot. The catalog's own writes (import, patch) go through one
    /// connection and are not safe to interleave from two callers, so whoever imports a version or applies repair
    /// patches holds this for the duration, the same way the old code's in-process lock around a container's index
    /// worked — except this one also protects two hosts sharing the same volume, since it is the SQLite file's own
    /// single-writer contract that ultimately enforces it; the semaphore just fails fast in-process instead of
    /// blocking on <c>busy_timeout</c>.
    /// <para>
    /// This is the same lock <see cref="OpenAsync"/>'s corrupt-recovery and <see cref="RemoveContainerAsync"/> take
    /// internally. Holding it while calling either of those for the same container deadlocks — the semaphore is not
    /// reentrant.
    /// </para>
    /// </summary>
    public async Task<IDisposable> LockForWriteAsync(int accountId, string container, CancellationToken ct)
    {
        var gate = GateFor(PathFor(accountId, container));
        await gate.WaitAsync(ct);
        return new Release(gate);
    }

    /// <summary>
    /// Deletes a container's catalog file and its WAL siblings, and the directory too if nothing else is left in it.
    /// Takes the container's write lock first — the same one <see cref="LockForWriteAsync"/> hands out — because
    /// deleting the file out from under a live writer would corrupt whatever it is mid-write on.
    /// <para>
    /// A caller that already holds that lock (from a prior, still-open <see cref="LockForWriteAsync"/> on this same
    /// container) must NOT call this method: <see cref="SemaphoreSlim"/> is not reentrant, so awaiting the lock again
    /// here would deadlock forever against the caller's own held lock. Release the write lock first.
    /// </para>
    /// </summary>
    public async Task RemoveContainerAsync(int accountId, string container, CancellationToken ct)
    {
        var path = PathFor(accountId, container);
        var gate = GateFor(path);
        await gate.WaitAsync(ct);
        try
        {
            DeleteContainerFiles(path);
            _checkedPaths.TryRemove(path, out _);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void DeleteContainerFiles(string path)
    {
        DeleteIfExists(path);
        DeleteIfExists(path + "-wal");
        DeleteIfExists(path + "-shm");

        var dir = Path.GetDirectoryName(path)!;
        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            Directory.Delete(dir);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Releases the container's write semaphore exactly once, however many times <see cref="Dispose"/> is
    /// called — the pattern every <c>using</c> over a lock handle relies on.</summary>
    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate.Release();
        }
    }
}
