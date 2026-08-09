namespace AzureStorageBackup.Api.Services;

public sealed record RetryOptions
{
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    public double Factor { get; init; } = 2.0;
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Explicit backoff sequence (PRD 4.1). Non-empty → sequence mode: back off along the sequence step by step, repeating
    /// <see cref="SteadyInterval"/> once the sequence is exhausted, and stop (give up and throw) as soon as the accumulated backoff exceeds <see cref="MaxTotalDelay"/>. Empty → exponential backoff capped by the <see cref="MaxAttempts"/> count.
    /// </summary>
    public IReadOnlyList<TimeSpan>? Backoff { get; init; }

    /// <summary>The fixed interval repeated once the sequence is exhausted (defaults to the last item of the sequence, i.e. the PRD's "every 300s thereafter").</summary>
    public TimeSpan? SteadyInterval { get; init; }

    /// <summary>Total cap on accumulated backoff in sequence mode (PRD default 2h). Empty means no cap (bounded only by the safety limit).</summary>
    public TimeSpan? MaxTotalDelay { get; init; }
}

/// <summary>Exponential-backoff retry (M4 §5, PRD 4.1). isTransient decides whether an exception is retryable; by default everything is.</summary>
public static class RetryPolicy
{
    // Safety cap for sequence mode when no MaxTotalDelay is set, so retrying cannot go on forever.
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
    /// Produces the backoff durations to wait before each retry (the first attempt does not count). Number of elements yielded = number of retries allowed.
    /// Sequence mode: item by item through Backoff, repeating SteadyInterval once exhausted, never accumulating past MaxTotalDelay.
    /// Count mode: exponential backoff, yielding MaxAttempts-1 items (corresponding to MaxAttempts attempts).
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
        // attempt starts at 1: after the 1st failure back off by BaseDelay, then grow by Factor, capped at MaxDelay.
        var millis = options.BaseDelay.TotalMilliseconds * Math.Pow(options.Factor, attempt - 1);
        return TimeSpan.FromMilliseconds(Math.Min(millis, options.MaxDelay.TotalMilliseconds));
    }
}
