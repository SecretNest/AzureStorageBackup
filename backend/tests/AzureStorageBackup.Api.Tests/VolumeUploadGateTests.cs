using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 上传额度闸门的仲裁规则。这些用例守的不是性能，是**中断时会白扔掉多少活**：
/// 整族卷传完、云端确认返回之后才记 journal、才销在途账，所以「同时半完成的件数」直接决定
/// 一次 <c>Stop now</c> / 挂起 / 崩溃丢掉多少已经压好传上去的字节。
/// <para>
/// 先到先得会让额度摊薄到所有在传的件上（压缩全局串行，稳态是 1 件在压 + N 件在传），
/// N 件同时半完成；按件龄仲裁把这个数压到通常 1~2 件。
/// </para>
/// </summary>
public sealed class VolumeUploadGateTests
{
    /// <summary>
    /// 这一条是整个改动的存在理由：**后到的老件先拿到额度**。
    /// 新件（票号大）先排上队，老件（票号小）后到，释放一份额度时必须落到老件头上。
    /// </summary>
    [Fact]
    public async Task An_Older_Item_Wins_The_Slot_Even_Though_It_Asked_Later()
    {
        var gate = new VolumeUploadGate(1);
        // 唯一那份额度先占住，后面两个都只能排队。
        await gate.AcquireAsync(ticket: 0, volume: 0, CancellationToken.None);

        var newer = gate.AcquireAsync(ticket: 9, volume: 0, CancellationToken.None);
        var older = gate.AcquireAsync(ticket: 2, volume: 0, CancellationToken.None);
        Assert.False(newer.IsCompleted);
        Assert.False(older.IsCompleted);

        gate.Release();

        await older.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(newer.IsCompleted);   // 新件继续等，尽管它先开的口

        gate.Release();
        await newer.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// 同一件之内按卷号升序。不是可有可无的整齐：界面上那张在途列表照这个顺序读，
    /// 一件一件顺着往下推进才看得懂。
    /// </summary>
    [Fact]
    public async Task Within_One_Item_The_Lowest_Volume_Number_Goes_First()
    {
        var gate = new VolumeUploadGate(1);
        await gate.AcquireAsync(ticket: 0, volume: 0, CancellationToken.None);

        var third = gate.AcquireAsync(ticket: 7, volume: 3, CancellationToken.None);
        var first = gate.AcquireAsync(ticket: 7, volume: 1, CancellationToken.None);
        var second = gate.AcquireAsync(ticket: 7, volume: 2, CancellationToken.None);

        gate.Release();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);

        gate.Release();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(third.IsCompleted);

        gate.Release();
        await third.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// 额度不能超发：任何时刻在跑的不超过容量。
    /// <para>
    /// 刻意**不**开一堆并发任务去压它——这个测试类是和一批有墙钟预算的集成用例并行跑的，
    /// 在这里抢线程池只会把邻居挤红，测出来的还不是这里想测的东西。改成单线程按序驱动：
    /// 领满、再多要几份、逐份放行，每一步都断言账面。同样钉得住超发，而且完全确定。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Never_Hands_Out_More_Slots_Than_Its_Capacity()
    {
        const int capacity = 3;
        var gate = new VolumeUploadGate(capacity);

        // 先把容量领满：这几份都该当场到手。
        for (var i = 0; i < capacity; i++)
            Assert.True(gate.AcquireAsync(ticket: i, volume: 0, CancellationToken.None).IsCompletedSuccessfully);
        Assert.Equal(0, gate.Free);

        // 超出容量的一律排队，一份都不许多发。
        var queued = Enumerable.Range(capacity, 5)
            .Select(i => gate.AcquireAsync(ticket: i, volume: 0, CancellationToken.None))
            .ToList();
        Assert.All(queued, t => Assert.False(t.IsCompleted));

        // 逐份放行：每放一份，恰好一个等待者转正，其余照旧等着。
        for (var i = 0; i < queued.Count; i++)
        {
            gate.Release();
            await queued[i].WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, gate.Free);
            Assert.All(queued.Skip(i + 1), t => Assert.False(t.IsCompleted));
        }

        // 手上还攥着 capacity 份（发出去 capacity + 5，已还 5），全部归还后额度回到满值——一份都没漏。
        for (var i = 0; i < capacity; i++)
            gate.Release();
        Assert.Equal(capacity, gate.Free);
    }

