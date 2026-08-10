using Azure.Core.Pipeline;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// A 3 TB run failed with six identical "exceeded the configured timeout of 0:01:40" — 100 seconds, the Azure SDK
/// default, which nothing in this project had ever set. These lock down the two halves of fixing that, because both
/// are the kind of setting that is easy to drop in a refactor and impossible to notice until a slow evening.
/// </summary>
public class BlobClientTimeoutTests
{
    /// <summary>
    /// A single attempt gets five minutes rather than the SDK's 100 seconds. At the 4-6 MB/s this project measured
    /// against Azure, the 100 MB default volume needs 17-25 seconds on a good line — the old budget left almost no
    /// room for a bad one, and a Put Blob that times out restarts from zero on every retry.
    /// </summary>
    [Fact]
    public void Network_timeout_leaves_room_for_a_slow_uplink()
    {
        var options = BlobClientFactory.CreateOptions(new HttpClientHandler());

        Assert.Equal(TimeSpan.FromMinutes(5), options.Retry.NetworkTimeout);

        // Sanity-check the reasoning rather than just the constant: a 100 MB volume has to survive a line an order
        // of magnitude worse than the measured ceiling.
        const double volumeMb = 100;
        var slowestTolerated = volumeMb / options.Retry.NetworkTimeout.TotalSeconds;
        Assert.True(slowestTolerated < 0.5,
            $"a 100 MB volume would need {slowestTolerated:N2} MB/s to fit in the timeout, which is not slow-line tolerance");
    }

    /// <summary>
    /// HttpClient's own timeout covers the whole request and knows nothing about the SDK's retries, so the smaller
    /// of the two silently wins. Left at its 100-second default it would override the setting above entirely — the
    /// failure would still read "0:01:40" and the fix would look applied while doing nothing.
    /// </summary>
    [Fact]
    public void The_transport_does_not_impose_a_shorter_timeout_of_its_own()
    {
        Assert.Equal(Timeout.InfiniteTimeSpan, BlobClientFactory.CreateHttpClient(new HttpClientHandler()).Timeout);

        // And it really is the transport the options hand to the SDK.
        Assert.IsType<HttpClientTransport>(BlobClientFactory.CreateOptions(new HttpClientHandler()).Transport);
    }
}
