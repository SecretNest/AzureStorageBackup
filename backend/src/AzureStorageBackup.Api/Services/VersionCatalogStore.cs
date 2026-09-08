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
    /// waiting on it. Shared by <see cref="LockForWriteAsync"/> and <see cref="RemoveContainerAsync"/> — see their
    /// doc comments for why they must not be nested on the same caller. A write open's corrupt-file recovery does
    /// <b>not</b> take it: <see cref="OpenForWriteAsync"/> only runs with the caller's own handle in hand.</summary>
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
    /// Opens the container's catalog <b>read-only</b>. Never creates anything — a missing file is a
    /// <see cref="FileNotFoundException"/>, not an empty catalog conjured for a caller who only meant to look — and
    /// never runs <c>quick_check</c> (a reader that hits a bad page simply fails the query that touches it, and is
    /// never the one to delete and recreate the file).
    /// <para>
    /// The <paramref name="readOnly"/> flag is kept so that every reader's call reads as what it is at the call
    /// site, but only <c>true</c> is accepted: a write open needs the container's write lock in hand, and that is
    /// <see cref="OpenForWriteAsync"/>, which takes the handle as an argument so the requirement is checked by the
    /// compiler rather than by a comment.
    /// </para>
    /// </summary>
    public async Task<VersionCatalog> OpenAsync(int accountId, string container, bool readOnly, CancellationToken ct)
    {
        if (!readOnly)
            throw new ArgumentException(
                $"A write open must go through {nameof(OpenForWriteAsync)} with the container's write lock held.",
                nameof(readOnly));

        var path = PathFor(accountId, container);
        if (!File.Exists(path))
            throw new FileNotFoundException($"No catalog for container '{container}' under account {accountId}.", path);
        return await VersionCatalog.OpenAsync(path, readOnly: true, ct);
    }

    /// <summary>
    /// Opens the container's catalog for writing, which is only legal while holding that container's write lock —
    /// hence <paramref name="held"/>: the handle <see cref="LockForWriteAsync"/> returned, passed in as proof, and
    /// checked to be a handle for <em>this</em> container rather than some other one the caller happened to hold.
    /// <para>
    /// It creates the directory, opens the file (creating it and its schema if needed, per
    /// <see cref="VersionCatalog.OpenAsync"/>), and — the first time this process opens this exact path — runs
    /// <see cref="VersionCatalog.QuickCheckAsync"/> once. If the open or the check finds the file unreadable
    /// (<c>SQLITE_NOTADB</c> from <see cref="CatalogSql.ApplyPragmas"/>'s own statements on a file that isn't a
    /// database at all, or <c>SQLITE_CORRUPT</c> from a bad page <c>quick_check</c> found), the file is not treated
    /// as a fatal error: it is a cache of what the cloud already holds, so it gets deleted and rebuilt empty.
    /// </para>
    /// <para>
    /// That recovery does <b>not</b> take the write lock — the caller already holds it, and
    /// <see cref="SemaphoreSlim"/> is not reentrant, so taking it again is a deadlock, not a safety measure. It does
    /// not need to: the lock the caller holds is exactly what keeps a second writer from deleting the same file at
    /// the same moment, which is all the locking was ever there for.
    /// </para>
    /// </summary>
    public async Task<VersionCatalog> OpenForWriteAsync(
        CatalogWriteLock held, int accountId, string container, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(held);
        if (held.IsReleased)
            throw new ObjectDisposedException(
                nameof(CatalogWriteLock), "The container's write lock was released before the catalog was opened for writing.");
        var path = PathFor(accountId, container);
        if (!string.Equals(held.Path, path, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"The write lock held is for '{held.Path}', not for container '{container}' under account {accountId} ('{path}').");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            return await OpenCheckedAsync(path, ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 /* SQLITE_CORRUPT */ or 26 /* SQLITE_NOTADB */)
        {
            return await RecoverAsync(path, ex, ct);
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

    /// <summary>The corrupt-file path: delete the file and its WAL siblings and open a fresh, empty catalog in its
    /// place. No lock is taken here — the only caller is <see cref="OpenForWriteAsync"/>, which has already proved
    /// the container's write lock is held, and that lock is what keeps a second writer from doing the same thing to
    /// the same file at the same moment.</summary>
    private async Task<VersionCatalog> RecoverAsync(string path, SqliteException cause, CancellationToken ct)
    {
        logger?.LogWarning(cause,
            "Catalog {Path} is unreadable; deleting it. It is a cache and will be rebuilt from the cloud on demand.", path);
        DeleteContainerFiles(path);
        _checkedPaths.TryRemove(path, out _);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return await OpenCheckedAsync(path, ct);
    }

    /// <summary>Whether the next write open of this container's catalog will run the full-file
    /// <see cref="VersionCatalog.QuickCheckAsync"/> — true until the first write open in this process, and again after
    /// <see cref="ForgetChecked"/>. The run asks this **before** it opens, so the check can be shown on a stage line of
    /// its own instead of running unannounced inside whatever open happens to come first.</summary>
    public bool NeedsCheck(int accountId, string container) => !_checkedPaths.ContainsKey(PathFor(accountId, container));

    /// <summary>Runs the check now, under the container's write lock, if it is still due — the write open that follows
    /// finds the path checked. A catalog nobody wrote yet is created (and trivially passes), as any write open would.
    /// Cancellation interrupts the statement and leaves the path unchecked, so the next open pays for it instead.</summary>
    public async Task EnsureCheckedAsync(int accountId, string container, CancellationToken ct)
    {
        using var held = await LockForWriteAsync(accountId, container, ct);
        await using var catalog = await OpenForWriteAsync(held, accountId, container, ct);
    }

    /// <summary>The bytes <see cref="VersionCatalog.QuickCheckAsync"/> is about to read — the main file plus its WAL,
    /// which the check reads through. 0 for a catalog nobody wrote yet.</summary>
    public long CatalogBytes(int accountId, string container)
    {
        var path = PathFor(accountId, container);
        long Size(string p) { try { return File.Exists(p) ? new FileInfo(p).Length : 0; } catch { return 0; } }
        return Size(path) + Size(path + "-wal");
    }

    /// <summary>Forgets that this path passed <see cref="VersionCatalog.QuickCheckAsync"/>, so the next write open
    /// runs it again. For when something other than that check found the damage: <see cref="VersionCatalogs"/>'s
    /// read-only probe hits a <c>SQLITE_CORRUPT</c>/<c>SQLITE_NOTADB</c> row well after this process last opened the
    /// path for writing, and without this the locked write open that follows would trust the stale "already
    /// checked" marker, skip the scan, and hand back a catalog whose first real query throws the same error
    /// uncaught instead of the write path recovering it.</summary>
    internal void ForgetChecked(int accountId, string container) =>
        _checkedPaths.TryRemove(PathFor(accountId, container), out _);

    /// <summary>
    /// Reserves the container's single write slot. The catalog's own writes (import, patch) go through one
    /// connection and are not safe to interleave from two callers, so whoever imports a version or applies repair
    /// patches holds this for the duration, the same way the old code's in-process lock around a container's index
    /// worked — except this one also protects two hosts sharing the same volume, since it is the SQLite file's own
    /// single-writer contract that ultimately enforces it; the semaphore just fails fast in-process instead of
    /// blocking on <c>busy_timeout</c>.
    /// <para>
    /// This is the same lock <see cref="RemoveContainerAsync"/> takes internally. Holding it while calling that for
    /// the same container deadlocks — the semaphore is not reentrant. <see cref="OpenForWriteAsync"/>, by contrast,
    /// is meant to be called with the handle this returns and takes no lock of its own.
    /// </para>
    /// </summary>
    public async Task<CatalogWriteLock> LockForWriteAsync(int accountId, string container, CancellationToken ct)
    {
        var path = PathFor(accountId, container);
        var gate = GateFor(path);
        await gate.WaitAsync(ct);
        return new CatalogWriteLock(gate, path);
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

}

/// <summary>
/// A held write slot for one container's catalog, and the token <see cref="VersionCatalogStore.OpenForWriteAsync"/>
/// demands: passing it is what turns "the caller must hold the lock" from a doc comment into something the compiler
/// checks, and carrying the path it was taken for is what lets the open reject a handle for a different container.
/// Releases the semaphore exactly once however many times <see cref="Dispose"/> is called — the pattern every
/// <c>using</c> over a lock handle relies on.
/// </summary>
public sealed class CatalogWriteLock : IDisposable
{
    private readonly SemaphoreSlim _gate;
    private int _released;

    internal CatalogWriteLock(SemaphoreSlim gate, string path)
    {
        _gate = gate;
        Path = path;
    }

    /// <summary>The catalog file this slot was reserved for.</summary>
    internal string Path { get; }

    /// <summary>True once <see cref="Dispose"/> has run. A handle proves nothing about who holds the semaphore once
    /// it is released — <see cref="VersionCatalogStore.OpenForWriteAsync"/> checks this before trusting one, so a
    /// `using` block that ended before the open call cannot be mistaken for the lock still being held.</summary>
    internal bool IsReleased => Volatile.Read(ref _released) != 0;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            _gate.Release();
    }
}
