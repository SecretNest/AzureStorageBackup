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

    /// <summary>
    /// 「diff 已经跑到上传前面、开始往盘上攒活」要能被看见，而且**头一件**必须立刻发布——
    /// 那一刻正是这段说明存在的理由，压进节流窗口的话界面会先安静一段再突然蹦出个大数。
    /// 之后的更新照常走节流：它只是同一个数在长，没有一次值得单独打断。
    /// </summary>
    [Fact]
    public void First_Spilled_Item_Is_Published_Immediately()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Diffing", total: 100, seen.Add);

        tracker.Touch("photos/a.bin");
        tracker.Advance(0);
        Assert.Equal(0, seen[^1].SpilledItems);

        tracker.SetSpilled(1);
        Assert.Equal(1, seen[^1].SpilledItems);

        // 0 → 非 0 已经发生过了，之后只是同一个数在长，没有哪一次值得单独打断节流。
        // 这里不去断言"下一拍还没发出来"——那要赌节流窗口的长短。要守的是另一件事：
        // 一旦有人强制发布（阶段收尾就是），带出来的必须是最新的值，而不是停在 1。
        tracker.SetSpilled(4096);
        tracker.Complete();
        Assert.Equal(4096, seen[^1].SpilledItems);
    }

    /// <summary>
    /// 上传侧的「已传」按**件**记，不按卷——因为它要和按件销账的原始字节摆在一起读。
    /// 一件大活分成许多卷，前几卷传完时那些字节**确实已经在云上**（按卷累加没有虚报），
    /// 但原始字节要等整件完成才跳，于是分子按卷、分母按件，两个真实的数字凑不出能读的比值：
    /// 界面上那个 "X uploaded (N% of original)" 会结构性地冲过 100%（实测 112%，那件活完成后
    /// 落回 99%），文件越大差得越远，和压缩率毫无关系。
    /// </summary>
    [Fact]
    public void Uploaded_Never_Runs_Ahead_Of_The_Original_Bytes_It_Is_Compared_With()
    {
        var (tracker, seen) = Rig();
        tracker.SetTransferred(0);   // 上传侧宣告：已传字节由件级读数接管

        // 一件 10 GB 的活切成 4 卷，压缩后共 8 GB。前 3 卷传完了。
        tracker.Enqueue(10_000);
        foreach (var (vol, size) in new[] { ("d.001", 2_000L), ("d.002", 2_000L), ("d.003", 2_000L) })
        {
            tracker.BeginItem(vol, "photos/big.bin", size);
            tracker.ItemProgress(vol).Report(size);
            tracker.EndItem(vol, 0);
        }
        tracker.Complete();

        // 那 6 GB 确实在云上了，但这件活还没销账（WorkDone 仍是 0）。此刻报出去就是
        // 分子有、分母无——正是 112% 的来源。
        Assert.Equal(0, seen[^1].WorkDone);
        Assert.Equal(0, seen[^1].TransferredBytes);

        // 末卷传完，整件销账：两个数字同一时刻落地，比值这才第一次有意义。
        tracker.BeginItem("d.004", "photos/big.bin", 2_000);
        tracker.ItemProgress("d.004").Report(2_000);
        tracker.EndItem("d.004", 0);
        tracker.Advance(0, 10_000);
        tracker.SetTransferred(8_000);
        tracker.Complete();

        Assert.Equal(10_000, seen[^1].WorkDone);
        Assert.Equal(8_000, seen[^1].TransferredBytes);
        Assert.True(seen[^1].TransferredBytes <= seen[^1].WorkDone, "已传不该跑在它被拿来比的原始字节前面");
    }

    /// <summary>
    /// 件级读数是**绝对值**，不是增量：它取自运行期那本"整件传完才记"的账
    /// （<c>RunState.UploadedBytes</c>），与完工日志里那个"本次上传量"同源，界面和日志因此对得上。
    /// 顺带免疫两处按卷累加固有的偏差——重传的字节（DeltaProgress 把回退按"重新开始"处理，
    /// 对测速是对的，但云上还是那一份）和 if-missing 命中已存在 blob（一个字节都没上网线）。
    /// </summary>
    [Fact]
    public void The_Item_Level_Reading_Overrides_Per_Volume_Accumulation()
    {
        var (tracker, seen) = Rig();
        tracker.SetTransferred(0);

        // 同一卷传到一半断了、重来一遍：网线上过了 1500 字节，云上只落了 1000。
        tracker.BeginItem("d.001", "a.bin", 1000);
        tracker.ItemProgress("d.001").Report(500);
        tracker.ItemProgress("d.001").Report(1000);   // 累计回退＝重传，DeltaProgress 按重新开始处理
        tracker.EndItem("d.001", 0);
        tracker.Advance(0, 4000);
        tracker.SetTransferred(1000);                 // 件级账只认真正落云的那一份
        tracker.Complete();

        Assert.Equal(1000, seen[^1].TransferredBytes);
        // 测速那本账**照旧**含重传——那些字节确实又过了一遍网线，当下网速要的正是这个。
        Assert.Equal(1500, seen[^1].Bytes);
    }

    /// <summary>
    /// 按件记账把"传完的卷"挤出了 uploaded，但那些字节**确实已经在云上**，不能就此从界面上消失。
    /// 它们落在单独一栏里，整件完成时并入 uploaded 并归零。
    /// </summary>
    [Fact]
    public void Volumes_Already_On_The_Cloud_Are_Shown_While_Their_Item_Is_Unfinished()
    {
        var (tracker, seen) = Rig();
        tracker.SetTransferred(0);

        // 一件活切成两卷，压缩后共 8000。第一卷传完了。
        tracker.BeginItem("d.001", "photos/big.bin", 5_000);
        tracker.ItemProgress("d.001").Report(5_000);
        tracker.EndItem("d.001", 0);
        tracker.Complete();

        Assert.Equal(0, seen[^1].TransferredBytes);          // 件没完成，进不了这本账
        Assert.Equal(5_000, seen[^1].UnfinishedItemBytes);   // 但它已经在云上了，得看得见

        tracker.BeginItem("d.002", "photos/big.bin", 3_000);
        tracker.ItemProgress("d.002").Report(3_000);
        tracker.EndItem("d.002", 0);
        tracker.Advance(0, 10_000);
        tracker.SetTransferred(8_000);
        tracker.Complete();

        Assert.Equal(8_000, seen[^1].TransferredBytes);
        Assert.Equal(0, seen[^1].UnfinishedItemBytes);   // 并入之后归零，界面上整段消失
    }

    /// <summary>
    /// 多件同时在传时，一件完成**不能**把这一栏清零——那会连别的活已经传上去的卷一起抹掉。
    /// 记的是笔总量守恒的账：卷传完就加，件销账时按 uploaded 的增量减，那个增量恰好是刚归档
    /// 那件的全部卷，因此不必知道哪一卷属于哪一件。
    /// </summary>
    [Fact]
    public void One_Item_Finishing_Does_Not_Wipe_Another_Items_Uploaded_Volumes()
    {
        var (tracker, seen) = Rig();
        tracker.SetTransferred(0);

        void Volume(string name, string label, long size)
        {
            tracker.BeginItem(name, label, size);
            tracker.ItemProgress(name).Report(size);
            tracker.EndItem(name, 0);
        }

        Volume("a.001", "a.bin", 500);    // A 的前半
        Volume("b.001", "b.bin", 1_000);  // B 的前半（并发）
        Volume("a.002", "a.bin", 500);    // A 齐了
        tracker.Advance(0, 900);
        tracker.SetTransferred(1_000);    // A 销账：1000 = A 的两卷
        tracker.Complete();

        Assert.Equal(1_000, seen[^1].TransferredBytes);
        Assert.Equal(1_000, seen[^1].UnfinishedItemBytes);  // 剩下的正是 B 已传的那一卷，没被误伤

        Volume("b.002", "b.bin", 1_000);
        tracker.Advance(0, 2_500);
        tracker.SetTransferred(3_000);
        tracker.Complete();

        Assert.Equal(3_000, seen[^1].TransferredBytes);
        Assert.Equal(0, seen[^1].UnfinishedItemBytes);
    }

    /// <summary>
    /// if-missing 撞上已存在的 blob 时一个字节都没上网线，件级账也不会计它——这一栏同样不能加，
    /// 否则它永远减不回 0，界面上会挂着一笔根本不存在的"已上传"。中断重跑时这种命中成片发生。
    /// </summary>
    [Fact]
    public void A_Skipped_Blob_Never_Enters_The_Unfinished_Column()
    {
        var (tracker, seen) = Rig();
        tracker.SetTransferred(0);

        tracker.BeginItem("d.001", "a.bin", 5_000);   // 申报了 5000，但一个字节也没传
        tracker.EndItem("d.001", 0);
        tracker.Complete();

        Assert.Equal(0, seen[^1].UnfinishedItemBytes);
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
