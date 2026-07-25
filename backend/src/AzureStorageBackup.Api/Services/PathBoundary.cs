namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 本地路径边界（设计 §3）。根来自 <c>Backup:Root</c>，**只做准入过滤**：
/// 不改写、不截断路径，也不作为相对路径基准。未配置时无边界，全部放行。
/// 单例：构造时解析一次真实根，之后不再变。
/// </summary>
public sealed class PathBoundary
{
    /// <summary>符号链接展开深度上限。超限判定为越界，而不是抛异常或死循环。</summary>
    private const int MaxLinkDepth = 40;

    private readonly string? _realRoot;

    public PathBoundary(IConfiguration config)
    {
        var configured = config["Backup:Root"];
        // 根自身可能是软链：必须先解析成真实路径，否则后续比较全部基于一个假地址，
        // 会把所有合法路径都误拒。
        _realRoot = string.IsNullOrWhiteSpace(configured) ? null : ResolveReal(configured);
    }

    /// <summary>是否启用边界。未配置根时为 false，一切放行。</summary>
    public bool Enabled => _realRoot is not null;

    /// <summary>解析后的真实根；未启用时为 null。用于错误消息。</summary>
    public string? Root => _realRoot;

    /// <summary>路径是否在边界之内。未启用边界时恒为 true。</summary>
    public bool IsInside(string path)
    {
        if (_realRoot is null)
            return true;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var real = ResolveReal(path);
        return real is not null && IsWithin(_realRoot, real);
    }

    /// <summary>
    /// 逐段展开符号链接，得到真实路径。链接成环（超过 <see cref="MaxLinkDepth"/>）时返回 null。
    /// <para>
    /// 不能用 <c>Directory.ResolveLinkTarget(p, returnFinalTarget: true)</c> 代替：它只展开
    /// **最后一段**。若 <c>/nas/link</c> 是指向 <c>/etc</c> 的软链，查询 <c>/nas/link/passwd</c>
    /// 时它返回 null（passwd 自身不是链接），中间段的越界就被漏掉了。
    /// </para>
    /// <para>
    /// 路径不存在的段不是链接，直接拼接即可——这自然实现了「按最近已存在祖先判定」，
    /// 使尚未创建的还原目标可以通过。
    /// </para>
    /// </summary>
    public static string? ResolveReal(string path)
    {
        var full = Path.GetFullPath(path);
        var sep = Path.DirectorySeparatorChar;
        var root = Path.GetPathRoot(full) ?? sep.ToString();
        var segments = full[root.Length..].Split(sep, StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        var depth = 0;

        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);

            // 一段可能连环指向另一个链接，循环展开到不再是链接为止
            while (true)
            {
                if (++depth > MaxLinkDepth)
                    return null;

                // FileSystemInfo.LinkTarget 底层是 lstat，不关心目标是文件还是目录；
                // 路径不存在时返回 null，正是我们要的（不存在的段不是链接）。
                var target = new FileInfo(current).LinkTarget;
                if (target is null)
                    break;

                current = Path.IsPathRooted(target)
                    ? Path.GetFullPath(target)
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current) ?? root, target));
            }
        }

        return current;
    }

    /// <summary>
    /// 纯词法的包含判定：规范化后按**路径段边界**比较，不解析符号链接。
    /// <c>/target</c> 不包含 <c>/targetx</c>。还原写入用它防索引数据里的 <c>..</c>。
    /// </summary>
    public static bool IsWithin(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);

        if (string.Equals(full, fullRoot, StringComparison.Ordinal))
            return true;

        return full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
