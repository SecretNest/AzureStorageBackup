using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class PauseGateTests
{
    private static PauseGate Fast(TimeSpan? patience = null) => new(
        schedule: [TimeSpan.FromMilliseconds(10)],
        steady: TimeSpan.FromMilliseconds(10),
        patience: patience ?? TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Self_heal_timer_releases_the_waiter()
    {
        using var gate = Fast();
        Assert.True(await gate.WaitAsync(new IOException("blip"), default));
        Assert.Null(gate.Current);
    }

    [Fact]
    public async Task Exposes_why_it_is_paused_while_waiting()
    {
        using var gate = new PauseGate(
            schedule: [TimeSpan.FromSeconds(30)], steady: TimeSpan.FromSeconds(30),
            patience: TimeSpan.FromMinutes(10));
        var waiting = gate.WaitAsync(new IOException("network down"), default);

        // 等它把状态立起来（开闸是同步做的，但等待者还没跑到 await）
        for (var i = 0; i < 200 && gate.Current is null; i++)
            await Task.Delay(5);

        Assert.Equal("network down", gate.Current!.Reason);
        Assert.Equal(1, gate.Current.Failures);
        Assert.NotNull(gate.Current.NextRetryAt);

        gate.ReleaseNow();
        Assert.True(await waiting);
    }

    [Fact]
    public async Task Manual_push_releases_immediately()
    {
        using var gate = new PauseGate(
            schedule: [TimeSpan.FromMinutes(5)], steady: TimeSpan.FromMinutes(5),
            patience: TimeSpan.FromHours(1));
        var waiting = gate.WaitAsync(new IOException("blip"), default);
        gate.ReleaseNow();
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task All_waiters_are_released_together()
    {
        using var gate = Fast();
        var a = gate.WaitAsync(new IOException("blip"), default);
        var b = gate.WaitAsync(new IOException("blip"), default);
        var c = gate.WaitAsync(new IOException("blip"), default);
        Assert.Equal(new[] { true, true, true }, await Task.WhenAll(a, b, c));
    }

    // 耐心用尽 -> 降级。调用方据此走挂起退出，而不是继续傻等。
    [Fact]
    public async Task Downgrades_when_patience_runs_out()
    {
        using var gate = Fast(patience: TimeSpan.Zero);
        Assert.False(await gate.WaitAsync(new IOException("blip"), default));
        Assert.True(gate.IsDowngraded);
    }

    [Fact]
    public async Task Downgraded_gate_never_waits_again()
    {
        using var gate = Fast();
        gate.Downgrade();
        Assert.False(await gate.WaitAsync(new IOException("blip"), default));
    }

    // 别的工作者干成了活 -> 网络显然是通的 -> 失败计数清零，退避从头来，耐心也重新计时。
    [Fact]
    public async Task Success_resets_the_failure_count()
    {
        using var gate = Fast();
        Assert.True(await gate.WaitAsync(new IOException("blip"), default));
        Assert.True(await gate.WaitAsync(new IOException("blip"), default));

        gate.ReportSuccess();

        var waiting = gate.WaitAsync(new IOException("blip"), default);
        for (var i = 0; i < 200 && gate.Current is null; i++)
            await Task.Delay(1);
        // 计数清零之后这一次算"第一次出事"
        Assert.True(gate.Current is null || gate.Current.Failures == 1);
        Assert.True(await waiting);
    }

    // 用户按了取消：取消永远赢，闸门不能把它吞掉。
    [Fact]
    public async Task User_cancellation_wins_over_waiting()
    {
        using var gate = new PauseGate(
            schedule: [TimeSpan.FromMinutes(5)], steady: TimeSpan.FromMinutes(5),
            patience: TimeSpan.FromHours(1));
        using var cts = new CancellationTokenSource();
        var waiting = gate.WaitAsync(new IOException("blip"), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    // 5 分钟的定时器不能比运行活得还久。
    [Fact]
    public void Dispose_kills_the_pending_timer()
    {
        var gate = new PauseGate(
            schedule: [TimeSpan.FromMinutes(5)], steady: TimeSpan.FromMinutes(5),
            patience: TimeSpan.FromHours(1));
        _ = gate.WaitAsync(new IOException("blip"), default);
        gate.Dispose();
        Assert.True(gate.IsDowngraded);
    }
}
