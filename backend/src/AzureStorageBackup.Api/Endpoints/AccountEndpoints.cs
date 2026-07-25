using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>账户管理端点。响应不含敏感字段；更新时空的敏感字段保留原值。</summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounts").WithTags("Accounts");

        group.MapGet("/", async (IAccountService svc, IKeyringHealth keyring, CancellationToken ct) =>
        {
            var list = await svc.ListAsync(ct);
            var keyringLost = keyring.Status == KeyringStatus.Lost;
            return Results.Ok(list.Select(a => AccountResponse.From(a, keyringLost)));
        });

        group.MapGet("/{id:int}", async (int id, IAccountService svc, IKeyringHealth keyring, CancellationToken ct) =>
        {
            var a = await svc.GetAsync(id, ct);
            return a is null ? Results.NotFound() : Results.Ok(AccountResponse.From(a, keyring.Status == KeyringStatus.Lost));
        })
        .WithName("GetAccount");

        group.MapPost("/", async (AccountRequest req, IAccountService svc, IEncryptionService encryption, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.AccountKey))
                return Results.BadRequest(new { error = "AccountKey is required." });

            var created = await svc.CreateAsync(req.ToAccount(encryption), ct);
            return Results.CreatedAtRoute("GetAccount", new { id = created.Id }, AccountResponse.From(created));
        });

        group.MapPut("/{id:int}", async (int id, AccountRequest req, IAccountService svc, IEncryptionService encryption, CancellationToken ct) =>
        {
            var existing = await svc.GetAsync(id, ct);
            if (existing is null)
                return Results.NotFound();

            var update = req.ToAccount(encryption);
            // 空的敏感字段表示保留原值：直接搬运原密文，不必解密。
            if (string.IsNullOrEmpty(req.AccountKey))
                update.AccountKeyProtected = existing.AccountKeyProtected;
            if (string.IsNullOrEmpty(req.ProxyPassword))
                update.ProxyPasswordProtected = existing.ProxyPasswordProtected;

            var result = await svc.UpdateAsync(id, update, ct);
            return result is null ? Results.NotFound() : Results.Ok(AccountResponse.From(result));
        });

        group.MapDelete("/{id:int}", async (int id, IAccountService svc, CancellationToken ct) =>
        {
            var ok = await svc.DeleteAsync(id, ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        // 用未保存的配置测试连通（不落库）
        group.MapPost("/test-connection", async (AccountRequest req, IBlobClientFactory factory, IEncryptionService encryption, CancellationToken ct) =>
        {
            var result = await factory.TestConnectionAsync(req.ToAccount(encryption), ct);
            return Results.Ok(result);
        });

        return app;
    }
}
