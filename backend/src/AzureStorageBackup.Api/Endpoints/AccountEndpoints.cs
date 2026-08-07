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
            var usage = await svc.GetBackupUsageAsync(ct);
            return Results.Ok(list.Select(a =>
                AccountResponse.From(a, Pending(keyring, encryption, a), usage.GetValueOrDefault(a.Id))));
        });

        group.MapGet("/{id:int}", async (int id, IAccountService svc, IKeyringHealth keyring, IEncryptionService encryption, CancellationToken ct) =>
        {
            var a = await svc.GetAsync(id, ct);
            if (a is null)
                return Results.NotFound();
            var usage = await svc.GetBackupUsageAsync(ct);
            return Results.Ok(AccountResponse.From(a, Pending(keyring, encryption, a), usage.GetValueOrDefault(id)));
        })
        .WithName("GetAccount");

        group.MapPost("/", async (AccountRequest req, IAccountService svc, IEncryptionService encryption, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.AccountKey))
                return Results.BadRequest(new { error = "AccountKey is required." });

            var created = await svc.CreateAsync(req.ToAccount(encryption), ct);
            // 刚建出来的账户不可能已被占用，显式传空比走默认值更能说明这里不是"拿不到所以留空"。
            return Results.CreatedAtRoute("GetAccount", new { id = created.Id },
                AccountResponse.From(created, Pending(keyring, encryption, created), []));
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
            if (result is null)
                return Results.NotFound();
            var usage = await svc.GetBackupUsageAsync(ct);
            // 保留原密文的分支恰恰是密钥环丢失时解不开的那份密文，必须如实上报 SecretsUnavailable；
            // 而提交了新凭据的分支写入的是当前密钥环的密文，逐条试解会如实返回 false。
            return Results.Ok(AccountResponse.From(
                result, Pending(keyring, encryption, result), usage.GetValueOrDefault(id)));
        });

        group.MapDelete("/{id:int}", async (int id, IAccountService svc, KeyringRecovery recovery, CancellationToken ct) =>
        {
            // 还被备份占用就不能删。库里 BackupConfig.AccountId 没有外键约束，所以数据库那一层
            // 拦不住——删完留下的是一批 AccountId 指向空号的孤儿配置，而它们一直到下次真跑起来
            // 才炸（BackupRunner/CheckRunner/RestoreRunner 三处的 "Account {id} not found"）。
            // 定时任务的话就是半夜失败、第二天才看见；还原那条更糟，等到真要恢复数据时才发现。
            // 界面已经把删除按钮禁掉了，这里是同一道判断的服务端一侧——不设，那个禁用就只是装饰。
            var usage = await svc.GetBackupUsageAsync(ct);
            if (usage.GetValueOrDefault(id) is { Count: > 0 } inUse)
                return Results.Conflict(new
                {
                    error = $"Account is used by {inUse.Count} backup(s): {string.Join(", ", inUse)}",
                    usedByBackups = inUse,
                });

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
            // 与 POST / 同一道校验：空 key 会变成空的 AccountKeyProtected，解密咽喉处抛
            // SecretUnavailableException，用户看到的是「密钥环解不开」——真实原因只是没填 key。
            if (string.IsNullOrWhiteSpace(req.AccountKey))
                return Results.BadRequest(new { error = "AccountKey is required." });

            var result = await factory.TestConnectionAsync(req.ToAccount(encryption), ct);
            return Results.Ok(result);
        });

        // 用已存账户的凭据测试连通（编辑态）。
        //
        // 编辑一个已有账户时 Key 框是空的（"Leave blank to keep current"），而上面那个不带 id 的
        // 端点会因为空 Key 直接 400——于是"改了 endpoint 或代理，想先测一下现有 key 还连不连得上"
        // 这件最该能做的事，恰恰做不了。这里补上：空的敏感字段沿用库里的密文，其余字段一律用
        // 请求里改过的值，所以测的是"新配置 + 旧凭据"这个真正要验证的组合。
        //
        // 与 PUT 用的是同一套"空即保留"的搬运逻辑（直接搬密文，不解密），两处必须保持一致——
        // 否则会出现"测得通但存不进"或反过来的情形。
        group.MapPost("/{id:int}/test-connection", async (
            int id, AccountRequest req, IAccountService svc, IBlobClientFactory factory,
            IEncryptionService encryption, CancellationToken ct) =>
        {
            var existing = await svc.GetAsync(id, ct);
            if (existing is null)
                return Results.NotFound();

            var probe = req.ToAccount(encryption);
            if (string.IsNullOrEmpty(req.AccountKey))
                probe.AccountKeyProtected = existing.AccountKeyProtected;
            if (string.IsNullOrEmpty(req.ProxyPassword))
                probe.ProxyPasswordProtected = existing.ProxyPasswordProtected;

            return Results.Ok(await factory.TestConnectionAsync(probe, ct));
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
