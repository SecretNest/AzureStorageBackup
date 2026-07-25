namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 本地路径边界（设计 §3）。根来自 <c>Backup:Root</c>，**只做准入过滤**：
/// 不改写、不截断路径，也不作为相对路径基准。未配置时无边界，全部放行。
/// 单例：构造时解析一次真实根，之后不再变。
/// </summary>
public sealed class PathBoundary
{
    /// <summary>
    /// 符号链接**展开次数**上限（与 Linux 的 40 次一致）。只数展开，不数普通路径段，
    /// 否则一个没有任何软链的深目录也会被误判。超限判定为越界，而不是抛异常或死循环。
    /// </summary>
    private const int MaxLinkDepth = 40;

    private readonly string? _realRoot;

    public PathBoundary(IConfiguration config)
    {
        var configured = config["Backup:Root"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            // 未配置 = 无边界，全部放行（既有约定）。
            _realRoot = null;
            return;
        }

        // 根自身可能是软链：必须先解析成真实路径，否则后续比较全部基于一个假地址，
        // 会把所有合法路径都误拒。
        // 配了根却解析不出来（成环）必须**炸在启动期**：若沿用 null，
        // Enabled 会变成 false，边界静默消失、一切放行——配置错误伪装成「没配置」，
        // 是这里最坏的结果。
        // 注：「组件不可读」不是这里的成因——.NET 的 Unix ReadLink 会吞掉 EACCES，
        // chmod 000 的目录在 LinkTarget 上仍返回 null，被当成普通段处理，不会导致解析失败。
        _realRoot = ResolveReal(configured)
            ?? throw new InvalidOperationException(
                $"Backup root '{configured}' could not be resolved to a real path " +
                "(symlink cycle). Fix Backup__Root or the filesystem.");
    }

    /// <summary>是否启用边界。未配置根时为 false，一切放行。</summary>
    public bool Enabled => _realRoot is not null;

    /// <summary>解析后的真实根；未启用时为 null。用于错误消息。</summary>
    public string? Root => _realRoot;

    /// <summary>
    /// 路径是否在边界之内。未启用边界时恒为 true。
    /// <para>
    /// **只接受绝对路径**：相对路径一律拒绝，而不是像 <see cref="IsWithin"/> 那样接受
    /// 后用文档提醒调用方注意基准。原因是本方法是端点、调度器、目录浏览 API 的公共
    /// 入口，调用方众多且分散；若放行相对路径，底层 <see cref="ResolveReal"/> 会按**进程
    /// 当前工作目录**把它变成绝对路径再判定，一旦调用方后续真正的文件操作用了别的基准
    /// （例如某个显式指定的目录），判定结果和实际落盘位置就会不一致，且没有任何报错
    /// 提示这一点。与其把这条风险写进文档指望每个调用方都读到，不如在入口直接堵死这类
    /// 输入——这里的根**只做安全过滤**，从不作为相对路径基准，拒绝相对输入是这条原则
    /// 最直接的落地。
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
    /// 真正的 realpath：从文件系统根出发逐段前进，得到完全解析后的真实路径。
    /// 软链展开次数超过 <see cref="MaxLinkDepth"/>（成环）时返回 null。
    /// <para>
    /// 算法：维护一个**待处理段**栈和一个**已完全解析**的前缀 <c>resolved</c>，每次弹出一段：
    /// <list type="bullet">
    /// <item><c>.</c> 跳过；</item>
    /// <item><c>..</c> 砍掉 <c>resolved</c> 的最后一段（不越过文件系统根）；</item>
    /// <item>其余先拼到 <c>resolved</c>，再看这个新路径是不是软链——是的话把**目标的各段
    /// 压回待处理栈**（目标为绝对路径时同时把 <c>resolved</c> 重置回文件系统根），
    /// 让目标本身也被逐段重走。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 两处关键性质，缺一个就是可越界的洞：
    /// </para>
    /// <para>
    /// 1) **绝不预先用 <c>Path.GetFullPath</c> 折叠 <c>..</c>**。GetFullPath 是**词法**折叠，
    /// 而 POSIX 的 <c>..</c> 在软链展开**之后**才结算，两者只在跟着软链走时不一致——
    /// 而那正是攻击面：<c>&lt;root&gt;/escape/../secret</c> 词法折叠成 <c>&lt;root&gt;/secret</c>（界内），
    /// 实际却落在 <c>escape</c> 目标的父目录里（界外）。这里 <c>..</c> 作用在已解析前缀上，
    /// 顺序与内核一致。相对**输入**仍需变成绝对路径，但只做拼接，不折叠。
    /// </para>
    /// <para>
    /// 2) **软链目标必须重走，不能整体替换**。若把 <c>resolved</c> 直接换成目标的未解析字符串，
    /// 目标里位于末段之前的组件就永远不会被检查：<c>b -&gt; &lt;界外&gt;</c>、<c>a -&gt; &lt;root&gt;/b/c</c> 时
    /// <c>a</c> 会被判成 <c>&lt;root&gt;/b/c</c>（界内），实际是 <c>&lt;界外&gt;/c</c>。
    /// </para>
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
        // Path/FileInfo 遇到 \0 会抛 ArgumentException；这里当成不可解析，
        // 让调用方（IsInside）得到一次干净的拒绝而不是未处理异常。
        if (string.IsNullOrEmpty(path) || path.Contains('\0'))
            return null;

