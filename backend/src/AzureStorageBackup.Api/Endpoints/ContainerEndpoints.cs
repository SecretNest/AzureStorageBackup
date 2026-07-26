using Azure;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

public record CreateContainerRequest(string Name);

/// <summary>
/// 账户下的 container 管理端点（PRD 1.2）。
/// 注：Azure Blob 不支持 container 重命名，故只有列举/创建/删除。
/// </summary>
public static class ContainerEndpoints
{
    /// <summary>
    /// 把 Azure 的失败翻译成客户端能用的响应。
    ///
    /// 逐端点捕获而非注册全局 handler：全局 handler 会一并接管本轮范围之外的所有未处理
    /// 异常，改变既有失败语义（见 KeyringGuard.cs 的同类说明）。
    /// </summary>
    private static IResult MapAzureFailure(RequestFailedException ex)
    {
        // 4xx 是调用方能修的（名字非法、无权限、已被他人占用），原样透传状态码——
        // 但排除 401：Azure 存储账户返回的 401 说的是「这次到存储账户的请求没有认证成功」，
        // 不是「这个操作员的登录会话失效」。用 StorageSharedKeyCredential 时 Azure 本身认证失败会给
        // 403,401 的现实来源是中间代理（本项目的中国区/美国政府云正是靠代理落地）。
        // 如果把它原样透传，前端 client.ts 的 401 处理器会把操作员直接踢回登录页，
        // 所以这里改走 502，和其他不可操作的失败归到一类。
        if (ex.Status is >= 400 and < 500 and not StatusCodes.Status401Unauthorized)
            return Results.Json(
                new { error = string.IsNullOrEmpty(ex.ErrorCode) ? ex.Message : $"{ex.ErrorCode}: {ex.Message}" },
                statusCode: ex.Status);

        // Status 0 表示请求没能拿到响应（DNS/代理/网络）；401 同理归到这里——
        // 都是上游的问题，不是本服务的问题，也不是用户会话的问题。用 502 说清楚责任在哪一侧。
        return Results.Json(
            new { error = "The storage account could not be reached. Check the endpoint, proxy, and network." },
            statusCode: StatusCodes.Status502BadGateway);
    }

    public static IEndpointRouteBuilder MapContainerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounts/{accountId:int}/containers").WithTags("Containers");

        // 列/建/删 container 都要连云（设计 §3.1 明列「列容器」为需要凭据的动作），
        // 密钥环丢失时必须在入口 409，而不是让 SecretReader 在深处抛异常。
        group.MapGet("/", async (
            int accountId, IAccountService accounts, IContainerService containers, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            var account = await accounts.GetAsync(accountId, ct);
            if (account is null)
                return Results.NotFound();

            try
            {
                var list = await containers.ListContainersAsync(account, ct);
                return Results.Ok(list);
            }
            catch (RequestFailedException ex)
            {
                return MapAzureFailure(ex);
            }
        });

        group.MapPost("/", async (
            int accountId, CreateContainerRequest req,
            IAccountService accounts, IContainerService containers, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            // 连云之前先判：Azure 对非法名只回一句「contains invalid characters」，
            // 不说是哪个字符也不说规则，照搬给用户等于没说。
            if (ContainerName.Validate(req.Name) is { } invalid)
                return Results.BadRequest(new { error = invalid });

            var account = await accounts.GetAsync(accountId, ct);
            if (account is null)
                return Results.NotFound();

            try
            {
                await containers.CreateContainerAsync(account, req.Name, ct);
            }
            catch (RequestFailedException ex)
            {
                return MapAzureFailure(ex);
            }

            return Results.Created($"/api/accounts/{accountId}/containers/{Uri.EscapeDataString(req.Name)}", new { name = req.Name });
        });

        group.MapDelete("/{name}", async (
            int accountId, string name,
            IAccountService accounts, IContainerService containers, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            var account = await accounts.GetAsync(accountId, ct);
            if (account is null)
                return Results.NotFound();

            try
            {
                await containers.DeleteContainerAsync(account, name, ct);
            }
            catch (RequestFailedException ex)
            {
                return MapAzureFailure(ex);
            }

            return Results.NoContent();
        });

        return app;
    }
}
