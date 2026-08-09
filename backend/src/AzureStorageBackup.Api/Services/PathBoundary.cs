namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Local path boundary (design §3). The root comes from <c>Backup:Root</c> and is **an admission filter only**:
/// it never rewrites or truncates a path, and never serves as the base for relative paths. Unconfigured = no boundary, everything passes.
/// Singleton: the real root is resolved once in the constructor and never changes afterwards.
/// </summary>
public sealed class PathBoundary
{
    /// <summary>
    /// Cap on the number of symlink **expansions** (matching Linux's 40). It counts expansions only, not ordinary path segments,
    /// otherwise a deep directory with no symlinks at all would be misjudged. Exceeding the cap counts as out of bounds, rather than throwing or looping forever.
    /// </summary>
    private const int MaxLinkDepth = 40;

    private readonly string? _configuredRoot;
    private readonly string? _realRoot;

    public PathBoundary(IConfiguration config)
    {
        var configured = config["Backup:Root"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            // Unconfigured = no boundary, everything passes (the existing convention).
            _configuredRoot = null;
            _realRoot = null;
            return;
        }

        // The root itself may be a symlink: it must be resolved to a real path first, otherwise every later comparison
        // is based on a fake address and would wrongly reject all legitimate paths.
        // A root that is configured but cannot be resolved (a cycle) must **blow up at startup**: keeping null,
        // Enabled would become false, the boundary would silently vanish and everything would pass — a misconfiguration
        // disguised as "not configured" is the worst possible outcome here.
        // Note: "a component is unreadable" is not a cause here — .NET's Unix ReadLink swallows EACCES, so a
        // chmod 000 directory still returns null from LinkTarget, is treated as an ordinary segment, and does not fail resolution.
        // A relative root is joined into an absolute path once, right here; from then on ConfiguredRoot is always absolute.
        // The reason is that ConfiguredRoot is not just for humans to read: the browse endpoint uses it as the display prefix (ToDisplayPath)
        // sent to the frontend, and the frontend hands that same string straight back as `?path=` / `localRoot`, while IsInside
        // **only accepts absolute input**. Keep the relative form and every hop of that round trip gets rejected by our own boundary with 409 —
        // clicking into a subdirectory, clicking "up one level", clicking "Use this folder" all stop working (every piece looks correct
        // on its own; only the combination breaks). Normalizing at the single entry point is more reliable than patching every downstream site.
        // The join is done exactly the way ResolveReal handles relative input: join the process's current working directory only,
        // and **never fold `..`** (see the ResolveReal docs for why), so both sides resolve to the same real location.
        // This step is **not** symlink resolution — joining the CWD follows no link, so ConfiguredRoot still neither equals
        // nor leaks RealRoot (when the root itself is a symlink the two still differ).
        _configuredRoot = Path.IsPathRooted(configured)
            ? configured
            : Path.Join(Directory.GetCurrentDirectory(), configured);
        _realRoot = ResolveReal(_configuredRoot)
            ?? throw new InvalidOperationException(
                $"Backup root '{_configuredRoot}' could not be resolved to a real path " +
                "(symlink cycle). Fix Backup__Root or the filesystem.");
    }

    /// <summary>Whether the boundary is enabled. False when no root is configured, and everything passes.</summary>
    public bool Enabled => _realRoot is not null;

    /// <summary>
    /// The path the operator configured in <c>Backup:Root</c>, **without symlink resolution** (a relative setting gets the
    /// process's current working directory joined onto it at construction; otherwise it is kept verbatim).
    /// Everything operator-facing — rejection messages, out-of-bounds warnings, the display path in directory browse responses —
    /// uses this one: a rejection should name the path the operator typed, not the place it really points
    /// to inside the host (configured as <c>/nas</c> but actually pointing at <c>/mnt/disk1</c>, say; the operator
    /// most likely would not recognize the latter). Null when the boundary is not enabled.
    /// <para>When enabled it is **always an absolute path** (see the constructor), so it is guaranteed to pass
    /// <see cref="IsInside"/> itself and can safely be handed to the frontend and sent back.</para>
    /// </summary>
    public string? ConfiguredRoot => _configuredRoot;

