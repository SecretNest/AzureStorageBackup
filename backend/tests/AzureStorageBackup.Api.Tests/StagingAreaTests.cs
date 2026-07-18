using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class StagingAreaTests : IDisposable
{
    private readonly string _root;
    private readonly string _compressTemp;
    private readonly string _stagedTemp;

    public StagingAreaTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "asb-stage-" + Guid.NewGuid().ToString("N"));
        _compressTemp = Path.Combine(_root, "compress");
        _stagedTemp = Path.Combine(_root, "staged");
        Directory.CreateDirectory(_compressTemp);
        Directory.CreateDirectory(_stagedTemp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private StagingArea Area(long limit) => new(_compressTemp, _stagedTemp, () => limit);

    private StagingArea AreaP(Func<long> limit) => new(_compressTemp, _stagedTemp, limit);

    /// <summary>假压缩：在 compress-temp 写一个 size 字节的卷文件。</summary>
    private static Func<string, CancellationToken, Task<IReadOnlyList<string>>> Produce(string name, int size)
        => async (dir, ct) =>
        {
            var path = Path.Combine(dir, name);
            await File.WriteAllBytesAsync(path, new byte[size], ct);
            return [path];
        };

    [Fact]
    public async Task Staged_Item_Is_Moved_From_Compress_To_Staged_Temp()
    {
        using var area = Area(limit: 1_000_000);

        var item = await area.StageAsync(Produce("v1", 500));

        Assert.Empty(Directory.GetFiles(_compressTemp));               // moved out of compress-temp
        var staged = Assert.Single(item.Files);
        // 现在暂存文件在 staged-temp 的 GUID 子目录里（跨备份隔离，防同名覆盖）。
        Assert.Equal(_stagedTemp, Path.GetDirectoryName(Path.GetDirectoryName(staged)));
        Assert.True(File.Exists(staged));
        Assert.Equal(500, item.Bytes);
        Assert.Equal(500, area.StagedBytes);
    }

    [Fact]
    public async Task Concurrent_Same_Named_Outputs_Do_Not_Collide()
    {
        using var area = Area(limit: 1_000_000);

        // 两次暂存产出「同名」文件（模拟不同 container 都从 p0001.7z 起）。
        // 压缩串行，但两份必须落在不同子目录、内容各自完整。
        var item1 = await area.StageAsync(Produce("p0001.7z", 100));
        var item2 = await area.StageAsync(Produce("p0001.7z", 200));

        var f1 = Assert.Single(item1.Files);
        var f2 = Assert.Single(item2.Files);
        Assert.NotEqual(f1, f2);                       // 不同路径
        Assert.True(File.Exists(f1) && File.Exists(f2));
        Assert.Equal(100, new FileInfo(f1).Length);    // 各自内容完整、未被覆盖
        Assert.Equal(200, new FileInfo(f2).Length);
        Assert.Equal(300, area.StagedBytes);

        area.Release(item1);
        Assert.False(File.Exists(f1));
        Assert.False(Directory.Exists(Path.GetDirectoryName(f1)));  // 空子目录一并清除
        Assert.True(File.Exists(f2));
    }

    [Fact]
    public async Task Compression_Is_Globally_Non_Concurrent()
    {
        using var area = Area(limit: 1_000_000);
        var concurrent = 0;
        var maxConcurrent = 0;

        Func<string, CancellationToken, Task<IReadOnlyList<string>>> Job(string name) => async (dir, ct) =>
        {
            var now = Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, now);
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref concurrent);
            var path = Path.Combine(dir, name);
            await File.WriteAllBytesAsync(path, new byte[10], ct);
            return (IReadOnlyList<string>)[path];
        };

        await Task.WhenAll(
            area.StageAsync(Job("a")),
            area.StageAsync(Job("b")),
            area.StageAsync(Job("c")));

        Assert.Equal(1, maxConcurrent);
    }

    [Fact]
    public async Task Over_Limit_Blocks_Next_Compression_Until_Release()
    {
        using var area = Area(limit: 100);

        // 从 0 起步允许超限：150 > 100，仍执行。
        var first = await area.StageAsync(Produce("v1", 150));
        Assert.Equal(150, area.StagedBytes);

        var secondStarted = false;
        var second = area.StageAsync(async (dir, ct) =>
        {
            secondStarted = true;
            return await Produce("v2", 10)(dir, ct);
        });

        await Task.Delay(150);
        Assert.False(secondStarted);     // 背压：超限时下一个压缩不启动
        Assert.False(second.IsCompleted);

        area.Release(first);             // 上传完成腾出空间

        var item = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(secondStarted);
        Assert.Equal(10, item.Bytes);
    }

    [Fact]
    public async Task Backpressure_Reads_Limit_Live_From_Provider()
    {
        long limit = 100;                      // 初始极小上限
        using var area = AreaP(() => limit);

        // 首个结果允许临时超限（从上限以下起步）。
        var first = await area.StageAsync(Produce("a", 500));
        Assert.Equal(500, area.StagedBytes);   // 已超过 100

        // 第二个压缩应被背压阻塞（StagedBytes 500 >= limit 100）。
        var blocked = area.StageAsync(Produce("b", 10));
        Assert.False(blocked.IsCompleted);

        // 调大上限 → 唤醒需要一次 Release 触发信号；这里改为先 Release 首个腾出空间。
        area.Release(first);                   // StagedBytes -> 0，唤醒
        var second = await blocked;
        Assert.Equal(10, area.StagedBytes);
    }

    [Fact]
    public async Task Release_Deletes_Staged_Files()
    {
        using var area = Area(limit: 1_000_000);
        var item = await area.StageAsync(Produce("v1", 42));
        var path = item.Files[0];

        area.Release(item);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Empty_Produce_Leaves_No_Subdir()
    {
        using var area = Area(limit: 1_000_000);

        var item = await area.StageAsync((_, _) => Task.FromResult<IReadOnlyList<string>>([]));

        Assert.Empty(item.Files);
        Assert.Equal(0, item.Bytes);
        Assert.Equal(0, area.StagedBytes);
        Assert.Empty(Directory.GetDirectories(_stagedTemp)); // 不留空 GUID 子目录
    }

    [Fact]
    public async Task Partial_Move_Failure_Cleans_Up_And_Does_Not_Leak_Or_Miscredit()
    {
        using var area = Area(limit: 1_000_000);

        // 产出两个路径：第一个真实存在，第二个不存在 → 第二次 File.Move 抛（源缺失）。
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> produce = async (dir, ct) =>
        {
            var ok = Path.Combine(dir, "ok.7z");
            await File.WriteAllBytesAsync(ok, new byte[10], ct);
            return [ok, Path.Combine(dir, "missing.7z")];
        };

        await Assert.ThrowsAnyAsync<Exception>(() => area.StageAsync(produce));

        Assert.Empty(Directory.GetDirectories(_stagedTemp)); // 已移动文件 + 子目录被清理，不泄漏
        Assert.Equal(0, area.StagedBytes);                    // 异常路径不错记字节
    }
}
