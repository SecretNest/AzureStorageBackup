namespace AzureStorageBackup.Api.Services;

public sealed record RetryOptions
{
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    public double Factor { get; init; } = 2.0;
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 显式退避序列（PRD 4.1）。非空 → 使用序列模式：按序列逐次退避，序列耗尽后重复 <see cref="SteadyInterval"/>，
    /// 累计退避超过 <see cref="MaxTotalDelay"/> 即停止（放弃并抛出）。为空 → 使用指数退避 + <see cref="MaxAttempts"/> 计数封顶。
    /// </summary>
    public IReadOnlyList<TimeSpan>? Backoff { get; init; }

    /// <summary>序列耗尽后的固定重复间隔（默认取序列最后一项，即 PRD 的「之后每 300s」）。</summary>
    public TimeSpan? SteadyInterval { get; init; }

    /// <summary>序列模式下的累计退避总上限（PRD 默认 2h）。为空则不封顶（仅受安全上限约束）。</summary>
    public TimeSpan? MaxTotalDelay { get; init; }
}

/// <summary>指数退避重试（M4 §5、PRD 4.1）。isTransient 判定异常是否可重试，默认全部可重试。</summary>
public static class RetryPolicy
{
    // 序列模式无 MaxTotalDelay 时的安全上限，避免无限重试。
    private const int MaxSequenceRetries = 100_000;

    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        RetryOptions? options = null,
        Func<Exception, bool>? isTransient = null,
        CancellationToken ct = default)
    {
        options ??= new RetryOptions();
        using var delays = DelaySchedule(options).GetEnumerator();
        while (true)
        {
            try
            {
                return await action(ct);
            }
            catch (Exception ex) when (isTransient?.Invoke(ex) ?? true)
            {
                if (!delays.MoveNext())
                    throw;
                await Task.Delay(delays.Current, ct);
            }
        }
    }

    public static Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        RetryOptions? options = null,
        Func<Exception, bool>? isTransient = null,
        CancellationToken ct = default)
        => ExecuteAsync<object?>(async token => { await action(token); return null; }, options, isTransient, ct);

    /// <summary>
    /// 生成每次重试前的退避时长序列（首次尝试不计）。产出的元素个数 = 允许的重试次数。
    /// 序列模式：按 Backoff 逐项、耗尽后重复 SteadyInterval，累计不超过 MaxTotalDelay。
    /// 计数模式：指数退避，产出 MaxAttempts-1 项（对应 MaxAttempts 次尝试）。
    /// </summary>
    public static IEnumerable<TimeSpan> DelaySchedule(RetryOptions options)
    {
        if (options.Backoff is { Count: > 0 } sequence)
        {
            var steady = options.SteadyInterval ?? sequence[^1];
            var cap = options.MaxTotalDelay;
            var elapsed = TimeSpan.Zero;
            for (var i = 0; i < MaxSequenceRetries; i++)
            {
                var delay = i < sequence.Count ? sequence[i] : steady;
                if (cap is { } max && elapsed + delay > max)
                    yield break;
                elapsed += delay;
                yield return delay;
            }
            yield break;
        }

        for (var attempt = 1; attempt < options.MaxAttempts; attempt++)
            yield return ExponentialDelay(attempt, options);
    }

    private static TimeSpan ExponentialDelay(int attempt, RetryOptions options)
    {
        // attempt 从 1 起：第 1 次失败后退避 BaseDelay，之后按 Factor 递增，封顶 MaxDelay。
        var millis = options.BaseDelay.TotalMilliseconds * Math.Pow(options.Factor, attempt - 1);
        return TimeSpan.FromMilliseconds(Math.Min(millis, options.MaxDelay.TotalMilliseconds));
    }
}
