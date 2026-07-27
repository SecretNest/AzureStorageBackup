using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>一个路径相对上一版本的变更分类。</summary>
public enum ChangeKind
{
    /// <summary>上一版本没有 → 新增。</summary>
    Added,

    /// <summary>内容变了，需重新处理/上传。</summary>
    Modified,

    /// <summary>内容不变，仅 mtime/权限变 → 只更新索引元数据，不重传。</summary>
    MetadataOnly,

    /// <summary>本轮读不开（被占用/无权限/读错误）。既不是变更也不是删除：
    /// 索引沿用上一版本条目并打 UnreadableAt，绝不能被当成删除。</summary>
    Unreadable,

    /// <summary>完全未变（length+mtime+权限一致，未哈希）。</summary>
    Unchanged,

    /// <summary>上一版本有、本次无 → 删除。</summary>
    Deleted,
}

/// <summary>单个路径的 diff 结果，携带构建新索引条目所需的已解析哈希/存储。</summary>
public sealed record FileChange(
    string Path,
    ChangeKind Kind,
    ScannedEntry? Current,
    IndexEntry? Previous,
    string? HeadHash,
    string? FullHash,
    StorageRef? CarriedStorage,
    /// <summary>读失败原因（ex.Message）。仅 Kind == Unreadable 时非空。</summary>
    string? UnreadableReason = null);

public sealed record DiffOptions
{
    /// <summary>headHash 覆盖的头部字节数（默认 4KB，M4 决策 §13.3）。</summary>
    public int HeadHashBytes { get; init; } = 4096;
}

/// <summary>diff 汇总。ChangedFiles/ChangedBytes 仅计 Added+Modified（未压缩、分组前，删除/仅元数据不计，§4）。</summary>
public sealed record DiffResult(
    IReadOnlyList<FileChange> Changes,
    int ChangedFiles,
    long ChangedBytes);