    /// <summary>
    /// The resolved real root (symlinks expanded). **For path comparison only**, never shown to the operator —
    /// building an error message out of it only shows them a path they never typed. Null when the boundary is not enabled.
    /// </summary>
    public string? RealRoot => _realRoot;

    /// <summary>
    /// Whether a path lies inside the boundary. Always true when the boundary is not enabled.
    /// <para>
    /// **Absolute paths only**: relative paths are always rejected, rather than accepted with a doc comment
    /// reminding the caller to mind the base the way <see cref="IsWithin"/> does. The reason is that this method is the shared
    /// entry point for the endpoints, the scheduler and the directory browse API; its callers are many and scattered.
    /// If relative paths were let through, the underlying <see cref="ResolveReal"/> would make them absolute against the **process's
    /// current working directory** before judging, and the moment the caller's actual file operations used a different base
    /// (some explicitly specified directory, say), the verdict and the place the bytes actually land would disagree, with nothing
    /// reporting it. Rather than writing that risk into the docs and hoping every caller reads it, we shut this class of
    /// input out at the entrance — the root here **only filters for safety** and is never a base for relative paths, and rejecting
    /// relative input is the most direct expression of that principle.
    /// </para>
    /// </summary>
    public bool IsInside(string path)
    {
        if (_realRoot is null)
            return true;
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (!Path.IsPathRooted(path))
            return false;

        var real = ResolveReal(path);
        return real is not null && IsWithin(_realRoot, real);
    }