    /// <summary>
    /// 取消掉的等待者不许吃掉额度。它取消之后仍躺在优先队列里，放行时必须被跳过、
    /// 额度落到下一个活着的等待者身上——否则一次取消就永久漏掉一条流。
    /// </summary>
    [Fact]
    public async Task A_Cancelled_Waiter_Does_Not_Consume_The_Slot_It_Was_Queued_For()
    {
        var gate = new VolumeUploadGate(1);
        await gate.AcquireAsync(ticket: 0, volume: 0, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        // 票号最小 = 优先级最高，所以它一定是放行时第一个被弹出来的那个。
        var doomed = gate.AcquireAsync(ticket: 1, volume: 0, cts.Token);
        var survivor = gate.AcquireAsync(ticket: 5, volume: 0, CancellationToken.None);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doomed);

        gate.Release();
        await survivor.WaitAsync(TimeSpan.FromSeconds(5));

        gate.Release();
        Assert.Equal(1, gate.Free);
    }

    /// <summary>
    /// 全体等待者都取消之后，队里剩的全是尸体。额度必须回到满值，而且**后来的人不能被尸体堵死**——
    /// 这正是「弹一个就收手」那种写法会踩的死锁：有空额度、队非空、可谁都拿不到。
    /// </summary>
    [Fact]
    public async Task A_Queue_Full_Of_Cancelled_Waiters_Does_Not_Wedge_The_Gate()
    {
        var gate = new VolumeUploadGate(1);
        await gate.AcquireAsync(ticket: 0, volume: 0, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var dead = Enumerable.Range(1, 5)
            .Select(i => gate.AcquireAsync(ticket: i, volume: 0, cts.Token))
            .ToList();

        await cts.CancelAsync();
        foreach (var d in dead)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => d);

        gate.Release();
        Assert.Equal(1, gate.Free);

        // 尸体还躺在队里；后来者必须照样拿得到。
        var latecomer = gate.AcquireAsync(ticket: 99, volume: 0, CancellationToken.None);
        await latecomer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, gate.Free);
    }

    /// <summary>
    /// 闸门空着时不该排队——调用方正是靠「返回的 Task 已完成」来决定不报「在等额度」的，
    /// 一件大活上千卷，报一次就是上千次强制发布。
    /// </summary>
    [Fact]
    public void An_Empty_Gate_Hands_The_Slot_Over_Synchronously()
    {
        var gate = new VolumeUploadGate(2);
        Assert.True(gate.AcquireAsync(ticket: 1, volume: 0, CancellationToken.None).IsCompletedSuccessfully);
        Assert.True(gate.AcquireAsync(ticket: 2, volume: 0, CancellationToken.None).IsCompletedSuccessfully);
        Assert.False(gate.AcquireAsync(ticket: 3, volume: 0, CancellationToken.None).IsCompleted);
    }

    /// <summary>票号严格递增，否则「谁更老」就无从谈起。</summary>
    [Fact]
    public void Tickets_Are_Handed_Out_In_Increasing_Order()
    {
        var gate = new VolumeUploadGate(1);
        var tickets = Enumerable.Range(0, 100).Select(_ => gate.NextTicket()).ToList();
        Assert.Equal(tickets.OrderBy(t => t), tickets);
        Assert.Equal(100, tickets.Distinct().Count());
    }

    /// <summary>
    /// 重复归还要当场炸，不能默默把额度变多。被换掉的 <c>SemaphoreSlim</c> 有这道保险
    /// （<c>SemaphoreFullException</c>），换实现时不能把它弄丢：额度凭空变多的后果是在途流数
    /// 静静超过用户设的并发数，而这种坏法不响。
    /// </summary>
    [Fact]
    public void Releasing_More_Than_Was_Acquired_Throws_Instead_Of_Inflating_The_Capacity()
    {
        var gate = new VolumeUploadGate(2);
        Assert.True(gate.AcquireAsync(1, 0, CancellationToken.None).IsCompletedSuccessfully);
        gate.Release();
        Assert.Equal(2, gate.Free);
        Assert.Throws<InvalidOperationException>(gate.Release);
        Assert.Equal(2, gate.Free);
    }
}
