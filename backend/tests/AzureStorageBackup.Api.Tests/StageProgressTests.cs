using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 阶段进度的节流与测速。节流不是优化而是必需：百万文件逐个上报会产生百万次对象分配，
/// 而人眼一秒看不了几次。但收尾**必须**强制产出一次终态，否则进度永远差最后一下——
/// 这个项目在 onItem 计数上已经踩过一次同形状的坑。
/// </summary>
public sealed class StageProgressTests
{
    [Fact]
    public void Throttles_Bursts_But_Never_Loses_The_Final_State()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Diffing", total: 1000, seen.Add);

        for (var i = 0; i < 1000; i++)
        {
            tracker.Touch($"file{i}.bin");
            tracker.Advance(10);
        }
        tracker.Complete();

        // 1000 个文件绝不该产生 1000 次上报。
        Assert.True(seen.Count < 50, $"expected heavy throttling, got {seen.Count} reports");

        // 但最后一次必须是完整的终态——差最后一下就是「永远 99%」。
        var final = seen[^1];
        Assert.Equal(1000, final.Processed);
        Assert.Equal(10_000, final.Bytes);
        Assert.Equal(100, final.Percent);
    }

    [Fact]
    public void Reports_The_Item_Currently_Being_Worked_On()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Diffing", total: 2, seen.Add);

        tracker.Touch("/nas/photos/IMG_0001.CR2");
        tracker.Advance(1024);
        tracker.Complete();

        // 至少有一次快照带着正在处理的那个路径——卡住时这是唯一能告诉人「卡在哪」的信息。
        Assert.Contains(seen, s => s.CurrentItem == "/nas/photos/IMG_0001.CR2");
    }

    /// <summary>总数未知时（扫描进行中）不能编造百分比，剩余时间同理。</summary>
    [Fact]
    public void Unknown_Total_Yields_No_Percentage_And_No_Estimate()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Scanning", total: 0, seen.Add);

        tracker.Touch("/nas/photos");
        tracker.Advance(0);
        tracker.Complete();

        Assert.Null(seen[^1].Percent);
        Assert.Null(seen[^1].EstimatedRemaining);
    }

    [Fact]
    public void Tracks_Concurrent_Items_In_Flight()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 3, seen.Add);

        tracker.BeginItem("packs/p0001.7z");
        tracker.BeginItem("packs/p0002.7z");
        tracker.Complete();
        Assert.Equal(
            ["packs/p0001.7z", "packs/p0002.7z"],
            seen[^1].ActiveItems.OrderBy(x => x, StringComparer.Ordinal));

        tracker.EndItem("packs/p0001.7z", 5000);
        tracker.Complete();
        Assert.Equal(["packs/p0002.7z"], seen[^1].ActiveItems);
        Assert.Equal(5000, seen[^1].Bytes);
    }

    /// <summary>
    /// 「多少在传、多少在准备、多少在排队」的分解。用户看到的现象是备份详情里只有一个
    /// 只增不减的 `N objects so far`：上传阶段的一件活要先过 7z（一箱 100 MB 几十秒起步）
    /// 才轮到推字节，那几十秒里在途项是空的、字节是 0，界面上完全看不出在干活还是卡死。
    /// </summary>
    [Fact]
    public void Reports_What_Is_Queued_And_What_Is_Being_Prepared()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add);

        for (var i = 0; i < 5; i++)
            tracker.Enqueue();
        tracker.BeginWork(); // 工作线程领走两件……
        tracker.BeginWork();
        tracker.BeginItem("packs/p0001.7z"); // ……其中一件已经在推字节，另一件还在压缩
        tracker.Complete();

        var s = seen[^1];
        Assert.Equal(["packs/p0001.7z"], s.ActiveItems);
        Assert.Equal(1, s.Preparing); // 手上 2 件 - 在传 1 件
        Assert.Equal(3, s.Queued);    // 入队 5 - 完成 0 - 手上 2
        Assert.Equal(0, s.Processed); // 这些记账一律**不**计数
    }

    /// <summary>队列深度必须能归零。BeginWork/EndWork 不配对（比如失败路径漏了 finally）
    /// 会让界面永远挂着几件"在准备"，而那时候其实什么都没在跑。</summary>
    [Fact]
    public void Queue_Depth_Drains_To_Zero_When_Every_Item_Is_Done()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 3, seen.Add);

        for (var i = 0; i < 3; i++)
            tracker.Enqueue();
        for (var i = 0; i < 3; i++)
        {
            tracker.BeginWork();
            tracker.Advance(10);
            tracker.EndWork();
        }
        tracker.Complete();

        Assert.Equal(0, seen[^1].Queued);
        Assert.Equal(0, seen[^1].Preparing);
        Assert.Equal(3, seen[^1].Processed);
    }

    /// <summary>三个计数各自独立推进，读到的必然是错开半拍的快照——消费者抢在入队记账落地之前
    /// 领走一件活是完全正常的时序。不夹到 0 以上，界面上就会闪出 "-1 queued"。</summary>
    [Fact]
    public void Skewed_Counters_Never_Produce_Negative_Numbers()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add);

        tracker.BeginWork(); // 入队还没落账，活已经被领走了
        tracker.Complete();

        Assert.Equal(0, seen[^1].Queued);
        Assert.Equal(1, seen[^1].Preparing);
    }

    /// <summary>
    /// 上传过程中的字节要**边传边计**，而不是等一个 blob 传完才一次性计入：传一个 100 MB 的包
    /// 要几十秒，那几十秒里测速窗口是空的，速度读数会归零——正是用户报的「看不到速度」。
    /// </summary>
    [Fact]
    public async Task Streaming_Byte_Reports_Produce_A_Live_Speed_Without_Counting_Items()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add);

        var progress = tracker.ItemProgress();
        progress.Report(1_000_000); // SDK 报的是**累计**值
        await Task.Delay(250);      // 跨过节流窗口，才会有第二个测速采样点
        progress.Report(3_000_000);
        tracker.Complete();

        Assert.True(seen[^1].BytesPerSecond > 0, "in-flight bytes should feed the speed readout");
        Assert.Equal(3_000_000, seen[^1].Bytes);
        Assert.Equal(0, seen[^1].Processed); // 字节回报不是槽位完成
    }

    /// <summary>累计值回退＝重试从头再传（或多卷的下一卷从 0 开始）。重传的字节要**再算一次**：
    /// 对「当下网速」而言这是对的，那些字节确实又过了一遍网线。</summary>
    [Fact]
    public void A_Retry_That_Restarts_The_Byte_Count_Is_Treated_As_Fresh_Traffic()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add);

        var progress = tracker.ItemProgress();
        progress.Report(100);
        progress.Report(300); // 同一次调用内继续累计 → +200
        progress.Report(50);  // 回退：重试从头来 → 这 50 是新流量
        tracker.Complete();

        Assert.Equal(350, seen[^1].Bytes);
    }

    /// <summary>在途项的起止**不得**计数。上传的槽位计数有「恰好一次」的约束——一个 pack 因成员
    /// 变化被重压时会经历多次上传，却始终只占 total 里的一个槽位。让 EndItem 顺手计数，
    /// 进度条就会冲过 100%（这个仓库在 onItem 上已经踩过一次重复计数）。</summary>
    [Fact]
    public void In_Flight_Bookkeeping_Does_Not_Advance_The_Count()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add);

        // 同一个槽位被重压两次：两轮上传，但只该计一次数。
        tracker.BeginItem("packs/p0001.7z");
        tracker.EndItem("packs/p0001.7z", 100);
        tracker.BeginItem("packs/p0001.7z");
        tracker.EndItem("packs/p0001.7z", 100);
        tracker.Advance(0); // 槽位完成，只在这里计数
        tracker.Complete();

        Assert.Equal(1, seen[^1].Processed);
        Assert.Equal(100, seen[^1].Percent); // 恰好 100%，不会超
    }
}
