using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

public record CreateContainerRequest(string Name);

/// <summary>
/// 账户下的 container 管理端点（PRD 1.2）。
/// 注：Azure Blob 不支持 container 重命名，故只有列举/创建/删除。
/// </summary>
public static class ContainerEndpoints
{
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

            var list = await containers.ListContainersAsync(account, ct);
            return Results.Ok(list);
        });

        group.MapPost("/", async (
            int accountId, CreateContainerRequest req,
            IAccountService accounts, IContainerService containers, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            var account = await accounts.GetAsync(accountId, ct);
            if (account is null)
                return Results.NotFound();

            await containers.CreateContainerAsync(account, req.Name, ct);
            return Results.Created($"/api/accounts/{accountId}/containers/{req.Name}", new { name = req.Name });
        });

        group.MapDelete("/{name}", async (
            int accountId, string name,
            IAccountService accounts, IContainerService containers, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            var account = await accounts.GetAsync(accountId, ct);
            if (account is null)
                return Results.NotFound();

            await containers.DeleteContainerAsync(account, name, ct);
            return Results.NoContent();
        });

        return app;
    }
}
