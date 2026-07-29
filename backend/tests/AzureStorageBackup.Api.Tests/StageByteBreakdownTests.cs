using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 进度里的字节明细。跑长任务时用户要回答的是「还剩多少、压好了多少没送走、真正落到云上多少」，
/// 而这三段**必须互不重叠**——同一批字节被数两遍，加起来对不上总量，这一行就比没有更糟。
/// <para>
/// 完成度也从件数改成按源字节算：一件活可能是一个 100 GB 的单文件，也可能是一箱几百个 5 KB 的
/// 小文件，按件数等于把它们当成一样重。
/// </para>
/// </summary>
public sealed class StageByteBreakdownTests
{
    private static (StageTracker Tracker, List<StageProgress> Seen) Rig(
        int total = 0, Func<long>? stagedBytes = null)
    {
        var seen = new List<StageProgress>();
        return (new StageTracker("Uploading", total, seen.Add, speedWhileInFlight: true, stagedBytes), seen);
    }

    /// <summary>
    /// 已传只认**走完**的流。在途那条传了一半时，它的字节属于"在途"，不属于"已传"——
    /// 界面上那个已传要能回答"有多少已经稳稳落在云上"。
    /// </summary>
    [Fact]
    public void Transferred_Counts_Only_Finished_Flows()
    {
        var (tracker, seen) = Rig();

        tracker.BeginItem("data/aaa.001", "photos/a.bin", 1000);
        tracker.ItemProgress("data/aaa.001").Report(400);
        tracker.Complete();
        Assert.Equal(0, seen[^1].TransferredBytes);   // 还在途，一个字节都不算"已传"

        tracker.EndItem("data/aaa.001", 0);
        tracker.Complete();
        Assert.Equal(400, seen[^1].TransferredBytes); // 走完了才认
    }

    /// <summary>
    /// 待传 = 池子占用 − 在途已经传走的那部分。那几卷确实还整个躺在池子里（逐卷释放，传完才删），
    /// 送走的只是其中一截；不减就会在这里和在途列表里把同一批字节各数一遍。
    /// </summary>
    [Fact]
    public void Staged_Subtracts_What_The_In_Flight_Flows_Already_Sent()
    {
        var pool = 1_000L;
        var (tracker, seen) = Rig(stagedBytes: () => pool);

        tracker.BeginItem("data/aaa.001", "photos/a.bin", 600);
        tracker.Complete();
        Assert.Equal(1000, seen[^1].StagedBytes);   // 一个字节都还没送出去

        tracker.ItemProgress("data/aaa.001").Report(250);
        tracker.Complete();
        Assert.Equal(750, seen[^1].StagedBytes);    // 送走 250，池子里还剩 750 没走
    }

    /// <summary>在途每一条都要带得出「是谁、多大、传了多少」——标签是源文件路径，不是内容寻址的 blob 名。</summary>
    [Fact]
    public void In_Flight_Carries_Label_Size_And_Progress()
    {
        var (tracker, seen) = Rig();

        tracker.BeginItem("data/9f2a3b7c.001", "photos/2024/IMG_0042.mov", 2000);
        tracker.ItemProgress("data/9f2a3b7c.001").Report(500);
        tracker.Complete();

        var flow = Assert.Single(seen[^1].ActiveItems);
        Assert.Equal("photos/2024/IMG_0042.mov", flow.Label);
        Assert.Equal(500, flow.Sent);
        Assert.Equal(2000, flow.Total);
        Assert.Equal(25, flow.Percent);
    }

    /// <summary>省略标签时退回用 key，与从前的行为一致（还原/校验那两条路暂时没有源路径可给）。</summary>
    [Fact]
    public void A_Flow_Without_A_Label_Falls_Back_To_Its_Key()
    {
        var (tracker, seen) = Rig();

        tracker.BeginItem("packs/p0001.7z");
        tracker.Complete();

        Assert.Equal("packs/p0001.7z", Assert.Single(seen[^1].ActiveItems).Label);
    }

    /// <summary>
    /// 完成度按源字节算。总量还没定下来时（diff 还在往队列里塞活）必须给 null——
    /// 那时分母还在长，算出来的百分比会先冲高再掉回去。
    /// </summary>
    [Fact]
    public void Work_Percent_Waits_Until_The_Total_Is_Settled()
    {
        var (tracker, seen) = Rig();

        tracker.Enqueue(work: 800);
        tracker.Enqueue(work: 200);
        tracker.Advance(0, work: 500);
        tracker.Complete();
        Assert.Null(seen[^1].WorkPercent);   // 件数总量未定 → 分母还可能长

        tracker.SetTotal(2);                  // diff 收工，总量到此确定
        tracker.Complete();
        Assert.Equal(50, seen[^1].WorkPercent);
        Assert.Equal(1000, seen[^1].WorkTotal);
        Assert.Equal(500, seen[^1].WorkDone);
        Assert.Equal(500, seen[^1].WorkRemaining);
    }

    /// <summary>
    /// 按字节与按件数会给出**不同**的答案，这正是改用字节的理由：一件 100 GB 加一件 1 KB，
    /// 传完那件小的，按件数是 50%，按字节几乎还是 0。
    /// </summary>
    [Fact]
    public void Byte_Percent_Does_Not_Follow_Item_Percent()
    {
        var (tracker, seen) = Rig(total: 2);

        tracker.Enqueue(work: 100_000_000_000);
        tracker.Enqueue(work: 1_000);
        tracker.Advance(0, work: 1_000);      // 小的那件传完了
        tracker.Complete();

        Assert.Equal(50, seen[^1].Percent);   // 件数：一半
        Assert.Equal(0, seen[^1].WorkPercent); // 字节：几乎没动
    }

    /// <summary>
    /// 下载侧能事先报出总传输量（索引里记着各卷尺寸），上传侧报不出——压完才知道有多大。
    /// 分母缺失时必须是 0 而不是一个偏小的数：拿它算百分比会一路虚高，然后卡在 100% 上不动。
    /// </summary>
    [Fact]
    public void Transfer_Total_Is_Only_Reported_When_Declared()
    {
        var (tracker, seen) = Rig();

        tracker.Enqueue(work: 1000);                    // 上传侧：只申报源字节
        tracker.Complete();
        Assert.Equal(0, seen[^1].TransferTotal);

        tracker.Enqueue(work: 500, transfer: 120);      // 下载侧：两笔都申报
        tracker.Enqueue(work: 500, transfer: 80);
        tracker.Complete();
        Assert.Equal(200, seen[^1].TransferTotal);
    }

    /// <summary>没有池子的阶段（扫描/差分/本地检查）不报待传字节，那一行在界面上整段消失。</summary>
    [Fact]
    public void Stages_Without_A_Pool_Report_No_Staged_Bytes()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Diffing", total: 10, seen.Add);

        tracker.Advance(100);
        tracker.Complete();

        Assert.Equal(0, seen[^1].StagedBytes);
    }
}
