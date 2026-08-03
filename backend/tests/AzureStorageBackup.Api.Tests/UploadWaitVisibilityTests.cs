using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 压完之后、开始推字节之前那一段，从前在界面上完全隐身：件既不在 <c>preparing</c>（那只数拿到
/// 压缩锁的）、不在 <c>queued</c>（那只数还没被领走或在排压缩锁的）、也不在 <c>uploading</c>
/// （那数的是在途的**卷**）。<see cref="StageTracker"/> 其实一直记着 <c>_inUpload</c>，
/// 但发布快照时从来没读过它。
/// <para>
/// 后果是实打实的：一件活在这一段里卡了几分钟，屏幕上显示的是
/// <c>5,345 of 6,378 objects · nothing on the wire right now · 1 preparing · 1,031 queued</c>——
/// 三个数加起来是 6,377，少的那一件就是卡住的那个，而屏幕上没有任何一栏在说它。
/// 操作员只能靠把三屏截图排在一起做减法才能发现它存在。
/// </para>
/// <para>
/// 所以两件事都要做到：件数账必须能平；而且要说得出它在**等什么**——等同批同内容的首个上传者、
/// 等全局上传闸门、还是等云端应答，这三段的处置完全不同。
/// </para>
/// </summary>
public sealed class UploadWaitVisibilityTests
{
    /// <summary>
    /// 件数账要能平：完工 + 在压 + 排队 + 已进上传段 = 总数。这条是操作员唯一能用来判断
    /// 「是不是有活凭空消失了」的等式，屏幕上那几个数必须凑得出它。
    /// </summary>
    [Fact]
    public void Counts_Add_Up_While_An_Item_Sits_Between_Compression_And_The_Wire()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 3, seen.Add, speedWhileInFlight: true);

        // 三件活入队；一件压完并走完，一件正占着压缩锁，一件压完了在等上传。
        tracker.Enqueue();
        tracker.Enqueue();
        tracker.Enqueue();

        tracker.Advance(100);          // 第一件：完工

        tracker.BeginWork();           // 第二件：领走 → 进暂存段 → 拿到压缩锁
        tracker.BeginStaging();
        tracker.BeginPacking();

        tracker.BeginWork();           // 第三件：领走 → 压完出了暂存段 → 进上传段但还没有卷在飞
        tracker.BeginStaging();
        tracker.EndStaging();
        tracker.BeginUpload();

        tracker.Complete();

        var s = seen[^1];
        Assert.Equal(1, s.Processed);
        Assert.Equal(1, s.Preparing);
        Assert.Equal(1, s.Uploading);   // ← 这一栏从前根本不发布
        Assert.Equal(3, s.Processed + s.Preparing + s.Queued + s.Uploading);
    }

    /// <summary>在途的卷不该让「已进上传段」的件重复计一遍——那样账又会多出来。</summary>
    [Fact]
    public void An_Item_With_Volumes_On_The_Wire_Is_Still_One_Item()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add, speedWhileInFlight: true);

        tracker.Enqueue();
        tracker.BeginWork();
        tracker.BeginUpload();
        // 一件活可以同时有好几卷在飞（MaxParallelPerItem）——件还是那一件。
        tracker.BeginItem("data/abc.002", "photo.raw (2/9)", 1024);
        tracker.BeginItem("data/abc.003", "photo.raw (3/9)", 1024);
        tracker.Complete();

        var s = seen[^1];
        Assert.Equal(1, s.Uploading);
        Assert.Equal(2, s.ActiveItems.Count);
        Assert.Equal(1, s.Processed + s.Preparing + s.Queued + s.Uploading);
    }

    [Theory]
    [InlineData(UploadWait.Peer)]
    [InlineData(UploadWait.Slot)]
    public void The_Reason_An_Item_Is_Waiting_Reaches_The_Snapshot(UploadWait kind)
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add, speedWhileInFlight: true);

        tracker.BeginWait(kind);
        var waiting = seen[^1];
        tracker.EndWait(kind);
        tracker.Complete();

        Assert.Equal(1, waiting.Waiting(kind));
        Assert.Equal(0, seen[^1].Waiting(kind));
    }

    /// <summary>
    /// 进入等待这件事必须**当场**发出去，不能被 200ms 节流吞掉。
    /// <para>
    /// 这不是锦上添花：等待期间本调用方不再产生任何事件，而心跳只在有流在传时才跑
    /// （<see cref="StageTracker.Tick"/> 在虚拟时钟冻着时直接返回）。零流在传 + 被吞掉的那一次
    /// 发布 = 界面冻在旧快照上直到等待结束——正是这一轮要修的那个「几分钟纹丝不动」。
    /// </para>
    /// </summary>
    [Fact]
    public void Entering_A_Wait_Is_Published_Immediately_Even_Inside_The_Throttle_Window()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 1, seen.Add, speedWhileInFlight: true);

        tracker.Advance(1);            // 刚发布过一次，节流窗口正开着
        var before = seen.Count;

        tracker.BeginWait(UploadWait.Slot);

        Assert.True(seen.Count > before, "entering a wait must publish immediately, not wait out the throttle");
        Assert.Equal(1, seen[^1].Waiting(UploadWait.Slot));
    }

    /// <summary>
    /// 闸门满了才算「在等额度」。正常情况下额度随手就拿到，标记它等于给每一卷平白加一次
    /// 强制发布——一件大活上千卷，那是上千次。
    /// </summary>
    [Fact]
    public async Task Waiting_On_The_Upload_Slot_Is_Only_Reported_When_The_Gate_Is_Actually_Full()
    {
        var seen = new List<StageProgress>();
        var tracker = new StageTracker("Uploading", total: 2, seen.Add, speedWhileInFlight: true);
        using var gate = new SemaphoreSlim(1, 1);
        var scope = new VolumeUploadScope(gate, tracker, maxParallelPerItem: 5);

        // 闸门空着：拿额度不该产生任何 "waiting" 读数。
        await scope.RunAsync("data/a.001", _ => Task.CompletedTask, CancellationToken.None);
        Assert.All(seen, s => Assert.Equal(0, s.Waiting(UploadWait.Slot)));

        // 闸门被占满：这一次必须报出来，否则界面上又是「什么都没在传，也没说在等什么」。
        await gate.WaitAsync();
        var blocked = scope.RunAsync("data/b.001", _ => Task.CompletedTask, CancellationToken.None);

        // 让它跑到 gate.WaitAsync 上挂住。
        for (var i = 0; i < 100 && seen[^1].Waiting(UploadWait.Slot) == 0; i++)
            await Task.Delay(10);

        Assert.Equal(1, seen[^1].Waiting(UploadWait.Slot));

        gate.Release();
        await blocked;
        tracker.Complete();
        Assert.Equal(0, seen[^1].Waiting(UploadWait.Slot));
    }
}