        // 相对输入按当前工作目录变成绝对路径——只拼接，不做任何 `..` 折叠。
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
                // resolved 此刻已完全解析，所以这一步与内核的求值顺序一致。
                var parent = Path.GetDirectoryName(resolved);
                resolved = string.IsNullOrEmpty(parent) ? fsRoot : parent;
                continue;
            }

            var candidate = Path.Combine(resolved, segment);

            // FileSystemInfo.LinkTarget 底层是 lstat，不关心目标是文件还是目录；
            // 路径不存在时返回 null，正是我们要的（不存在的段不是链接）。
            var target = new FileInfo(candidate).LinkTarget;
            if (target is null)
            {
                resolved = candidate;
                continue;
            }

            if (++expansions > MaxLinkDepth)
                return null;

            if (Path.IsPathRooted(target))
            {
                var targetRoot = Path.GetPathRoot(target);
                if (string.IsNullOrEmpty(targetRoot))
                    return null;
                PushSegments(pending, target[targetRoot.Length..]);
                resolved = targetRoot; // 绝对目标：从文件系统根重新走
            }
            else
            {
                // 相对目标以链接自身所在目录为基准，也就是当前的 resolved，不动它。
                PushSegments(pending, target);
            }
        }

        return resolved;
    }

    /// <summary>把一段路径拆成各段，倒序压栈，使弹出顺序等于从左到右。</summary>
    private static void PushSegments(Stack<string> pending, string path)
    {
        var parts = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
            pending.Push(parts[i]);
    }

    /// <summary>
    /// 纯词法的包含判定：规范化后按**路径段边界**比较，不解析符号链接。
    /// <c>/target</c> 不包含 <c>/targetx</c>。还原写入用它防索引数据里的 <c>..</c>。
    /// <para>
    /// **相对路径注意**：本方法只做词法处理，不碰文件系统（所以 <see cref="ResolveReal"/>
    /// 的软链问题与它无关），但 <c>root</c>/<c>candidate</c> 若是**相对**路径，会被
    /// <c>Path.GetFullPath</c> 按**进程当前工作目录**规范化——那几乎肯定不是调用方想要的基准。
    /// 还原写入必须**先把索引里的相对路径拼到目标根上**再调用本方法，例如
    /// <c>IsWithin(targetRoot, Path.Combine(targetRoot, entryPath))</c>，
    /// 不要把 <c>entryPath</c> 直接传进来。
    /// </para>
    /// <para>
    /// **不抛异常**：<c>root</c>/<c>candidate</c> 可能来自云端索引——恶意或损坏的数据。
    /// <c>Path.GetFullPath</c> 对含 <c>\0</c> 的路径和空字符串会抛 <see cref="ArgumentException"/>，
    /// 这里一律转成「无法规范化 = 判定越界」返回 <c>false</c>，不能让一条脏索引记录
    /// 变成未处理异常（500）。
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
    /// <c>Path.GetFullPath</c> 的不抛异常版本：\0、空字符串，以及底层可能抛出的其他
    /// <see cref="ArgumentException"/>，一律当成「无法规范化」返回 false。
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
