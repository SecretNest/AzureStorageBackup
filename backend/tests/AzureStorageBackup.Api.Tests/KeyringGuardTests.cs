using AzureStorageBackup.Api.Endpoints;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class KeyringGuardTests
{
    private sealed class FixedHealth(KeyringStatus status) : IKeyringHealth
    {
        public KeyringStatus Status { get; private set; } = status;
        public void Set(KeyringStatus s) => Status = s;
    }

    [Fact]
    public void Returns_Null_When_Healthy()
        => Assert.Null(KeyringGuard.Blocked(new FixedHealth(KeyringStatus.Healthy)));

    [Fact]
    public void Returns_Conflict_When_Lost()
    {
        var result = KeyringGuard.Blocked(new FixedHealth(KeyringStatus.Lost));

        Assert.NotNull(result);
        var statusCode = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result);
        Assert.Equal(409, statusCode.StatusCode);
    }
}
