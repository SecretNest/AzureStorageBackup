namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Hands out one <see cref="RunWorkDb"/> per backup run, under a directory the process owns.
/// <para>
/// A run's scratch database is named after its runId, so two runs never share a file, and
/// <see cref="RunWorkDb.DisposeAsync"/> removes it at the end. What that leaves is the killed-process case, which
/// <see cref="ClearStale"/> answers the same way <c>DiffWorkQueue.ClearStale</c> does — at <b>process startup only</b>,
/// never per run: several backups can be in flight at once, and clearing per run would delete a file somebody else is
/// still writing to.
/// </para>
/// </summary>
public sealed class RunWorkDbFactory(string rootDir)
{
    public string RootDir => rootDir;

    /// <summary>
    /// Creates <c>{root}/{runId}.db</c>, fresh. Anything already at that name is deleted first rather than reused:
    /// a leftover from a run that shared this id would seed the new run with somebody else's scan rows, and the
    /// whole point of this file is that it describes exactly one run.
    /// </summary>
    public Task<RunWorkDb> CreateAsync(string runId, CancellationToken ct)
    {
        Directory.CreateDirectory(rootDir);
        var path = Path.Combine(rootDir, runId + ".db");
        RunWorkDb.Delete(path);
        RunWorkDb.Delete(path + "-wal");
        RunWorkDb.Delete(path + "-shm");
        return RunWorkDb.CreateAsync(path, ct);
    }

    /// <summary>Clears the scratch files a previous abnormal exit left behind. The pattern covers the <c>-wal</c> and
    /// <c>-shm</c> companions as well, because a WAL left next to a deleted database is not just wasted disk — it is
    /// what SQLite would try to replay into the next file created under that name. The serialized indexes a run
    /// writes beside its database (<c>{runId}.v{n}.idx</c>) go the same way: both are named for a run that is over,
    /// and an index at a few million entries is hundreds of MB to leave lying about.</summary>
    public static void ClearStale(string rootDir)
    {
        try
        {
            Directory.CreateDirectory(rootDir);
            foreach (var pattern in new[] { "*.db*", "*.idx" })
                foreach (var file in Directory.EnumerateFiles(rootDir, pattern))
                    RunWorkDb.Delete(file);
        }
        catch
        {
            // A bit of wasted disk does not affect correctness; blocking startup over it would be the real problem.
        }
    }
}
