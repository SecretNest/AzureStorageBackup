using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>还原下载量/解压量估算结果（§4.1b，需求 A）。DistinctObjects 为存储键（"pack:{Ref}" / "blob:{Ref}"），
/// 供端点对各自首卷发起 HEAD 判定活化状态（决策 5）。</summary>
public sealed record RestoreEstimate(long DownloadBytes, long UncompressedBytes, int FileCount, IReadOnlyList<string> DistinctObjects);

/// <summary>
/// 还原量估算（纯逻辑、不触网）：选中路径 → 索引条目 → 按存储对象去重（共享 pack/去重 blob 只计一次）
/// 合计下载量（各卷尺寸）与解压量（文件 Length 合计）。
/// </summary>
public static class RestoreEstimator
{
    public static RestoreEstimate Compute(VersionIndex index, BackupInfoFile info, IReadOnlyCollection<string> paths)
    {
        var pathSet = new HashSet<string>(paths, StringComparer.Ordinal);
        var selected = index.Entries.Where(e => pathSet.Contains(e.Path) && e.Storage is not null).ToList();

        long uncompressed = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long download = 0;

        foreach (var e in selected)
        {
            uncompressed += e.Length;

            var storage = e.Storage!;
            var key = StorageKey(storage);
            if (!seen.Add(key))
                continue;

            download += storage.Kind == "pack"
                ? (info.Packs.TryGetValue(storage.Ref, out var pack) ? pack.VolumeSizes.Sum() : 0)
                : storage.VolumeSizes.Sum();
        }

        return new RestoreEstimate(download, uncompressed, selected.Count, seen.ToList());
    }

    private static string StorageKey(StorageRef s) => s.Kind == "pack" ? "pack:" + s.Ref : "blob:" + s.Ref;
}
