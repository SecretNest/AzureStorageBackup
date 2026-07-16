using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using NSubstitute;

namespace AzureStorageBackup.Api.Tests;

public class BackupInventoryServiceTests
{
    [Fact]
    public async Task Lists_Only_Containers_With_Backup()
    {
        var accounts = Substitute.For<IAccountService>();
        var containers = Substitute.For<IContainerService>();
        var acct = new Account { Id = 1, Name = "a1" };
        accounts.ListAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Account>>([acct]));
        containers.ListContainersAsync(acct, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ContainerInfo>>(
            [
                new ContainerInfo("c1", BackupPresence.Plain),
                new ContainerInfo("c2", BackupPresence.None),
                new ContainerInfo("c3", BackupPresence.Encrypted),
            ]));

        var sut = new BackupInventoryService(accounts, containers);
        var result = await sut.ListAsync();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, b => b is { ContainerName: "c1", Presence: BackupPresence.Plain });
        Assert.Contains(result, b => b is { ContainerName: "c3", Presence: BackupPresence.Encrypted });
        Assert.DoesNotContain(result, b => b.ContainerName == "c2");
    }

    [Fact]
    public async Task Aggregates_Across_Accounts()
    {
        var accounts = Substitute.For<IAccountService>();
        var containers = Substitute.For<IContainerService>();
        var a1 = new Account { Id = 1, Name = "a1" };
        var a2 = new Account { Id = 2, Name = "a2" };
        accounts.ListAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Account>>([a1, a2]));
        containers.ListContainersAsync(a1, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ContainerInfo>>([new ContainerInfo("x", BackupPresence.Plain)]));
        containers.ListContainersAsync(a2, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ContainerInfo>>([new ContainerInfo("y", BackupPresence.Plain)]));

        var sut = new BackupInventoryService(accounts, containers);
        var result = await sut.ListAsync();

        Assert.Equal(2, result.Count);
        Assert.Contains(result, b => b.AccountId == 1 && b.ContainerName == "x");
        Assert.Contains(result, b => b.AccountId == 2 && b.ContainerName == "y");
    }
}
