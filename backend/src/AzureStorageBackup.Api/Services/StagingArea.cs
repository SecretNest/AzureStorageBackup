namespace AzureStorageBackup.Api.Services;

/// <summary>已移入 staged-temp 的一组卷文件，等待上传。</summary>
public sealed record StagedItem(IReadOnlyList<string> Files, long Bytes);

/// <summary>
/// 临时区状态机（M4 设计 §7）。
/// 压缩全局非并发（单一压缩锁）；压缩产出先写 compress-temp，完成后整套移入 staged-temp。
/// staged-temp 有字节上限：未达上限才启动下一个压缩（允许单个结果临时超限）；
/// 超限则阻塞新压缩，直到上传调用 <see cref="ReleaseFile"/> / <see cref="Release"/> 腾出空间。
/// <para>
/// 释放粒度是**单卷**而不是整族：一个大文件切出上千卷，整族传完才删的话，峰值占用等于整个归档
/// （100 GB 的文件就要 100 GB 临时空间——这条已经把备份撞失败过一次），而且水位整段贴在上限上，
/// 压缩被背压一直堵着。传完一卷删一卷之后，峰值只剩"还没传完的那几卷"。
/// </para>
/// </summary>
public sealed class StagingArea(string compressTempDir, string stagedTempDir, Func<long> stagedLimit) : IDisposable
{
    private readonly SemaphoreSlim _compressLock = new(1, 1);
    private readonly SemaphoreSlim _releaseSignal = new(0);
    // 每个已暂存文件占的字节。按卷释放要能精确扣账，而且必须**幂等**——同一卷会被上传路径
    // 逐卷释放一次、收尾时再随整族兜底一次，重复扣会把水位记成负的，压缩就再也不会被背压挡住了。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _staged =
        new(StringComparer.Ordinal);
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
            // 每次实时读当前上限（决策 4：Settings 改动立即生效）。
            while (Interlocked.Read(ref _stagedBytes) >= stagedLimit())
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

    /// <summary>
    /// **一卷**传完后调用：删掉这一卷、扣掉它占的字节、唤醒等待的压缩。
    /// 幂等——已经释放过（或压根不属于本暂存区）的路径直接忽略。
    /// </summary>
    public void ReleaseFile(string file)
    {
        if (!_staged.TryRemove(file, out var bytes))
            return;
        try { File.Delete(file); } catch { /* best effort */ }
        Interlocked.Add(ref _stagedBytes, -bytes);
        _releaseSignal.Release();
    }

    /// <summary>整族收尾：把还没逐卷释放掉的都释放掉（去重命中时一卷都没传，全在这里还），
    /// 再删空的 GUID 子目录。逐卷释放过的部分在 <see cref="ReleaseFile"/> 里已经幂等短路。</summary>
    public void Release(StagedItem item)
    {
        foreach (var file in item.Files)
            ReleaseFile(file);
        // 删空的 GUID 子目录。
        foreach (var dir in item.Files.Select(Path.GetDirectoryName).Distinct())
        {
            try { if (dir is not null && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch { /* best effort */ }
        }
        // 整族收尾也发一次信号：全部卷都已逐卷释放时上面一次都没发，等在背压里的压缩会漏掉唤醒。
        _releaseSignal.Release();
    }

    private StagedItem MoveToStaged(IReadOnlyList<string> producedFiles)
    {
        if (producedFiles.Count == 0)
            return new StagedItem([], 0); // 无产出：不建子目录，避免留下空 GUID 目录

        // 每次暂存独立 GUID 子目录：不同备份即使产出同名文件也不互相覆盖（跨 container 并发安全）。
        var subDir = Path.Combine(stagedTempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(subDir);
        var staged = new List<string>(producedFiles.Count);
        long bytes = 0;
        try
        {
            foreach (var src in producedFiles)
            {
                var dest = Path.Combine(subDir, Path.GetFileName(src));
                File.Move(src, dest, overwrite: false);
                var size = new FileInfo(dest).Length;
                bytes += size;
                _staged[dest] = size;   // 逐卷释放要按这份账扣，不能事后再 stat（那时文件已经删了）
                staged.Add(dest);
            }
        }
        catch
        {
            // 中途失败：清理已移动文件 + 子目录，不泄漏。异常沿 StageAsync 抛出，调用方不会把 bytes 记入 _stagedBytes，
            // 所以这里只从账本上摘掉、**不**去扣 _stagedBytes——那笔钱根本没记上过。
            foreach (var f in staged)
                _staged.TryRemove(f, out _);
            try { Directory.Delete(subDir, recursive: true); } catch { /* best effort */ }
            throw;
        }
        return new StagedItem(staged, bytes);
    }

    public void Dispose()
    {
        _compressLock.Dispose();
        _releaseSignal.Dispose();
    }
}
