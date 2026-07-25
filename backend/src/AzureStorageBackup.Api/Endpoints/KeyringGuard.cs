using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>
/// 恢复模式闸门（设计 §3.3）：密钥环丢失时，所有需要凭据的动作在入口即 409 失败，
/// 不进入编排层——避免用解不开的密码发起云操作。
/// </summary>
public static class KeyringGuard
{
    public static IResult? Blocked(IKeyringHealth health) =>
        health.Status == KeyringStatus.Lost
            ? Results.Json(
                new { error = "Data protection keys were lost; re-enter credentials before running this action.", code = "keyring_lost" },
                statusCode: StatusCodes.Status409Conflict)
            : null;
}
