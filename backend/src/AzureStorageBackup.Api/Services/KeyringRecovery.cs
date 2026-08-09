namespace AzureStorageBackup.Api.Services;

/// <summary>
/// The recovery completion check (design §3.4): once every ciphertext decrypts with the current key ring,
/// rebuild the canary and flip back to Healthy.
/// It must not flip on the first successful reset — the other records still do not decrypt at that point.
/// </summary>
public sealed class KeyringRecovery(IKeyringHealth health, KeyringProbe probe)
{
    public async Task<bool> TryCompleteAsync(CancellationToken ct = default)
    {
        // Shared with KeyringProbe's "stale canary but no ciphertext left" branch, so both use the same criterion.
        if (!await probe.AllStoredSecretsReadableAsync(ct))
            return false;

        await probe.WriteCanaryAsync(ct);
        health.Set(KeyringStatus.Healthy);
        return true;
    }
}