/// <summary>
/// 版本对比引擎（M4 设计 §4.2）：惰性两级哈希。
/// 先靠 length+mtime+权限 判断；仅"length 同但 mtime/权限变"的文件才算 headHash，
/// 再不同才算 fullHash。避免每次备份重读全部文件。
/// </summary>
public sealed class BackupDiffer(IFileHasher hasher)
{
    public async Task<DiffResult> DiffAsync(
        string rootPath,
        ScanResult current,
        VersionIndex? previous,
        DiffOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new DiffOptions();
        var root = Path.GetFullPath(rootPath);
        var prevByPath = (previous?.Entries ?? []).ToDictionary(e => e.Path, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var changes = new List<FileChange>();
        var changedFiles = 0;
        long changedBytes = 0;

        foreach (var entry in current.Entries)
        {
            ct.ThrowIfCancellationRequested();
            seen.Add(entry.Path);

            var full = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            var kind = entry.Kind == EntryKind.File ? "file" : "symlink";
            prevByPath.TryGetValue(entry.Path, out var prev);

            var change = prev is null
                ? await AddedAsync(entry, full, options, ct)
                : await CompareAsync(entry, prev, full, kind, options, ct);

            changes.Add(change);
            if (change.Kind is ChangeKind.Added or ChangeKind.Modified)
            {
                changedFiles++;
                changedBytes += entry.Length;
            }
        }

        // 扫描阶段就读不出来的路径，必须在"判删除"之前登记进 seen。
        // 一个列不出内容的目录，其下**整棵子树**都没能被扫到——若不登记，接下来那段循环会把
        // 这些既有条目一个不剩地判成删除，等于因为一次权限故障就把整棵子树从索引里抹掉，
        // 直到还原时才发现文件没了。读不开 ≠ 删除，这里是这条原则最要紧的一处。
        foreach (var u in current.Unreadable)
        {
            foreach (var prev in PreviousEntriesUnder(prevByPath, u))
            {
                if (seen.Add(prev.Path))
                    changes.Add(new FileChange(prev.Path, ChangeKind.Unreadable, null, prev, null, null, null, u.Reason));
            }

            // 读不开的**文件**即使上一版本没有（全新且从头就读不开），也要记一条：
            // 没有内容可指向、索引里不会有它，但操作员必须知道它本轮没被备份。
            if (!u.IsDirectory && !prevByPath.ContainsKey(u.Path) && seen.Add(u.Path))
                changes.Add(new FileChange(u.Path, ChangeKind.Unreadable, null, null, null, null, null, u.Reason));
        }

        foreach (var prev in prevByPath.Values)
        {
            if (!seen.Contains(prev.Path))
                changes.Add(new FileChange(prev.Path, ChangeKind.Deleted, null, prev, null, null, null));
        }

        return new DiffResult(changes, changedFiles, changedBytes);
    }

    /// <summary>某个读不出来的路径覆盖到的上一版本条目：目录取其整棵子树，文件取它自己。</summary>
    private static IEnumerable<IndexEntry> PreviousEntriesUnder(
        Dictionary<string, IndexEntry> prevByPath, UnreadablePath unreadable)
    {
        if (!unreadable.IsDirectory)
            return prevByPath.TryGetValue(unreadable.Path, out var one) ? [one] : [];

        // 根自身读不开时 Path 为 "."（GetRelativePath 对根给出的结果），此时整份索引都在其下。
        if (unreadable.Path is "" or ".")
            return prevByPath.Values;

        var prefix = unreadable.Path + "/";
        return prevByPath.Values.Where(e => e.Path.StartsWith(prefix, StringComparison.Ordinal));
    }

    private async Task<FileChange> CompareAsync(
        ScannedEntry entry, IndexEntry prev, string full, string kind, DiffOptions options, CancellationToken ct)
    {
        // 类型变更（file<->symlink）视为内容变更。
        if (prev.Kind != kind)
            return await ModifiedAsync(entry, prev, full, options, ct);

        if (entry.Kind == EntryKind.Symlink)
            return entry.Target == prev.Target
                ? Unchanged(entry, prev)
                : new FileChange(entry.Path, ChangeKind.Modified, entry, prev, null, null, null);

        // length 不同 → 直接变更，无需 head 预筛（仍需 fullHash 作去重键）。
        if (entry.Length != prev.Length)
            return await ModifiedAsync(entry, prev, full, options, ct);

        // length 同、mtime 与权限都同 → 未变，完全跳过哈希。
        if (entry.ModifiedAt == prev.Mtime && entry.Permissions == prev.Permissions)
            return Unchanged(entry, prev);

        // length 同、mtime 或权限变 → 两级哈希。
        return await TryReadAsync(async () =>
        {
            var head = await hasher.HeadHashAsync(full, options.HeadHashBytes, ct);
            if (head != prev.HeadHash)
            {
                var changedFull = await hasher.FullHashAsync(full, ct);
                return new FileChange(entry.Path, ChangeKind.Modified, entry, prev, head, changedFull, null);
            }

            var fullHash = await hasher.FullHashAsync(full, ct);
            return fullHash == prev.FullHash
                ? new FileChange(entry.Path, ChangeKind.MetadataOnly, entry, prev, head, fullHash, prev.Storage)
                : new FileChange(entry.Path, ChangeKind.Modified, entry, prev, head, fullHash, null);
        }, entry, prev);
    }

    private async Task<FileChange> AddedAsync(ScannedEntry entry, string full, DiffOptions options, CancellationToken ct)
    {
        if (entry.Kind == EntryKind.Symlink)
            return new FileChange(entry.Path, ChangeKind.Added, entry, null, null, null, null);

        return await TryReadAsync(async () =>
        {
            var head = await hasher.HeadHashAsync(full, options.HeadHashBytes, ct);
            var fullHash = await hasher.FullHashAsync(full, ct);
            return new FileChange(entry.Path, ChangeKind.Added, entry, null, head, fullHash, null);
        }, entry, null);
    }

    private async Task<FileChange> ModifiedAsync(ScannedEntry entry, IndexEntry prev, string full, DiffOptions options, CancellationToken ct)
    {
        if (entry.Kind == EntryKind.Symlink)
            return new FileChange(entry.Path, ChangeKind.Modified, entry, prev, null, null, null);

        // 记录完整的 headHash + fullHash（索引条目须含原文件哈希/尺寸/权限，供后续 diff 与还原比对）。
        return await TryReadAsync(async () =>
        {
            var head = await hasher.HeadHashAsync(full, options.HeadHashBytes, ct);
            var fullHash = await hasher.FullHashAsync(full, ct);
            return new FileChange(entry.Path, ChangeKind.Modified, entry, prev, head, fullHash, null);
        }, entry, prev);
    }

    private static FileChange Unchanged(ScannedEntry entry, IndexEntry prev) =>
        new(entry.Path, ChangeKind.Unchanged, entry, prev, prev.HeadHash, prev.FullHash, prev.Storage);

    /// <summary>
    /// 读失败（被占用/无权限/读到一半设备错误）不该终止整轮备份。
    /// 精确捕获这两类，**不要**写成 catch(Exception)：OperationCanceledException 不派生自它们，
    /// 写宽了会把取消也变成「跳过一个文件」，备份看起来成功、实际没跑完。
    /// </summary>
    private static async Task<FileChange> TryReadAsync(
        Func<Task<FileChange>> build, ScannedEntry entry, IndexEntry? prev)
    {
        try
        {
            return await build();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileChange(entry.Path, ChangeKind.Unreadable, entry, prev, null, null, null, ex.Message);
        }
    }
}
