using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Maps a container's identity — <c>(accountId, container)</c> — to its <c>catalog.db</c> file, and owns the two
/// things a lone <see cref="VersionCatalog"/> cannot own about itself: that only one writer may hold a given
/// container's file at a time, and that a file SQLite cannot read is not a fatal error but a cache miss.
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
    /// waiting on it.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

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

    /// <summary>
    /// Opens the container's catalog. A read-only open never creates anything — a missing file is a
    /// <see cref="FileNotFoundException"/>, not an empty catalog conjured for a caller who only meant to look — while
    /// a write open creates the directory and, per <see cref="VersionCatalog.OpenAsync"/>, the file and schema too.
    /// <para>
    /// A write open that finds the file unreadable (garbage bytes, a corrupt page — <c>SQLITE_NOTADB</c> or
    /// <c>SQLITE_CORRUPT</c>) does not fail: the catalog is a cache of what the cloud already holds, so the file is
    /// deleted and rebuilt empty, and the caller gets a catalog with nothing imported yet rather than an exception.
    /// The delete is safe because <see cref="VersionCatalog.OpenAsync"/> disposes its connection before rethrowing,
    /// so no handle is still open on the file by the time <see cref="RemoveContainer"/> runs.
    /// </para>
    /// </summary>
    public async Task<VersionCatalog> OpenAsync(int accountId, string container, bool readOnly, CancellationToken ct)
    {
        var path = PathFor(accountId, container);
        if (readOnly)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"No catalog for container '{container}' under account {accountId}.", path);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }

        try
        {
            return await VersionCatalog.OpenAsync(path, readOnly, ct);
        }
        catch (SqliteException ex) when (!readOnly && ex.SqliteErrorCode is 11 /* SQLITE_CORRUPT */ or 26 /* SQLITE_NOTADB */)
        {
            logger?.LogWarning(ex,
                "Catalog {Path} is unreadable; deleting it. It is a cache and will be rebuilt from the cloud on demand.", path);
            RemoveContainer(accountId, container);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return await VersionCatalog.OpenAsync(path, readOnly: false, ct);
        }
    }

    /// <summary>
    /// Reserves the container's single write slot. The catalog's own writes (import, patch) go through one
    /// connection and are not safe to interleave from two callers, so whoever imports a version or applies repair
    /// patches holds this for the duration, the same way the old code's in-process lock around a container's index
    /// worked — except this one also protects two hosts sharing the same volume, since it is the SQLite file's own
    /// single-writer contract that ultimately enforces it; the semaphore just fails fast in-process instead of
    /// blocking on <c>busy_timeout</c>.
    /// </summary>
    public async Task<IDisposable> LockForWriteAsync(int accountId, string container, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(PathFor(accountId, container), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Release(gate);
    }

    /// <summary>Deletes a container's catalog file and its WAL siblings, and the directory too if nothing else is
    /// left in it — called both when a container is removed from the backup and when <see cref="OpenAsync"/> finds
    /// the file unreadable and needs to start over.</summary>
    public void RemoveContainer(int accountId, string container)
    {
        var path = PathFor(accountId, container);
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