    /// <summary>
    /// Translates a real path already confirmed to be inside the boundary (prefixed by <see cref="RealRoot"/>) into the form
    /// the operator sees: replace the <see cref="RealRoot"/> prefix with <see cref="ConfiguredRoot"/>.
    /// The caller must first confirm with <see cref="IsInside"/> that the real path really does lie inside the boundary — this method does
    /// no validation, and passing an out-of-bounds real path in throws <see cref="InvalidOperationException"/> instead of quietly
    /// returning a string that still carries the <see cref="RealRoot"/> prefix (once that string reaches a response it is a
    /// leak of the host's real path). Returned unchanged when the boundary is not enabled.
    /// <para>
    /// Use case: things like "parent directory" that must be computed from the real path (following symlinks) yet shown to the
    /// user — we cannot show <see cref="RealRoot"/> directly (configured as <c>/nas</c> but actually pointing at
    /// <c>/mnt/disk1</c>, the operator does not recognize the latter), but we also cannot take the shortcut of computing the parent
    /// by lexical folding (that hole in the <see cref="ResolveReal"/> docs where following a symlink gets the answer wrong).
    /// </para>
    /// </summary>
    public string ToDisplayPath(string real)
    {
        if (_realRoot is null || _configuredRoot is null)
            return real;

        if (string.Equals(real, _realRoot, StringComparison.Ordinal))
            return _configuredRoot;

        if (real.StartsWith(_realRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return _configuredRoot + real[_realRoot.Length..];

        // B4: this should be unreachable — the caller's contract is to have confirmed with IsInside first. Silently returning the
        // input hands the caller back a string with no signal that it carries the RealRoot prefix, and they unknowingly keep passing
        // the host's real path (/mnt/disk1, say, rather than the /nas the operator knows) further down, worst case into an HTTP response.
        // Blowing up here is far better than silently leaking host paths in production.
        throw new InvalidOperationException(
            "PathBoundary.ToDisplayPath received a real path outside RealRoot; " +
            $"the caller must verify IsInside(real) before calling this method. real='{real}'.");
    }

    /// <summary>
    /// A real realpath: walk segment by segment from the filesystem root to arrive at the fully resolved real path.
    /// Returns null when symlink expansions exceed <see cref="MaxLinkDepth"/> (a cycle).
    /// <para>
    /// Algorithm: keep a stack of **pending segments** and a **fully resolved** prefix <c>resolved</c>, popping one segment at a time:
    /// <list type="bullet">
    /// <item><c>.</c> is skipped;</item>
    /// <item><c>..</c> chops the last segment off <c>resolved</c> (never past the filesystem root);</item>
    /// <item>everything else is first joined onto <c>resolved</c>, then that new path is checked for being a symlink — if it is, **the
    /// target's segments are pushed back onto the pending stack** (and when the target is absolute, <c>resolved</c> is reset to the filesystem root
    /// at the same time), so the target itself is re-walked segment by segment too.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Two crucial properties; missing either one is an escapable hole:
    /// </para>
    /// <para>
    /// 1) **Never pre-fold <c>..</c> with <c>Path.GetFullPath</c>**. GetFullPath folds **lexically**,
    /// while POSIX settles <c>..</c> **after** symlink expansion; the two disagree only when following a symlink —
    /// and that is exactly the attack surface: <c>&lt;root&gt;/escape/../secret</c> folds lexically to <c>&lt;root&gt;/secret</c> (inside),
    /// while it actually lands in the parent directory of <c>escape</c>'s target (outside). Here <c>..</c> acts on the already-resolved prefix,
    /// in the same order as the kernel. Relative **input** still has to become absolute, but by joining only, never folding.
    /// </para>
    /// <para>
    /// 2) **A symlink target must be re-walked, not substituted wholesale**. If <c>resolved</c> were simply replaced with the target's unresolved string,
    /// the components before the target's last segment would never be checked: with <c>b -&gt; &lt;outside&gt;</c> and <c>a -&gt; &lt;root&gt;/b/c</c>,
    /// <c>a</c> would be judged as <c>&lt;root&gt;/b/c</c> (inside) when it really is <c>&lt;outside&gt;/c</c>.
    /// </para>
    /// <para>
    /// <c>Directory.ResolveLinkTarget(p, returnFinalTarget: true)</c> cannot stand in for this: it only expands
    /// **the last segment**. If <c>/nas/link</c> is a symlink pointing at <c>/etc</c>, querying <c>/nas/link/passwd</c>
    /// returns null (passwd itself is not a link), and the escape in the middle segment is missed.
    /// </para>
    /// <para>
    /// A segment that does not exist is not a link and is simply joined on — which naturally gives "judge by the nearest existing ancestor",
    /// letting a restore target that has not been created yet pass.
    /// </para>
    /// </summary>
    public static string? ResolveReal(string path)
    {
        // Path/FileInfo throws ArgumentException on \0; treat that as unresolvable here
        // so the caller (IsInside) gets a clean rejection instead of an unhandled exception.
        if (string.IsNullOrEmpty(path) || path.Contains('\0'))
            return null;

        // Relative input becomes absolute against the current working directory — joining only, no `..` folding whatsoever.
        var absolute = Path.IsPathRooted(path)
            ? path
            : Path.Join(Directory.GetCurrentDirectory(), path);

        var fsRoot = Path.GetPathRoot(absolute);
        if (string.IsNullOrEmpty(fsRoot))
            return null;

        var pending = new Stack<string>();
        PushSegments(pending, absolute[fsRoot.Length..]);

        var resolved = fsRoot;
        var expansions = 0;

        while (pending.Count > 0)
        {
            var segment = pending.Pop();

            if (segment is ".")
                continue;

            if (segment is "..")
            {
                // resolved is fully resolved at this point, so this step matches the kernel's evaluation order.
                var parent = Path.GetDirectoryName(resolved);
                resolved = string.IsNullOrEmpty(parent) ? fsRoot : parent;
                continue;
            }

            var candidate = Path.Combine(resolved, segment);

            // FileSystemInfo.LinkTarget is lstat underneath and does not care whether the target is a file or a directory;
            // it returns null when the path does not exist, which is exactly what we want (a nonexistent segment is not a link).
            var target = new FileInfo(candidate).LinkTarget;
            if (target is null)
            {
                resolved = candidate;
                continue;
            }

            if (++expansions > MaxLinkDepth)
                return null;

            if (Path.IsPathFullyQualified(target))
            {
                // IsPathFullyQualified rather than IsPathRooted: on Windows
                // "C:foo" satisfies IsPathRooted but not IsPathFullyQualified —
                // it is a "drive-relative" path whose real anchor is that drive's current directory at the time,
                // and a boundary component has no business guessing that for the caller. On POSIX the two are always
                // equivalent, so this branch's semantics do not change on the Linux/Docker deployment target.
                var targetRoot = Path.GetPathRoot(target);
                if (string.IsNullOrEmpty(targetRoot))
                    return null;
                PushSegments(pending, target[targetRoot.Length..]);
                resolved = targetRoot; // Absolute target: start over from the filesystem root
            }
            else if (Path.IsPathRooted(target))
            {
                // Rooted but not fully qualified (Windows' "C:foo", "\foo"): the target's anchor
                // depends on the process's current drive / current working directory and is not a definite
                // directory. Better to declare resolution failed (IsInside rejects it as out of bounds) than to
                // guess an anchor and keep walking — guessing wrong is a hole that silently lets things through. Reachable on Windows only.
                return null;
            }
            else
            {
                // A relative target is based on the directory holding the link itself, i.e. the current resolved; leave it alone.
                PushSegments(pending, target);
            }
        }

        return resolved;
    }

    /// <summary>Splits a path into segments and pushes them in reverse, so that popping yields left-to-right order.</summary>
    private static void PushSegments(Stack<string> pending, string path)
    {
        var parts = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
            pending.Push(parts[i]);
    }

    /// <summary>
    /// Purely lexical containment check: normalize, then compare on **path segment boundaries**, without resolving symlinks.
    /// <c>/target</c> does not contain <c>/targetx</c>. Restore writes use it to guard against <c>..</c> in index data.
    /// <para>
    /// **Note on relative paths**: this method is purely lexical and never touches the filesystem (so the symlink problem in
    /// <see cref="ResolveReal"/> does not apply to it), but if <c>root</c>/<c>candidate</c> are **relative**, they get
    /// normalized by <c>Path.GetFullPath</c> against the **process's current working directory** — almost certainly not the base the caller intended.
    /// Restore writes must **first join the index's relative path onto the target root** before calling this method, e.g.
    /// <c>IsWithin(targetRoot, Path.Combine(targetRoot, entryPath))</c>;
    /// do not pass <c>entryPath</c> in directly.
    /// </para>
    /// <para>
    /// **Never throws**: <c>root</c>/<c>candidate</c> may come from a cloud index — malicious or corrupted data.
    /// <c>Path.GetFullPath</c> throws <see cref="ArgumentException"/> on paths containing <c>\0</c> and on the empty string;
    /// all of those are turned into "cannot normalize = judged out of bounds" returning <c>false</c>, because one dirty index record
    /// must not become an unhandled exception (500).
    /// </para>
    /// </summary>
    public static bool IsWithin(string root, string candidate)
    {
        if (!TryGetFullPath(root, out var fullRoot) || !TryGetFullPath(candidate, out var full))
            return false;

        if (string.Equals(full, fullRoot, StringComparison.Ordinal))
            return true;

        return full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// A non-throwing version of <c>Path.GetFullPath</c>: \0, the empty string, and any other
    /// <see cref="ArgumentException"/> the underlying call may throw are all treated as "cannot normalize" and return false.
    /// </summary>
    private static bool TryGetFullPath(string path, out string fullPath)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\0'))
        {
            fullPath = "";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            return true;
        }
        catch (ArgumentException)
        {
            fullPath = "";
            return false;
        }
    }
}
