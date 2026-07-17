using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>备份配置管理端点（PRD §11 向导产物的持久化）。响应不含密码；更新时空密码保留原值。</summary>
public static class BackupConfigEndpoints
{
    public static IEndpointRouteBuilder MapBackupConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/backup-configs").WithTags("BackupConfigs");

        group.MapGet("/", async (IBackupConfigService svc, CancellationToken ct) =>
        {
            var list = await svc.ListAsync(ct);
            return Results.Ok(list.Select(BackupConfigResponse.From));
        });

        group.MapGet("/{id:int}", async (int id, IBackupConfigService svc, CancellationToken ct) =>
        {
            var c = await svc.GetAsync(id, ct);
            return c is null ? Results.NotFound() : Results.Ok(BackupConfigResponse.From(c));
        })
        .WithName("GetBackupConfig");

        // 导入已有备份：读 container 的信息文件恢复配置，回填本地权威状态 + 全部版本索引入本地缓存（roadmap，PRD 1.5、§3.3）
        group.MapPost("/import", async (ImportRequest req, IAccountService accounts, TrackedInfoStore trackedInfo, IBackupConfigService svc, ILocalIndexCache indexCache, CancellationToken ct) =>
        {
            var account = await accounts.GetAsync(req.AccountId, ct);
            if (account is null)
                return Results.BadRequest(new { error = "Account not found." });

            (BackupInfoFile Info, string ETag)? seeded;
            try
            {
                seeded = await trackedInfo.SeedFromCloudAsync(account, req.ContainerName, req.Password, ct);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"Could not read info file (wrong password?): {ex.Message}" });
            }
            if (seeded is null)
                return Results.NotFound(new { error = "No backup found in this container." });
            var info = seeded.Value.Info;

            var config = new BackupConfig
            {
                AccountId = req.AccountId,
                ContainerName = req.ContainerName,
                Name = info.Backup.Name,
                Description = info.Backup.Description,
                LocalRoot = info.Backup.SourceRootHint ?? string.Empty,
                Password = info.Backup.Encrypted ? req.Password : null,
            };
            var created = await svc.CreateAsync(config, ct);

            // 下载全部版本索引到本地缓存（版本文件是 metadata、不在 Archive）：之后备份/清理平时不再下载云端索引。
            var identity = info.Backup.CreatedAt.UtcTicks;
            foreach (var v in info.Versions)
                await indexCache.ReadAsync(account, req.ContainerName, v.Version, identity, v.IndexBlob, req.Password, ct);

