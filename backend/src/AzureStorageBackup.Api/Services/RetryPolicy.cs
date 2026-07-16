namespace AzureStorageBackup.Api.Services;

public sealed record RetryOptions
{
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    public double Factor { get; init; } = 2.0;
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>指数退避重试（M4 §5、PRD 4.1）。isTransient 判定异常是否可重试，默认全部可重试。</summary>
public static class RetryPolicy
{
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> action,
        RetryOptions? options = null,
        Func<Exception, bool>? isTransient = null,
        CancellationToken ct = default)
    {
        options ??= new RetryOptions();
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                return await action(ct);
            }
            catch (Exception ex) when (
                attempt < options.MaxAttempts && (isTransient?.Invoke(ex) ?? true))
            {
                await Task.Delay(DelayFor(attempt, options), ct);
            }
        }
    }

    public static Task ExecuteAsync(
        Func<CancellationToken, Task> action,
        RetryOptions? options = null,
        Func<Exception, bool>? isTransient = null,
        CancellationToken ct = default)
        => ExecuteAsync<object?>(async token => { await action(token); return null; }, options, isTransient, ct);

    private static TimeSpan DelayFor(int attempt, RetryOptions options)
    {
        // attempt 从 1 起：第 1 次失败后退避 BaseDelay，之后按 Factor 递增，封顶 MaxDelay。
        var millis = options.BaseDelay.TotalMilliseconds * Math.Pow(options.Factor, attempt - 1);
        return TimeSpan.FromMilliseconds(Math.Min(millis, options.MaxDelay.TotalMilliseconds));
    }
}
