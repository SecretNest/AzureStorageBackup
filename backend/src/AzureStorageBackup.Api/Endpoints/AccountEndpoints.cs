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

        group.MapGet("/", async (IAccountService svc, IKeyringHealth keyring, IEncryptionService encryption, CancellationToken ct) =>
        {
            var list = await svc.ListAsync(ct);
            return Results.Ok(list.Select(a => AccountResponse.From(a, Pending(keyring, encryption, a))));
        });

        group.MapGet("/{id:int}", async (int id, IAccountService svc, IKeyringHealth keyring, IEncryptionService encryption, CancellationToken ct) =>
        {
            var a = await svc.GetAsync(id, ct);
            return a is null ? Results.NotFound() : Results.Ok(AccountResponse.From(a, Pending(keyring, encryption, a)));
        })
        .WithName("GetAccount");

        group.MapPost("/", async (AccountRequest req, IAccountService svc, IEncryptionService encryption, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.AccountKey))
                return Results.BadRequest(new { error = "AccountKey is required." });

            var created = await svc.CreateAsync(req.ToAccount(encryption), ct);
            return Results.CreatedAtRoute("GetAccount", new { id = created.Id }, AccountResponse.From(created, Pending(keyring, encryption, created)));
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
            // 保留原密文的分支恰恰是密钥环丢失时解不开的那份密文，必须如实上报 SecretsUnavailable；
            // 而提交了新凭据的分支写入的是当前密钥环的密文，逐条试解会如实返回 false。
            return result is null ? Results.NotFound() : Results.Ok(AccountResponse.From(result, Pending(keyring, encryption, result)));
        });

        group.MapDelete("/{id:int}", async (int id, IAccountService svc, KeyringRecovery recovery, CancellationToken ct) =>
        {
            var ok = await svc.DeleteAsync(id, ct);
            // 删掉的可能正是唯一一条待重设的解不开的密文：不收尾就翻不回 Healthy，
            // 用户直到下次重启前都会卡在「Lost 但无一条待重设」的死角（设计 §3.4 fix）。
            if (ok)
                await recovery.TryCompleteAsync(ct);
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

            // 前面的存在性检查与这次写之间，账户可能已被删除（验证要连云，窗口不短）：
            // FirstAsync 会抛成 500，而全仓约定是 404。
            var row = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (row is null)
                return Results.NotFound();
            row.AccountKeyProtected = candidate.AccountKeyProtected;
            row.ProxyPasswordProtected = candidate.ProxyPasswordProtected;
            await db.SaveChangesAsync(ct);

            await recovery.TryCompleteAsync(ct);
            return Results.NoContent();
        });

        return app;
    }

    /// <summary>
    /// 该账户是否仍待重设。Healthy 时短路，列表端点不触发任何解密（设计 §3.1 的核心性质）；
    /// Lost 时逐条试解，使已重设成功的账户立刻停止显示「待重设」（设计 §3.3）。
    /// </summary>
    private static bool Pending(IKeyringHealth keyring, IEncryptionService encryption, Account account) =>
        keyring.Status == KeyringStatus.Lost && SecretAvailability.Unreadable(encryption, account);
}