            return Results.CreatedAtRoute("GetBackupConfig", new { id = created.Id }, BackupConfigResponse.From(created));
        });

        group.MapPost("/", async (BackupConfigRequest req, IBackupConfigService svc, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.LocalRoot))
                return Results.BadRequest(new { error = "LocalRoot is required." });
            if (string.IsNullOrWhiteSpace(req.ContainerName))
                return Results.BadRequest(new { error = "ContainerName is required." });

            var created = await svc.CreateAsync(req.ToConfig(), ct);
            return Results.CreatedAtRoute("GetBackupConfig", new { id = created.Id }, BackupConfigResponse.From(created));
        });

        group.MapPut("/{id:int}", async (int id, BackupConfigRequest req, IBackupConfigService svc, CancellationToken ct) =>
        {
            var existing = await svc.GetAsync(id, ct);
            if (existing is null)
                return Results.NotFound();

            var update = req.ToConfig();
            // 空密码表示保留原值（不清除加密）
            if (string.IsNullOrEmpty(req.Password))
                update.Password = existing.Password;

            var result = await svc.UpdateAsync(id, update, ct);
            return result is null ? Results.NotFound() : Results.Ok(BackupConfigResponse.From(result));
        });

        group.MapDelete("/{id:int}", async (int id, IBackupConfigService svc, IOperationLog log, CancellationToken ct) =>
        {
            var config = await svc.GetAsync(id, ct);
            var ok = await svc.DeleteAsync(id, ct);
            if (ok && config is not null)
                await log.DeleteForContainerAsync(config.ContainerName, ct); // 删除备份时连带删其审计日志（PRD 3.6）
            return ok ? Results.NoContent() : Results.NotFound();
        });

        // 启动一次备份（后台运行，进度轮询）
        group.MapPost("/{id:int}/run", async (int id, IBackupConfigService svc, BackupRunner runner, CancellationToken ct) =>
        {
            if (await svc.GetAsync(id, ct) is null)
                return Results.NotFound();

            var state = runner.Start(id);
            return Results.Accepted($"/api/backup-configs/{id}/run", BackupRunResponse.From(state));
        });

        // 查询运行进度/状态
        group.MapGet("/{id:int}/run", (int id, BackupRunner runner) =>
        {
            var state = runner.Get(id);
            return state is null ? Results.NotFound() : Results.Ok(BackupRunResponse.From(state));
        });

        // 启动还原（后台运行；targetRoot 缺省用配置的本地根，version 缺省用最新）
        group.MapPost("/{id:int}/restore", async (int id, RestoreRequestBody body, IBackupConfigService svc, RestoreRunner runner, CancellationToken ct) =>
        {
            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();

            var target = string.IsNullOrWhiteSpace(body.TargetRoot) ? config.LocalRoot : body.TargetRoot;
            var state = runner.Start(id, target, body.Version);
            return Results.Accepted($"/api/backup-configs/{id}/restore", RestoreRunResponse.From(state));
        });

        group.MapGet("/{id:int}/restore", (int id, RestoreRunner runner) =>
        {
            var state = runner.Get(id);
            return state is null ? Results.NotFound() : Results.Ok(RestoreRunResponse.From(state));
        });

        // 列出某备份的全部版本（供还原/检查选择版本）。走本地权威信息文件，平时不读云端。
        group.MapGet("/{id:int}/versions", async (int id, IBackupConfigService svc, IAccountService accounts, TrackedInfoStore trackedInfo, CancellationToken ct) =>
        {
            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();
            var account = await accounts.GetAsync(config.AccountId, ct);
            if (account is null)
                return Results.BadRequest(new { error = "Account not found." });

            var password = string.IsNullOrEmpty(config.Password) ? null : config.Password;
            var info = await trackedInfo.LoadAsync(account, config.ContainerName, password, ct);
            var versions = (info?.Versions ?? []).Select(v => new
            {
                v.Version,
                v.CreatedAt,
                files = v.Stats.Files,
                bytes = v.Stats.Bytes,
                changedFiles = v.Stats.ChangedFiles,
            });
            return Results.Ok(versions);
        });

        // 完整性检查（deep=true 时下载解压重算 hash 深度校验）
        group.MapPost("/{id:int}/check", async (int id, int? version, CloudCheckLevel? cloud, LocalCheckLevel? local, StorageTier? rehydrate, IBackupConfigService svc, IAccountService accounts, BackupChecker checker, IGlobalSettingsService settingsSvc, BackupBusyTracker busy, CancellationToken ct) =>
        {
            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();
            var account = await accounts.GetAsync(config.AccountId, ct);
            if (account is null)
                return Results.BadRequest(new { error = "Account not found." });

            // 检查也是对该备份的操作 → 纳入忙碌；备份正忙则拒绝，检查期间也标记忙碌使计划任务跳过。
            if (!busy.TryAcquire(account.Id, config.ContainerName))
                return Results.Conflict(new { error = "Backup is busy with another operation." });
            try
            {
                var password = string.IsNullOrEmpty(config.Password) ? null : config.Password;
                var settings = await settingsSvc.GetAsync(ct);
                var options = new CheckOptions
                {
                    Cloud = cloud ?? CloudCheckLevel.ExistenceSize,
                    Local = local ?? LocalCheckLevel.Content,
                    RehydrateTier = rehydrate is { } t ? BackupRequestMapper.MapTier(t) : null,
                };
                var result = await checker.CheckAsync(account, config.ContainerName, password, version, options, config.LocalRoot, ct,
                    downloadConcurrency: settings.DownloadConcurrency > 0 ? settings.DownloadConcurrency : 5);
                return Results.Ok(result);
            }
            finally
            {
                busy.Release(account.Id, config.ContainerName);
            }
        });

        return app;
    }
}
