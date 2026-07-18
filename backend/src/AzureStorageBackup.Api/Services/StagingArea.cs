namespace AzureStorageBackup.Api.Services;

/// <summary>已移入 staged-temp 的一组卷文件，等待上传。</summary>
public sealed record StagedItem(IReadOnlyList<string> Files, long Bytes);

/// <summary>
/// 临时区状态机（M4 设计 §7）。
/// 压缩全局非并发（单一压缩锁）；压缩产出先写 compress-temp，完成后整套移入 staged-temp。
/// staged-temp 有字节上限：未达上限才启动下一个压缩（允许单个结果临时超限）；
/// 超限则阻塞新压缩，直到上传调用 Release 腾出空间。
/// </summary>
public sealed class StagingArea(string compressTempDir, string stagedTempDir, long stagedLimitBytes) : IDisposable
{
    private readonly SemaphoreSlim _compressLock = new(1, 1);
    private readonly SemaphoreSlim _releaseSignal = new(0);
    private long _stagedBytes;

    public long StagedBytes => Interlocked.Read(ref _stagedBytes);

    public async Task<StagedItem> StageAsync(
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> produce,
        CancellationToken ct = default)
    {
        // 全局非并发：同一时刻只有一个压缩。
        await _compressLock.WaitAsync(ct);
        try
        {
            // 背压：超限时等待上传腾出空间（从上限以下起步则允许本次结果临时超限）。
            while (Interlocked.Read(ref _stagedBytes) >= stagedLimitBytes)
                await _releaseSignal.WaitAsync(ct);

            Directory.CreateDirectory(compressTempDir);
            Directory.CreateDirectory(stagedTempDir);

            var produced = await produce(compressTempDir, ct);
            var item = MoveToStaged(produced);
            Interlocked.Add(ref _stagedBytes, item.Bytes);
            return item;
        }
        finally
        {
            _compressLock.Release();
        }
    }

    /// <summary>上传完成后调用：删除 staged 文件、释放其占用的字节、唤醒等待的压缩。</summary>
    public void Release(StagedItem item)
    {
        foreach (var file in item.Files)
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
        // 删空的 GUID 子目录。
        foreach (var dir in item.Files.Select(Path.GetDirectoryName).Distinct())
        {
            try { if (dir is not null && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch { /* best effort */ }
        }
        Interlocked.Add(ref _stagedBytes, -item.Bytes);
        _releaseSignal.Release();
    }

    private StagedItem MoveToStaged(IReadOnlyList<string> producedFiles)
    {
        // 每次暂存独立 GUID 子目录：不同备份即使产出同名文件也不互相覆盖（跨 container 并发安全）。
        var subDir = Path.Combine(stagedTempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(subDir);
        var staged = new List<string>(producedFiles.Count);
        long bytes = 0;
        foreach (var src in producedFiles)
        {
            var dest = Path.Combine(subDir, Path.GetFileName(src));
            File.Move(src, dest, overwrite: false);
            bytes += new FileInfo(dest).Length;
            staged.Add(dest);
        }
        return new StagedItem(staged, bytes);
    }

    public void Dispose()
    {
        _compressLock.Dispose();
        _releaseSignal.Dispose();
    }
}
