using Azure.Core;
using Azure.Core.Pipeline;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// Measures HEAD overlap: the peak number of GetProperties requests in flight at once. A small delay is injected
/// into every HEAD so that overlap, where the implementation allows it, is actually observable — without the
/// delay Azurite answers faster than the caller can issue the next request and the peak reads 1 either way.
/// Non-HEAD traffic passes through untouched and uncounted.
/// </summary>
internal sealed class HeadOverlapProbe : HttpPipelinePolicy
{
    private int _inFlight;
    private int _peak;
    private int _heads;

    public int Peak => Volatile.Read(ref _peak);

    /// <summary>Total HEADs issued — what the "a condemned family is not probed to the end" skip saves.</summary>
    public int Heads => Volatile.Read(ref _heads);

    public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        if (message.Request.Method != RequestMethod.Head)
        {
            await ProcessNextAsync(message, pipeline);
            return;
        }
        Interlocked.Increment(ref _heads);
        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = Volatile.Read(ref _peak))
               && Interlocked.CompareExchange(ref _peak, now, seen) != seen) { }
        try
        {
            await Task.Delay(100);
            await ProcessNextAsync(message, pipeline);
        }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        => ProcessNext(message, pipeline);
}
