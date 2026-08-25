namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The sentinel: a per-backup precondition that answers "is the source really there?" before a run touches it.
/// <para>
/// It exists because an unmounted root is not an absent root. The mount point stays on disk with nothing under
/// it, so a scan finds an empty tree, the diff concludes that every file was deleted, and one round of that
/// records a version in which the whole backup has vanished. Nothing about it looks like a failure — which is
/// exactly what makes it dangerous. Pointing the sentinel at something that only exists **after** the mount
/// (a file inside it, or a subdirectory) turns "the mount is not there" into a fact the run can check before it
/// starts, instead of a conclusion the diff reaches after the fact.
/// </para>
/// <para>
/// With no sentinel configured, the local root stands in for one. A root that is not there cannot be backed up
/// under any circumstances, so it answers the same question, and this way the check costs nothing to opt into
/// and covers every config that predates the feature. It also means the backup path does not need a separate
/// "does the root exist" test — this is that test, with a better answer when a sentinel is configured.
/// </para>
/// <para>
/// One definition, shared by the backup gate (<see cref="BackupRunner"/>) and the local half of a check
/// (<see cref="BackupChecker"/>), because the day the two disagree is the day one of them backs up — or
/// verifies against — a tree the other has already judged missing.
/// </para>
/// </summary>
public static class SentinelGate
{
    /// <summary>
    /// The path that actually gets probed: the sentinel when one is configured, the local root otherwise, and
    /// null when there is nothing to probe at all. A blank sentinel is "none" — that is what an emptied text
    /// box sends, and it must not become a sentinel whose path is the empty string.
    /// </summary>
    private static string? Effective(string? sentinelPath, string? localRoot) =>
        string.IsNullOrWhiteSpace(sentinelPath)
            ? string.IsNullOrWhiteSpace(localRoot) ? null : localRoot.Trim()
            : sentinelPath.Trim();

    /// <summary>
    /// Whether this backup is allowed to look at its source right now. Existence only, deliberately: emptiness
    /// is not the question — a genuinely empty directory is a legitimate thing to back up and must not be
    /// mistaken for an unmounted one — and any deeper probe (reading it, listing it) is a new way to fail on a
    /// source that is perfectly fine.
    /// <para>
    /// **Nothing to probe means yes.** An imported backup can sit with no local root at all until the operator
    /// supplies one; that case has its own guards and must not be turned into a silent skip here.
    /// </para>
    /// </summary>
    public static bool Present(string? sentinelPath, string? localRoot) =>
        Effective(sentinelPath, localRoot) is not { } path
        || File.Exists(path)
        || Directory.Exists(path);

    /// <summary>
    /// The other half of <see cref="Present"/>, for the callers that need the path in a message: the path that
    /// was not there, or null when nothing is blocking. It reports **whichever of the two did the blocking**,
    /// because that is the one the operator has to go and look at — naming the other sends them to the wrong
    /// place. It also keeps every caller from re-deriving "configured, and absent" and getting the blank case
    /// wrong.
    /// </summary>
    public static string? Missing(string? sentinelPath, string? localRoot) =>
        Present(sentinelPath, localRoot) ? null : Effective(sentinelPath, localRoot);
}
