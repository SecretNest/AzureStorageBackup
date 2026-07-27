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
