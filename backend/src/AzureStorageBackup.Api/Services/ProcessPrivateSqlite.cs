namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Builds the <c>Data Source</c> for the SQLite files only this process ever opens: the version catalogs and a run's
/// work database. On Unix they are opened through SQLite's <c>unix-excl</c> VFS, which changes one thing: the WAL
/// index lives in this process's heap instead of a memory-mapped <c>-shm</c> file, and no byte-range locks are ever
/// taken on such a file. Connections inside the process still share the index and still run concurrently — a reader
/// never waits for the writer — but a second process cannot open the file while this one has it open, which is
/// exactly the situation these files are in anyway.
/// <para>
/// Why: on a QNAP NAS (QuTS hero, kernel 6.6.32-qnap, ZFS) the first 2026.9.8 run failed every time with
/// <c>SQLite Error 15: 'locking protocol'</c> while committing a catalog import. The writer held the WAL write lock
/// and spent ten seconds asking the kernel for an exclusive lock on the WAL read-slot bytes of <c>catalog.db-shm</c>;
/// <c>/proc/locks</c> showed nobody holding them, and the same library, directory and statement sequence succeeded
/// from a python process in the same container. Nothing on our side of the <c>fcntl</c> call explains it, the NAS is
/// offline so the failing call's errno could not be captured, and the one thing every observation agrees on is that
/// the failure lives in byte-range locks on the <c>-shm</c> file. So those files are not used any more.
/// </para>
/// <para>
/// The consequence to remember: a connection opened <c>ReadOnly</c> at the SQLite level (<c>SQLITE_OPEN_READONLY</c>)
/// is excluded from <c>unix-excl</c> and would open the <c>-shm</c> file after all — and drag every other connection
/// on the same file back onto it, since the shared-memory node is per file. Every catalog connection is therefore
/// opened <c>ReadWrite</c>, including the ones that only read; the "read-only" in their names is a promise the code
/// keeps, not a mode SQLite enforces.
/// </para>
/// </summary>
public static class ProcessPrivateSqlite
{
    /// <summary>The VFS name; <c>null</c> where it does not exist (Windows), in which case the plain path is used.</summary>
    public static string? Vfs => OperatingSystem.IsWindows() ? null : "unix-excl";

    /// <summary>
    /// The <c>Data Source</c> value for <paramref name="path"/>: a <c>file:</c> URI carrying <c>vfs=unix-excl</c>,
    /// or the path itself where the VFS is unavailable. Microsoft.Data.Sqlite passes <c>SQLITE_OPEN_URI</c> for any
    /// data source that starts with <c>file:</c>, and SQLite decodes <c>%HH</c> escapes in the path part, so the
    /// three characters that would otherwise be read as URI syntax are escaped and everything else is left alone.
    /// </summary>
    public static string DataSource(string path)
    {
        if (Vfs is null)
            return path;

        var escaped = path.Replace("%", "%25").Replace("?", "%3F").Replace("#", "%23");
        return $"file:{escaped}?vfs={Vfs}";
    }
}
