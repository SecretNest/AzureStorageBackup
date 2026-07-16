using AzureStorageBackup.Api.Models;

namespace AzureStorageBackup.Api.Services;

public class ContainerService(IBlobClientFactory factory) : IContainerService
{
    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(
        Account account, CancellationToken ct = default)
    {
        var svc = factory.CreateServiceClient(account);
        var result = new List<ContainerInfo>();

        await foreach (var item in svc.GetBlobContainersAsync(cancellationToken: ct))
        {
            var container = svc.GetBlobContainerClient(item.Name);
            var hasPlain = await container.GetBlobClient(BackupDiscovery.IndexBlobName)
                .ExistsAsync(ct);
            var hasEncrypted = await container.GetBlobClient(BackupDiscovery.EncryptedIndexBlobName)
                .ExistsAsync(ct);

            var presence = BackupDiscovery.Determine(hasPlain.Value, hasEncrypted.Value);
            result.Add(new ContainerInfo(item.Name, presence));
        }

        return result;
    }

    public async Task CreateContainerAsync(Account account, string name, CancellationToken ct = default)
    {
        var svc = factory.CreateServiceClient(account);
        await svc.GetBlobContainerClient(name).CreateIfNotExistsAsync(cancellationToken: ct);
    }

    public async Task DeleteContainerAsync(Account account, string name, CancellationToken ct = default)
    {
        var svc = factory.CreateServiceClient(account);
        await svc.GetBlobContainerClient(name).DeleteIfExistsAsync(cancellationToken: ct);
    }
}
