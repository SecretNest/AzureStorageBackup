using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public sealed class RetryPolicyTests
{
    private static readonly RetryOptions Fast =
        new() { MaxAttempts = 5, BaseDelay = TimeSpan.FromMilliseconds(1) };

    [Fact]
    public async Task Returns_Result_On_First_Success()
    {
        var attempts = 0;

        var result = await RetryPolicy.ExecuteAsync(_ =>
        {
            attempts++;
            return Task.FromResult(42);
        }, Fast);

        Assert.Equal(42, result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Retries_Transient_Failures_Then_Succeeds()
    {
        var attempts = 0;

        var result = await RetryPolicy.ExecuteAsync(_ =>
        {
            if (++attempts < 3)
                throw new InvalidOperationException("transient");
            return Task.FromResult("ok");
        }, Fast);

        Assert.Equal("ok", result);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Throws_After_Exhausting_Attempts()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RetryPolicy.ExecuteAsync(_ =>
            {
                attempts++;
                throw new InvalidOperationException("always");
            }, Fast));

        Assert.Equal(5, attempts);
    }

    [Fact]
    public async Task Non_Transient_Exception_Is_Not_Retried()
    {
        var attempts = 0;

        await Assert.ThrowsAsync<ArgumentException>(() =>
            RetryPolicy.ExecuteAsync(_ =>
            {
                attempts++;
                throw new ArgumentException("fatal");
            }, Fast, isTransient: ex => ex is not ArgumentException));

        Assert.Equal(1, attempts);
    }

    // --- 退避序列（PRD 4.1：5s、30s、90s、300s，之后每 300s，总上限 2h）---

    private static RetryOptions PrdBackoff() => new()
    {
        Backoff =
        [
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(300),
        ],
        SteadyInterval = TimeSpan.FromSeconds(300),
        MaxTotalDelay = TimeSpan.FromHours(2),
    };

    [Fact]
    public void DelaySchedule_Starts_With_Explicit_Sequence()
    {
        var schedule = RetryPolicy.DelaySchedule(PrdBackoff()).ToList();

        Assert.Equal(TimeSpan.FromSeconds(5), schedule[0]);
        Assert.Equal(TimeSpan.FromSeconds(30), schedule[1]);
        Assert.Equal(TimeSpan.FromSeconds(90), schedule[2]);
        Assert.Equal(TimeSpan.FromSeconds(300), schedule[3]);
    }

    [Fact]
    public void DelaySchedule_Repeats_Steady_Interval_After_Sequence()
    {
        var schedule = RetryPolicy.DelaySchedule(PrdBackoff()).ToList();

        Assert.All(schedule.Skip(4), d => Assert.Equal(TimeSpan.FromSeconds(300), d));
    }

    [Fact]
    public void DelaySchedule_Is_Capped_By_Total_Delay()
    {
        var schedule = RetryPolicy.DelaySchedule(PrdBackoff()).ToList();

        var total = schedule.Aggregate(TimeSpan.Zero, (a, d) => a + d);
        Assert.True(total <= TimeSpan.FromHours(2), $"total {total} exceeds cap");
        // 再加一个稳定间隔就会超过上限 → 序列已尽可能长。
        Assert.True(total + TimeSpan.FromSeconds(300) > TimeSpan.FromHours(2));
    }

    [Fact]
    public void DelaySchedule_Count_Mode_Yields_MaxAttempts_Minus_One_Retries()
    {
        var schedule = RetryPolicy.DelaySchedule(
            new RetryOptions { MaxAttempts = 5, BaseDelay = TimeSpan.FromMilliseconds(1) }).ToList();

        // 5 次尝试 = 首次 + 4 次重试。
        Assert.Equal(4, schedule.Count);
    }
}
