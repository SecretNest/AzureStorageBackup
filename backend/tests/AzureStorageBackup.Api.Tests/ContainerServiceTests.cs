using System.Net.Sockets;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

[Trait("Category", "Integration")]
public class ContainerServiceTests
{
    // Azurite 的 well-known 账户与密钥
    private const string AzuriteKey =
        "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";

    private static Account AzuriteAccount() => new()
    {
        Name = "azurite",
        BlobEndpoint = "http://127.0.0.1:10000/devstoreaccount1",
        AccountKey = AzuriteKey,
        Region = AzureRegion.Global
    };

    private static bool AzuriteReachable()
    {
        try
        {
            using var c = new TcpClient();
            c.Connect("127.0.0.1", 10000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string RandomName(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    [SkippableFact]
    public async Task Create_List_Delete_Container()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running on 127.0.0.1:10000");
        var svc = new ContainerService(new BlobClientFactory());
        var acct = AzuriteAccount();
        var name = RandomName("test-");

        await svc.CreateContainerAsync(acct, name);
        var list = await svc.ListContainersAsync(acct);
        Assert.Contains(list, c => c.Name == name);

        await svc.DeleteContainerAsync(acct, name);
        var after = await svc.ListContainersAsync(acct);
        Assert.DoesNotContain(after, c => c.Name == name);
    }

    [SkippableFact]
    public async Task Container_Without_Index_Is_None()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        var svc = new ContainerService(new BlobClientFactory());
        var acct = AzuriteAccount();
        var name = RandomName("noidx-");

        await svc.CreateContainerAsync(acct, name);
        try
        {
            var list = await svc.ListContainersAsync(acct);
            Assert.Equal(BackupPresence.None, list.First(c => c.Name == name).Backup);
        }
        finally
        {
            await svc.DeleteContainerAsync(acct, name);
        }
    }

    [SkippableFact]
    public async Task Container_With_Plain_Index_Is_Plain()
    {
        Skip.IfNot(AzuriteReachable(), "Azurite not running");
        var factory = new BlobClientFactory();
        var svc = new ContainerService(factory);
        var acct = AzuriteAccount();
        var name = RandomName("plain-");

        await svc.CreateContainerAsync(acct, name);
        try
        {
            var container = factory.CreateServiceClient(acct).GetBlobContainerClient(name);
            await container.GetBlobClient(BackupDiscovery.IndexBlobName)
                .UploadAsync(BinaryData.FromString("{}"));

            var list = await svc.ListContainersAsync(acct);
            Assert.Equal(BackupPresence.Plain, list.First(c => c.Name == name).Backup);
        }
        finally
        {
            await svc.DeleteContainerAsync(acct, name);
        }
    }
}
