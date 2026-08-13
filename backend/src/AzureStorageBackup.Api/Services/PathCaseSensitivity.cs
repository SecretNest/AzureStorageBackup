namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Answers "does this directory live on a filesystem that folds case" by asking the filesystem itself, not by
/// guessing from the operating system.
/// <para>
/// The OS is the wrong thing to key on: Linux mounts exFAT/NTFS/CIFS shares that fold case, macOS ships a
/// case-insensitive APFS volume by default but can format a case-sensitive one, and Windows can turn folding off
/// per directory. A restore that decides on <c>OperatingSystem.IsLinux()</c> would be wrong in exactly the cases
/// this check exists for.
/// </para>
/// </summary>
public static class PathCaseSensitivity
{
    /// <summary>
    /// Writes a probe file with an all-lowercase name and asks whether its all-uppercase twin exists; on a folding
    /// filesystem the answer is yes. The probe is removed in a <c>finally</c> — it is never left behind, not even
    /// when the existence test throws.
    /// <para>
    /// A probe that cannot be written returns <c>true</c> (assume folding). The alternative — assuming a
    /// case-sensitive filesystem — would silently re-enable the very overwrite path this probe exists to block, and
    /// the failure it causes is invisible (one file's content quietly replaced by another's). Assuming folding costs
    /// at worst a visible, explained failure on a handful of case-colliding paths, and in practice the branch is
    /// close to unreachable: a directory we cannot write a probe into is one the restore cannot write files into
    /// either.
    /// </para>
    /// </summary>
    public static bool IsCaseInsensitive(string dir)
    {
        // Guid's "N" format is lowercase hex and the prefix is lowercase too, so ToUpperInvariant is guaranteed to
        // produce a different string — without that guarantee the test would compare a name against itself and
        // report every filesystem as folding.
        var name = ".asb-case-probe-" + Guid.NewGuid().ToString("N");
        var probe = Path.Combine(dir, name);
        try
        {
            using (File.Create(probe)) { }
            return File.Exists(Path.Combine(dir, name.ToUpperInvariant()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return true;
        }
        finally
        {
            try { File.Delete(probe); } catch { /* best effort */ }
        }
    }
}
