using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>409 的响应体。闸门与深度防御的映射共用同一形状，前端只需认一个 code。</summary>
public sealed record KeyringLostError(string error, string code);

/// <summary>
/// 恢复模式闸门（设计 §3.3）：密钥环丢失时，所有需要凭据的动作在入口即 409 失败，
/// 不进入编排层——避免用解不开的密码发起云操作。
/// </summary>
public static class KeyringGuard
{
    public static KeyringLostError Payload => new(
        "Data protection keys were lost; re-enter credentials before running this action.",
        "keyring_lost");

    public static IResult? Blocked(IKeyringHealth health) =>
        health.Status == KeyringStatus.Lost
            ? Results.Json(Payload, statusCode: StatusCodes.Status409Conflict)
            : null;
}

/// <summary>
/// 深度防御（设计 §3.1）：闸门被新代码路径绕过、或密钥环在进程运行期间被换掉（canary 尚未
/// 重新判定）时，咽喉处的 <see cref="SecretUnavailableException"/> 会一路冒泡。若无人接管，
/// 客户端拿到的是裸 500，前端无从与「密钥环丢失」关联。这里统一映射成 409 + keyring_lost，
/// 与 <see cref="KeyringGuard"/> 同形。
///
/// 刻意不用 UseExceptionHandler/IExceptionHandler：那条路要么注册兜底 handler、要么注册
/// ProblemDetails，两者都会把**全部**未处理异常一并接管，改变本次修复范围之外的失败语义
/// （包括让集成测试里本应抛出的异常悄悄变成 500 响应）。这里只认这一种异常，其余原样上抛。
/// </summary>
public static class SecretUnavailableMapping
{
    public static IApplicationBuilder UseSecretUnavailableMapping(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (SecretUnavailableException)
            {
                // 响应已经开始（例如流式下载中途）时改不了状态码，只能让异常继续上抛。
                if (context.Response.HasStarted)
                    throw;

                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(KeyringGuard.Payload);
            }
        });
}
