namespace AzureStorageBackup.Api.Services;

public class BackupInventoryService(IAccountService accounts, IContainerService containers)
    : IBackupInventoryService
{
    public async Task<IReadOnlyList<DiscoveredBackup>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<DiscoveredBackup>();

        foreach (var account in await accounts.ListAsync(ct))
        {
            var found = await containers.ListContainersAsync(account, ct);
            foreach (var c in found.Where(c => c.Backup != BackupPresence.None))
                result.Add(new DiscoveredBackup(account.Id, account.Name, c.Name, c.Backup));
        }

        return result;
    }
}
