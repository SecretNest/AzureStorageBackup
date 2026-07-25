using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;

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

        group.MapPost("/", async (AccountRequest req, IAccountService svc, IEncryptionService encryption, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.AccountKey))
                return Results.BadRequest(new { error = "AccountKey is required." });

            var created = await svc.CreateAsync(req.ToAccount(encryption), ct);
            return Results.CreatedAtRoute("GetAccount", new { id = created.Id }, AccountResponse.From(created, keyring.Status == KeyringStatus.Lost));
        });

        group.MapPut("/{id:int}", async (int id, AccountRequest req, IAccountService svc, IEncryptionService encryption, IKeyringHealth keyring, CancellationToken ct) =>
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
            // 保留原密文的分支恰恰是密钥环丢失时解不开的那份密文，必须如实上报 SecretsUnavailable。
            return result is null ? Results.NotFound() : Results.Ok(AccountResponse.From(result, keyring.Status == KeyringStatus.Lost));
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

        // 凭据重设（设计 §3.4）。不复用 PUT——PUT 在恢复模式下受限，且此处必须验证后才落库。
        group.MapPost("/{id:int}/reset-secrets", async (
            int id, ResetAccountSecretsRequest req, IAccountService svc, IBlobClientFactory factory,
            IEncryptionService encryption, AppDbContext db, KeyringRecovery recovery, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.AccountKey))
                return Results.BadRequest(new { error = "AccountKey is required." });

            var existing = await svc.GetAsync(id, ct);
            if (existing is null)
                return Results.NotFound();

            // 用待验证的凭据构造一个临时账户对象去连云；验证不过则不落库。
            var candidate = new Account
            {
                Id = existing.Id,
                Name = existing.Name,
                BlobEndpoint = existing.BlobEndpoint,
                Region = existing.Region,
                UseProxy = existing.UseProxy,
                ProxyMode = existing.ProxyMode,
                ProxyHost = existing.ProxyHost,
                ProxyPort = existing.ProxyPort,
                ProxyUsername = existing.ProxyUsername,
                AccountKeyProtected = encryption.Encrypt(req.AccountKey),
                ProxyPasswordProtected = string.IsNullOrEmpty(req.ProxyPassword)
                    ? null : encryption.Encrypt(req.ProxyPassword),
            };

            var check = await factory.TestConnectionAsync(candidate, ct);
            if (!check.Success)
                return Results.BadRequest(new { error = $"Verification failed: {check.Error}" });

            var row = await db.Accounts.FirstAsync(a => a.Id == id, ct);
            row.AccountKeyProtected = candidate.AccountKeyProtected;
            row.ProxyPasswordProtected = candidate.ProxyPasswordProtected;
            await db.SaveChangesAsync(ct);

            await recovery.TryCompleteAsync(ct);
            return Results.NoContent();
        });

        return app;
    }
}
