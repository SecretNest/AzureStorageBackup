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
}
