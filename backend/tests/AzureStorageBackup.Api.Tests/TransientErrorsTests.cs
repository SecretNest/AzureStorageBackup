using System.Net.Sockets;
using Azure;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class TransientErrorsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(408)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void RequestFailed_transient_statuses(int status)
        => Assert.True(TransientErrors.IsTransient(new RequestFailedException(status, "boom")));

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(412)]
    public void RequestFailed_permanent_statuses(int status)
        => Assert.False(TransientErrors.IsTransient(new RequestFailedException(status, "nope")));

    [Fact]
    public void Io_socket_timeout_are_transient()
    {
        Assert.True(TransientErrors.IsTransient(new IOException("disk hiccup")));
        Assert.True(TransientErrors.IsTransient(new SocketException(110)));
        Assert.True(TransientErrors.IsTransient(new TimeoutException("slow")));
    }

    // This is the exact shape of the production failure: the SDK exhausts its retries -> AggregateException(TaskCanceledException...)
    [Fact]
    public void Aggregate_of_timeouts_is_transient()
    {
        var agg = new AggregateException(
            "Retry failed after 6 tries.",
            new TaskCanceledException("timeout"), new TaskCanceledException("timeout"));
        Assert.True(TransientErrors.IsTransient(agg));
    }

    [Fact]
    public void Aggregate_with_any_permanent_inner_is_not_transient()
    {
        var agg = new AggregateException(
            new TaskCanceledException("timeout"), new InvalidOperationException("bug"));
        Assert.False(TransientErrors.IsTransient(agg));
    }

    [Fact]
    public void Empty_aggregate_is_not_transient()
        => Assert.False(TransientErrors.IsTransient(new AggregateException()));

    // The user pressed cancel -> the token is triggered -> this is not a network blip and must not be swallowed as transient.
    [Fact]
    public void Cancellation_by_user_is_not_transient()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(TransientErrors.IsTransient(new OperationCanceledException(cts.Token), cts.Token));
        Assert.False(TransientErrors.IsTransient(
            new AggregateException(new TaskCanceledException()), cts.Token));
    }

    // An OperationCanceledException with an untriggered token = the SDK's network timeout, which counts as transient.
    [Fact]
    public void Cancellation_without_user_request_is_transient()
        => Assert.True(TransientErrors.IsTransient(new OperationCanceledException(), CancellationToken.None));

    [Fact]
    public void Plain_bug_is_not_transient()
        => Assert.False(TransientErrors.IsTransient(new InvalidOperationException("bug")));
}
