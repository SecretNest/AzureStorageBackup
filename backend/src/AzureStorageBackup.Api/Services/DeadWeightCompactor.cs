using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

/// <summary>某 pack 内仍被有效版本引用的成员。按 entryName（归档条目名，pack 内唯一）标识——
/// 同内容不同路径会去重成同 fullHash 但仍是**两个**独立成员，故不可用 fullHash 作身份，否则压实会漏掉其一。</summary>
public sealed record LivePackMember(string EntryName, long Length, string FullHash);

/// <summary>
/// 死重压实（M4 设计 §6）：pack 内成员被删/变更且所有有效版本都不再引用后成为死重。
/// 死重比例（原始尺寸）超阈值时**原地重压**该 pack——保留仍有效成员、丢弃死重成员，覆盖同 packId blob（删旧分卷）。
/// 因 pack 按 packId+entryName 引用、有效成员 entryName 不变，无需改写任何版本索引。仅在版本退役时触发（死重只在此时增加）。
///
/// 成员内容来源：**优先用本地文件**（内容一致者，须 hash 确认）；本地缺失的成员——若允许下载（按数据 tier 开关）
/// 则下载云端 pack 解压补齐，否则**放弃该 pack 的重打包**（保留死重）。全部成员本地可得时无需任何下载（Archive 亦可压实）。
/// </summary>
public sealed class DeadWeightCompactor(
    IBlobUploader uploader, IFileCompressor compressor, IFileHasher hasher, string tempRoot,
    ILogger<DeadWeightCompactor>? logger = null)
{
    /// <param name="liveByPack">packId → (fullHash → 仍有效成员)，由清理器扫描保留版本索引得出。</param>
    public async Task CompactAsync(
        Account account, BlobContainerClient container, string? password, BackupInfoFile info,
        IReadOnlyDictionary<string, Dictionary<string, LivePackMember>> liveByPack,
        AccessTier dataTier, long? volumeBytes, double threshold,
        string? localRoot, bool allowDownload, CancellationToken ct)
    {
        foreach (var packId in info.Packs.Keys.ToList())
        {
            ct.ThrowIfCancellationRequested();
            var packInfo = info.Packs[packId];
            var live = liveByPack.GetValueOrDefault(packId);
            var liveBytes = live?.Values.Sum(m => m.Length) ?? 0;
            var deadBytes = Math.Max(0, packInfo.OriginalBytes - liveBytes);
            var ratio = packInfo.OriginalBytes == 0 ? 0 : (double)deadBytes / packInfo.OriginalBytes;

            if (ratio <= threshold)
            {
                if (packInfo.DeadBytes != deadBytes)
                    info.Packs[packId] = packInfo with { DeadBytes = deadBytes };
                continue;
            }

            // 全部成员死重 → 整个 pack 已不被引用，交由保留清理删除，这里不处理。
            if (live is null || live.Count == 0)
                continue;

            try
            {
                var newSizes = await RecompactAsync(
                    account, container, password, packId, live, localRoot, dataTier, allowDownload, volumeBytes, ct);
                if (newSizes.Count > 0)
                {
                    info.Packs[packId] = packInfo with
                    {
                        Members = [.. live.Values.Select(m => m.FullHash)],
                        OriginalBytes = liveBytes,
                        DeadBytes = 0,
                        Volumes = newSizes.Count,
                        VolumeSizes = [.. newSizes],
                    };
                    logger?.LogInformation(
                        "Compacted pack {Pack}: dropped {Dead} bytes of dead weight ({Ratio:P0})", packId, deadBytes, ratio);
                }
                else
                {
                    // 本地缺失成员且不允许下载 → 放弃重打包，仅记录死重。
                    info.Packs[packId] = packInfo with { DeadBytes = deadBytes };
                    logger?.LogInformation(
                        "Dead-weight compaction skipped for pack {Pack}: missing members not available locally and download disabled for this tier",
                        packId);
                }
            }
            catch (Exception ex)
            {
                info.Packs[packId] = packInfo with { DeadBytes = deadBytes };
                logger?.LogWarning(ex, "Dead-weight compaction failed for pack {Pack}", packId);
            }
        }
    }

    /// <returns>非空=已重压（新各分卷尺寸）；空=放弃重压（本地缺失成员且不允许下载，
    /// 或索引里的成员名越出 compose 目录）。</returns>
    private async Task<IReadOnlyList<long>> RecompactAsync(
        Account account, BlobContainerClient container, string? password, string packId,
        Dictionary<string, LivePackMember> live, string? localRoot, AccessTier dataTier,
        bool allowDownload, long? volumeBytes, CancellationToken ct)
    {
        var baseRef = $"packs/{packId}.7z";
        var work = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        var composeDir = Path.Combine(work, "compose");

        // EntryName 来自云端索引，/import 之后即攻击者可控（设计 §5）。ToLocal 之后若含 `..`
        // 或是绝对路径，下面每一处 Path.Combine 都会落到目标目录之外：
        //   - LocalPath → localRoot 之外的存在性探测（无 hash 门，纯预言机）；
        //   - CopyInto 的 dest → composeDir 之外的**任意写**，内容还是从 pack 里解压出来的；
        //   - 补齐分支的 source → extractDir 之外的读取。
        // 三处拼的是同一段字符串，越界与否一致，所以在入口一次判完。
        // 处置是**整包放弃压实**，不是「跳过该成员」：跳过会悄悄丢掉一个仍被引用的成员，
        // 而放弃只是保留死重——返回 [] 走的正是既有那条安全空操作路径（成员本地缺失且
        // 不允许下载）。压实本就是纯优化，宁可不做。
        if (live.Values.Any(m => !PathBoundary.IsWithin(composeDir, Path.Combine(composeDir, ToLocal(m.EntryName)))))
        {
            logger?.LogWarning(
                "Dead-weight compaction skipped for pack {Pack}: an index entry name escapes the compose directory",
                packId);
            return [];
        }

        // 优化：先按「存在性」判断是否有本地缺失成员；若缺失且不允许下载，直接放弃（不做任何 hash 比对）。
        var hasAbsentLocal = live.Values.Any(m => !File.Exists(LocalPath(localRoot, m.EntryName)));
        if (hasAbsentLocal && !allowDownload)
            return [];

        Directory.CreateDirectory(composeDir);
        try
        {
            // 本地文件内容一致者直接采用（须 hash 确认，即便长度/时间/权限相同）；其余需从云端 pack 补齐。
            var needFromPack = new List<string>();
            foreach (var member in live.Values)
            {
                var localPath = LocalPath(localRoot, member.EntryName);
                if (localPath is not null && File.Exists(localPath)
                    && await hasher.FullHashAsync(localPath, ct) == member.FullHash)
                    CopyInto(composeDir, member.EntryName, localPath);
                else
                    needFromPack.Add(member.EntryName);
            }

            if (needFromPack.Count > 0)
            {
                if (!allowDownload)
                    return [];

                // 下载并解压旧 pack，取出本地缺失的成员。
                var extractDir = Path.Combine(work, "x");
                var firstVolume = await VolumeBlobIO.DownloadAsync(container, baseRef, work, ct);
                await compressor.ExtractAsync(firstVolume, extractDir, password, ct);
                foreach (var entryName in needFromPack)
                    CopyInto(composeDir, entryName, Path.Combine(extractDir, ToLocal(entryName)));
            }

            // 用仍有效成员重压成新归档，替换同 packId：先覆盖上传新卷、后删残留旧卷（不再先删空）。
            var outDir = Path.Combine(work, "out");
            Directory.CreateDirectory(outDir);
            var output = Path.Combine(outDir, packId + ".7z");
            var result = await compressor.CompressAsync(
                new CompressionRequest(composeDir, [.. live.Values.Select(m => m.EntryName)], output, password,
                    VolumeBytes: volumeBytes, StoreOnly: false), ct);

            await VolumeBlobIO.ReplaceAsync(
                uploader, account, container, baseRef, result.VolumeFiles, dataTier, retry: null, ct);
            return result.VolumeFiles.Select(f => new FileInfo(f).Length).ToList();
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string? LocalPath(string? localRoot, string entryName) =>
        localRoot is null ? null : Path.Combine(localRoot, ToLocal(entryName));

    private static void CopyInto(string composeDir, string entryName, string source)
    {
        var dest = Path.Combine(composeDir, ToLocal(entryName));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(source, dest, overwrite: true);
    }

    private static string ToLocal(string entryName) => entryName.Replace('/', Path.DirectorySeparatorChar);
}
