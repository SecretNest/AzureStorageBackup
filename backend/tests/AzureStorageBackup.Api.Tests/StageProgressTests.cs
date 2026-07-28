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
        // ……其中一件已经过完暂存区、进了上传段
        tracker.BeginStaging();
        tracker.BeginPacking();
        tracker.EndPacking();
        tracker.EndStaging();
        tracker.BeginUpload();
        tracker.BeginItem("packs/p0001.7z"); // 在途登记的是**卷**
        // 另一件正占着压缩锁在产出
        tracker.BeginStaging();
        tracker.BeginPacking();
        tracker.Complete();

        var s = seen[^1];
        Assert.Equal(["packs/p0001.7z"], s.ActiveItems);
        Assert.Equal(1, s.Preparing); // 正占着压缩锁的那一件
        Assert.Equal(3, s.Queued);    // 入队 5 - 完成 0 - 手上 2
        Assert.Equal(0, s.Processed); // 这些记账一律**不**计数
    }

    /// <summary>
    /// "在准备"永远不会超过 1：<see cref="StagingArea"/> 里有一把全局压缩锁，同一时刻只有一件活
    /// 在产出。工作线程池开得比它大（<c>UploadConcurrency + 1</c>）是为了让压完的活各自去占一条
    /// 上传流，不是为了并行压缩。
    /// <para>
    /// 从前这个数是 <c>手上件数 - 在上传件数</c> 反推的，于是把「排在压缩锁后面干等」的线程也算成
    /// 了"在准备"：默认配置下界面会显示 5 preparing，读起来像五件活在并行推进，实际是一件在压、
    /// 四个线程闲着。它同时也是**压缩就是瓶颈**这个结论的反面证据——看起来越忙，其实越闲。
    /// </para>
    /// </summary>
    [Fact]
    public void Preparing_Never_Exceeds_The_One_Item_Holding_The_Compress_Lock()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add);

        for (var i = 0; i < 3; i++)
            tracker.BeginWork();  // 三件活在工作线程手上
        // 一件在产出，另两件在排压缩锁
        tracker.BeginStaging();
        tracker.BeginPacking();
        tracker.BeginStaging();
        tracker.BeginStaging();
        tracker.BeginUpload();    // 还有一件已经进了上传段……
        for (var i = 1; i <= 5; i++)
            tracker.BeginItem($"data/big.{i:000}"); // ……它自己就有 5 卷同时在传
        tracker.Complete();

        var s = seen[^1];
        Assert.Equal(5, s.ActiveItems.Count); // "N uploading" 回答的是"网线上有几条流"
        Assert.Equal(1, s.Preparing);         // 与在手件数、卷数都无关：压缩是串行的
    }

    /// <summary>
    /// 排在压缩锁后面干等的活算 <c>queued</c>。从用户的角度它们和还在队列里没被领走的活没有区别，
    /// 都是"排着队还没开工"——而把它们算进"在准备"会让界面显示成一堆活在并行推进。
    /// </summary>
    [Fact]
    public void Items_Waiting_For_The_Compress_Lock_Count_As_Queued()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add);

        for (var i = 0; i < 3; i++)
            tracker.Enqueue();
        for (var i = 0; i < 3; i++)
        {
            tracker.BeginWork();     // 三件全被领走，队列里一件不剩
            tracker.BeginStaging();
        }
        tracker.BeginPacking();      // 只有一件拿到了锁
        tracker.Complete();

        var s = seen[^1];
        Assert.Equal(1, s.Preparing); // 真正在产出的
        Assert.Equal(2, s.Queued);    // 另两件在干等锁——队列里虽然空了，它们仍是"没开工"
    }

    /// <summary>
    /// 压完到开始上传之间还有一段实打实的活：pack 要逐成员重新 <c>Stat</c>（变了的还得重算 hash），
    /// 单文件要查去重映射，去重命中的甚至根本不上传。这段活**不能**被算成 queued——
    /// 把正在干活的报成"排队中"，比原先那个虚高的 preparing 更误导。
    /// </summary>
    [Fact]
    public void Post_Packing_Verification_Is_Not_Reported_As_Queued()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add);

        tracker.Enqueue();
        tracker.Enqueue();
        tracker.BeginWork();
        tracker.BeginStaging();
        tracker.BeginPacking();
        tracker.EndPacking();
        tracker.EndStaging();  // 压完了，正在逐成员校验/查去重，还没进上传段
        tracker.Complete();

        var s = seen[^1];
        Assert.Equal(0, s.Preparing); // 压缩锁已经交出去了
        Assert.Equal(1, s.Queued);    // 只有还在队列里那一件，不是 2
    }

    /// <summary>每卷各要一个 <c>ItemProgress</c>：DeltaProgress 的累计基线是 per-call 的，
    /// 多卷并行共用一个实例，彼此的累计值会被当成对方的"回退"而重复计入。</summary>
    [Fact]
    public void Parallel_Volumes_Each_Get_Their_Own_Progress_Baseline()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add);

        var v1 = tracker.ItemProgress();
        var v2 = tracker.ItemProgress();
        // 两卷交错着各自从 0 涨到 100。
        v1.Report(40);
        v2.Report(60);
        v1.Report(100);
        v2.Report(100);
        tracker.Complete();

        Assert.Equal(200, seen[^1].Bytes);
    }

    /// <summary>队列深度必须能归零。这些计数不配对（比如失败路径漏了 finally）
    /// 会让界面永远挂着几件"在准备"或"在排队"，而那时候其实什么都没在跑。</summary>
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
            tracker.BeginStaging();
            tracker.BeginPacking();
            tracker.EndPacking();
            tracker.EndStaging();
            tracker.Advance(10);
            tracker.EndWork();
        }
        tracker.Complete();

        Assert.Equal(0, seen[^1].Queued);
        Assert.Equal(0, seen[^1].Preparing);
        Assert.Equal(3, seen[^1].Processed);
    }

    /// <summary>几个计数各自独立推进，读到的必然是错开半拍的快照——消费者抢在入队记账落地之前
    /// 领走一件活是完全正常的时序。不夹到 0 以上，界面上就会闪出 "-1 queued"。</summary>
    [Fact]
    public void Skewed_Counters_Never_Produce_Negative_Numbers()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add);

        tracker.BeginWork(); // 入队还没落账，活已经被领走了
        tracker.Complete();

        Assert.Equal(0, seen[^1].Queued);    // 入队 0 - 手上 1 是负的，夹到 0
        Assert.Equal(0, seen[^1].Preparing); // 还没拿到压缩锁
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

    // ---- 剩余时间 ----
    //
    // 用户实测反馈：上传速度起伏很大，剩余时间跟着乱跳。根因是它从前拿 10 秒滚动窗口的速度当分母，
    // 而备份的节奏是「压一箱几十秒 → 传几秒」：压缩期间窗口里一个字节都没有，速度归零，剩余时间
    // 整段消失；压完又猛地冒出一个很小的数。压缩那几十秒明明也是剩余时间的一部分。

    /// <summary>压缩把网速摁到 0 的那几十秒里，剩余时间不许消失——它恰恰是最需要它的时候。</summary>
    [Fact]
    public async Task Remaining_Time_Survives_The_Stretches_Where_Nothing_Is_On_The_Wire()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add);

        for (var i = 0; i < 4; i++) tracker.Enqueue(1_000_000); // 四件活，每件 1 MB 原始字节
        tracker.SetTotal(4);
        tracker.BeginWork();
        await Task.Delay(60);
        tracker.Advance(0, work: 1_000_000); // 一件完工。字节走 ItemProgress，这里一个都不加
        await Task.Delay(250);               // 越过节流，逼出一次带最新状态的上报
        tracker.Advance(0, work: 1_000_000);

        var last = seen[^1];
        Assert.Equal(0, last.BytesPerSecond);      // 测速窗口里确实一个字节都没有
        Assert.NotNull(last.EstimatedRemaining);   // 但剩余时间照给
        Assert.True(last.EstimatedRemaining > TimeSpan.Zero);
    }

    /// <summary>一件活可能是 100 GB 的单文件，也可能是一箱几百个 5 KB 的小文件。上传阶段按
    /// **原始字节**外推正是为了别把这两者当成一样重：干完 1 件却已经过掉 90% 的字节时，
    /// 剩余时间该按字节说话，而不是说"还剩 3/4"。</summary>
    [Fact]
    public async Task Upload_Estimates_By_Bytes_Not_By_Item_Count()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add);

        tracker.Enqueue(900);                                  // 一个大件
        for (var i = 0; i < 3; i++) tracker.Enqueue(100 / 3 + 1); // 三个小件，合计约 100
        tracker.SetTotal(4);
        tracker.BeginWork();
        await Task.Delay(300);
        tracker.Advance(0, work: 900); // 大件完工：件数才 1/4，字节已经 9/10

        var eta = seen[^1].EstimatedRemaining;
        Assert.NotNull(eta);
        // 按字节：剩下约 1/9 的时间。按件数会是 3 倍已用时间，差着一个数量级。
        Assert.True(eta < TimeSpan.FromMilliseconds(200), $"expected a byte-weighted estimate, got {eta}");
    }

    /// <summary>没申报工作量的阶段（diff/还原/检查）退回按件数外推——同样是全程平均，
    /// 同样不看瞬时速度。diff 那边件数才是对的代理：绝大多数条目只 stat 一下就过去了。</summary>
    [Fact]
    public async Task Stages_Without_A_Declared_Workload_Fall_Back_To_Counting_Items()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Diffing", total: 4, seen.Add);

        await Task.Delay(60);
        tracker.Advance(0); // 一个没变的文件：一个字节都没读，测速窗口里空空如也
        await Task.Delay(250);
        tracker.Advance(0);

        Assert.Equal(0, seen[^1].BytesPerSecond);
        Assert.NotNull(seen[^1].EstimatedRemaining);
    }

    /// <summary>全部干完之后必须收成 null：留一个"还剩 0 秒"挂在界面上，比不显示更像卡住了。</summary>
    [Fact]
    public void No_Remaining_Time_Once_Everything_Is_Done()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 0, seen.Add);

        tracker.Enqueue(1000);
        tracker.Enqueue(1000);
        tracker.SetTotal(2);
        tracker.Advance(0, work: 1000);
        tracker.Advance(0, work: 1000);
        tracker.Complete();

        Assert.Null(seen[^1].EstimatedRemaining);
    }

    /// <summary>工作量只影响剩余时间，不许渗进 <c>Bytes</c>——那个数是"真正过了网线的字节"，
    /// 界面上的速度和累计流量都指着它。混进原始字节，去重命中就会显示成传了一堆数据。</summary>
    [Fact]
    public void Declared_Workload_Never_Leaks_Into_The_Transferred_Byte_Count()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add);

        tracker.Enqueue(5_000_000);
        tracker.Advance(0, work: 5_000_000); // 去重命中：原始 5 MB，网线上 0 字节
        tracker.Complete();

        Assert.Equal(0, seen[^1].Bytes);
    }

    /// <summary>
    /// 备份上传的节奏是「压一箱几十秒 → 传几秒」。测速窗口过去按墙钟打时间戳，于是同一条网线
    /// 量出来的数字随停顿长短而变：停顿短于窗口被稀释，长于窗口则老采样被整批淘汰、当场报 0。
    /// 速度要回答的是"网线上有多快"，压缩那几十秒就不该进分母。
    /// </summary>
    [Fact]
    public void Compression_Stalls_Do_Not_Dilute_The_Upload_Speed()
    {
        long now = 0;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 2, seen.Add, speedWhileInFlight: true)
        {
            Clock = () => now,
        };

        // 第一卷：1 MB 用掉 1 秒。
        tracker.BeginItem("v1");
        var first = tracker.ItemProgress();
        now += 1_000;
        first.Report(1 << 20);
        tracker.EndItem("v1", 0);

        // 压缩 30 秒——一条流都没开着。这 30 秒不该进分母。
        now += 30_000;

        // 第二卷：又是 1 MB 用掉 1 秒。
        tracker.BeginItem("v2");
        var second = tracker.ItemProgress();
        now += 1_000;
        second.Report(1 << 20);
        tracker.EndItem("v2", 0);

        // 2 MB / 2 秒在网线上 ≈ 1 MB/s。被 30 秒摊薄的话是 64 KB/s，老采样被淘汰的话是 0。
        Assert.InRange(seen[^1].BytesPerSecond, 900_000L, 1_150_000L);
    }

    /// <summary>
    /// 开关默认关：扫描、差分这些阶段从不登记在途项，虚拟时钟对它们会永远停在 0，
    /// 速度将恒为 0。它们必须原样走墙钟。
    /// </summary>
    [Fact]
    public void Stages_Without_In_Flight_Items_Keep_The_Wall_Clock_Speed()
    {
        long now = 0;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Diffing", total: 2, seen.Add) { Clock = () => now };

        tracker.Advance(1 << 20);
        now += 1_000;
        tracker.Advance(1 << 20);

        Assert.InRange(seen[^1].BytesPerSecond, 900_000L, 1_150_000L);
    }

    /// <summary>
    /// 流开着却一个字节都不动（网络卡死、SDK 没触发重试）时，没有任何事件会触发上报，
    /// 界面就冻在卡住前的数字上——最该看出问题的时候反而看不出来。
    /// 活跃段内的心跳负责把测速窗口推下去，让速度自己掉到 0。
    /// </summary>
    [Fact]
    public void A_Stuck_Stream_Drags_The_Speed_Down_Instead_Of_Freezing_It()
    {
        long now = 0;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add, speedWhileInFlight: true)
        {
            Clock = () => now,
        };

        tracker.BeginItem("v1");
        var bytes = tracker.ItemProgress();
        now += 1_000;
        bytes.Report(4 << 20);
        now += 1_000;
        bytes.Report(8 << 20);   // 累计值：又是 4 MB
        Assert.True(seen[^1].BytesPerSecond > 0, "流通着的时候要看得见速度");

        // 流还挂着，字节不动。心跳每秒一拍。
        for (var i = 0; i < 12; i++)
        {
            now += 1_000;
            tracker.Tick();
        }

        Assert.Equal(0, seen[^1].BytesPerSecond);
    }

    /// <summary>
    /// 纯压缩期一条流都没开：那段时间不进分母，也就没有任何新东西可报。
    /// 心跳必须闭嘴，否则几十秒一箱的压缩会刷出一串内容完全相同的快照。
    /// </summary>
    [Fact]
    public void The_Heartbeat_Stays_Silent_While_Nothing_Is_On_The_Wire()
    {
        long now = 0;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add, speedWhileInFlight: true)
        {
            Clock = () => now,
        };

        tracker.BeginWork();   // 领了活，但还在压缩：一条流都没开
        seen.Clear();

        for (var i = 0; i < 5; i++)
        {
            now += 1_000;
            tracker.Tick();
        }

        Assert.Empty(seen);
    }

    /// <summary>
    /// 并发上传是生产默认场景：多条卷同时在飞。先结束的那一卷不该把测速时钟叫停——
    /// 只要还有另一条流没收口，这段时间依然要算进测速窗口。<see cref="EndItem"/> 里的
    /// <c>_active.IsEmpty</c> 判断正是为了这个：只有"最后一条"收工才停表。
    /// 只测严格先后的 Begin/End 对（现有测试都是这样）盖不到这条分支——那种写法即使把
    /// IsEmpty 判断整个删掉，串行场景照样算得对，删掉它才会露馅的正是这里的重叠场景。
    /// </summary>
    [Fact]
    public void An_Overlapping_Upload_Keeps_The_Clock_Running_Until_The_Last_Volume_Ends()
    {
        long now = 0;
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 2, seen.Add, speedWhileInFlight: true)
        {
            Clock = () => now,
        };

        tracker.BeginItem("a");
        tracker.BeginItem("b"); // b 开的时候 a 还没收工，两条流重叠在一起

        var aProgress = tracker.ItemProgress();
        var bProgress = tracker.ItemProgress();

        now += 1_000;
        aProgress.Report(1 << 20); // a 传了 1 MB，用掉 1 秒
        tracker.EndItem("a", 0);   // a 收工，但 b 还在飞——时钟不该停

        now += 1_000;
        bProgress.Report(1 << 20); // b 又传了 1 MB，用掉 1 秒。若时钟被 a 的收工叫停，
                                    // 这一拍会被记成与上一拍相同的时刻，测出来的速度就是 0。
        tracker.EndItem("b", 0);   // 最后一条流收工，时钟才真正停下

        // 2 MB 在 2 秒里过了网线 ≈ 1 MB/s。时钟被提前叫停的话，第二拍会撞上第一拍的时间戳，
        // spanMs 算出 0，速度读数变成 0——正是 IsEmpty 判断要防的那种假象。
        Assert.InRange(seen[^1].BytesPerSecond, 900_000L, 1_150_000L);
    }
}
