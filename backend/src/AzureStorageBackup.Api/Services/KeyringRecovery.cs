namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 恢复完成判定（设计 §3.4）：所有密文都能用当前密钥环解开时，重建 canary 并翻回 Healthy。
/// 不可在首条重设成功时就翻转——彼时其余记录仍解不开。
/// </summary>
public sealed class KeyringRecovery(IKeyringHealth health, KeyringProbe probe)
{
    public async Task<bool> TryCompleteAsync(CancellationToken ct = default)
    {
        // 扫描与 KeyringProbe 的「哨兵已陈旧但无密文残留」分支共用，保证两处口径一致。
        if (!await probe.AllStoredSecretsReadableAsync(ct))
            return false;

        await probe.WriteCanaryAsync(ct);
        health.Set(KeyringStatus.Healthy);
        return true;
    }
}
