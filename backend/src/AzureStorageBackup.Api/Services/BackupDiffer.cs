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
    StorageRef? CarriedStorage);

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

        foreach (var prev in prevByPath.Values)
        {
            if (!seen.Contains(prev.Path))
                changes.Add(new FileChange(prev.Path, ChangeKind.Deleted, null, prev, null, null, null));
        }

        return new DiffResult(changes, changedFiles, changedBytes);
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
    }

    private async Task<FileChange> AddedAsync(ScannedEntry entry, string full, DiffOptions options, CancellationToken ct)
    {
        if (entry.Kind == EntryKind.Symlink)
            return new FileChange(entry.Path, ChangeKind.Added, entry, null, null, null, null);

        var head = await hasher.HeadHashAsync(full, options.HeadHashBytes, ct);
        var fullHash = await hasher.FullHashAsync(full, ct);
        return new FileChange(entry.Path, ChangeKind.Added, entry, null, head, fullHash, null);
    }

    private async Task<FileChange> ModifiedAsync(ScannedEntry entry, IndexEntry prev, string full, DiffOptions options, CancellationToken ct)
    {
        if (entry.Kind == EntryKind.Symlink)
            return new FileChange(entry.Path, ChangeKind.Modified, entry, prev, null, null, null);

        var fullHash = await hasher.FullHashAsync(full, ct);
        return new FileChange(entry.Path, ChangeKind.Modified, entry, prev, null, fullHash, null);
    }

    private static FileChange Unchanged(ScannedEntry entry, IndexEntry prev) =>
        new(entry.Path, ChangeKind.Unchanged, entry, prev, prev.HeadHash, prev.FullHash, prev.Storage);
}
