namespace AzureStorageBackup.Api.Services;

/// <summary>
/// One process-wide gate serializing the account-topology check-then-act spans: "no backup uses this
/// account → delete it" (AccountEndpoints) versus "this account exists → create a config under it"
/// (BackupConfigEndpoints create/import). BackupConfig.AccountId has no foreign key — old databases must
/// keep migrating — so the database cannot referee this race, and without the gate both checks can pass
/// before either write commits, leaving an orphan config whose account is gone (it only blows up on the
/// next scheduled run at 3am, or worse, on the restore that actually needs the data). The spans held are
/// a couple of DB queries each, so one global semaphore costs nothing.
/// </summary>
internal static class AccountTopologyGate
{
    internal static readonly SemaphoreSlim Gate = new(1, 1);
}
