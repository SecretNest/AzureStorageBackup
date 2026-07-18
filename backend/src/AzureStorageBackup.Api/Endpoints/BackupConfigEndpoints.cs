using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>备份配置管理端点（PRD §11 向导产物的持久化）。响应不含密码；更新时空密码保留原值。</summary>
public static class BackupConfigEndpoints
{
    public static IEndpointRouteBuilder MapBackupConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/backup-configs").WithTags("BackupConfigs");

        group.MapGet("/", async (IBackupConfigService svc, BackupRunner backupRunner, RestoreRunner restoreRunner, RepairRunner repairRunner, BackupBusyTracker busy, CancellationToken ct) =>
        {
            var list = await svc.ListAsync(ct);
            return Results.Ok(list.Select(c =>
                BackupConfigResponse.From(c, DeriveActivity(c, backupRunner, restoreRunner, repairRunner, busy))));
        });

        group.MapGet("/{id:int}", async (int id, IBackupConfigService svc, BackupRunner backupRunner, RestoreRunner restoreRunner, RepairRunner repairRunner, BackupBusyTracker busy, CancellationToken ct) =>
        {
            var c = await svc.GetAsync(id, ct);
            return c is null
                ? Results.NotFound()
                : Results.Ok(BackupConfigResponse.From(c, DeriveActivity(c, backupRunner, restoreRunner, repairRunner, busy)));
        })
        .WithName("GetBackupConfig");

        // 手动清错（决策 2）：与「下次成功自动清错」同语义，供用户主动 dismiss。
        group.MapPost("/{id:int}/reset-status", async (int id, IBackupConfigService svc, CancellationToken ct) =>
        {
            if (await svc.GetAsync(id, ct) is null)
                return Results.NotFound();
            await svc.ResetStatusAsync(id, ct);
            return Results.NoContent();
        });

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

            try
            {
                var result = await svc.UpdateAsync(id, update, ct);
                return result is null ? Results.NotFound() : Results.Ok(BackupConfigResponse.From(result));
            }
            catch (InvalidOperationException ex)
            {
                // 基础字段创建后锁定（§4.5）：账户/container/本地根/Tier/加密性变更被拒。
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // deleteContainer=true（默认 false）：连云端 container 整体删除（不可逆，§4.3）。先删云端再删本地配置，
        // 避免云端删除失败时本地记录已丢失、用户无法重试。
        group.MapDelete("/{id:int}", async (int id, bool? deleteContainer, IBackupConfigService svc, IAccountService accounts, IContainerService containers, IOperationLog log, CancellationToken ct) =>
        {
            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();

            if (deleteContainer ?? false)
            {
                var account = await accounts.GetAsync(config.AccountId, ct);
                if (account is null)
                    return Results.BadRequest(new { error = "Account not found." });
                await containers.DeleteContainerAsync(account, config.ContainerName, ct);
            }

            var ok = await svc.DeleteAsync(id, ct);
            if (ok)
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
            var state = runner.Start(id, target, body.Version, body.Substitutions);
            return Results.Accepted($"/api/backup-configs/{id}/restore", RestoreRunResponse.From(state));
        });

        // 某路径可从哪些版本恢复（含该路径且有存储、且未标记不可恢复的版本，就近排序），供还原时逐文件替代选择。
        group.MapGet("/{id:int}/file-versions", async (int id, string path, IBackupConfigService svc, IAccountService accounts, IBackupInfoStore store, TrackedInfoStore trackedInfo, CancellationToken ct) =>
        {
            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();
            var account = await accounts.GetAsync(config.AccountId, ct);
            if (account is null)
                return Results.BadRequest(new { error = "Account not found." });

            var password = string.IsNullOrEmpty(config.Password) ? null : config.Password;
            var info = await trackedInfo.LoadAsync(account, config.ContainerName, password, ct);
            var candidates = new List<object>();
            foreach (var v in (info?.Versions ?? []).OrderByDescending(v => v.Version))
            {
                var idx = await store.ReadIndexAsync(account, config.ContainerName, v.IndexBlob, password, ct);
                if (idx.UnrecoverablePaths.Contains(path))
                    continue;
                var e = idx.Entries.FirstOrDefault(x => x.Path == path && x.Storage is not null);
                if (e is not null)
                    candidates.Add(new { v.Version, v.CreatedAt, length = e.Length });
            }
            return Results.Ok(candidates);
        });

        // 某版本被标记为不可恢复的文件路径（还原时驱动逐文件替代选择）。
        group.MapGet("/{id:int}/unrecoverable", async (int id, int? version, IBackupConfigService svc, IAccountService accounts, IBackupInfoStore store, TrackedInfoStore trackedInfo, CancellationToken ct) =>
        {
            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();
            var account = await accounts.GetAsync(config.AccountId, ct);
            if (account is null)
                return Results.BadRequest(new { error = "Account not found." });

            var password = string.IsNullOrEmpty(config.Password) ? null : config.Password;
            var info = await trackedInfo.LoadAsync(account, config.ContainerName, password, ct);
            if (info is null || info.Versions.Count == 0)
                return Results.Ok(Array.Empty<string>());
            var ver = version is { } vv ? info.Versions.FirstOrDefault(x => x.Version == vv) : info.Versions[^1];
            if (ver is null)
                return Results.Ok(Array.Empty<string>());
            var idx = await store.ReadIndexAsync(account, config.ContainerName, ver.IndexBlob, password, ct);
            return Results.Ok(idx.UnrecoverablePaths);
        });

        // 还原懒加载目录树（§4.1a，决策 1）：返回 path 目录的直接子节点（子目录+文件），供前端逐层展开，
        // 不必一次性拉整棵树。数据源为版本索引，本地权威缓存优先，缺失/身份不符才回落云端（ILocalIndexCache.ReadAsync 内部处理）。
        group.MapGet("/{id:int}/tree", async (int id, int? version, string? path,
            IBackupConfigService svc, IAccountService accounts, TrackedInfoStore trackedInfo, ILocalIndexCache indexCache,
            CancellationToken ct) =>
        {
            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();
            var account = await accounts.GetAsync(config.AccountId, ct);
            if (account is null)
                return Results.BadRequest(new { error = "Account not found." });

            var password = string.IsNullOrEmpty(config.Password) ? null : config.Password;
            var info = await trackedInfo.LoadAsync(account, config.ContainerName, password, ct);
            if (info is null || info.Versions.Count == 0)
                return Results.Ok(Array.Empty<TreeNode>());
            var ver = version is { } vv ? info.Versions.FirstOrDefault(x => x.Version == vv) : info.Versions[^1];
            if (ver is null)
                return Results.NotFound(new { error = "Version not found." });

            var identity = info.Backup.CreatedAt.UtcTicks;
            var idx = await indexCache.ReadAsync(account, config.ContainerName, ver.Version, identity, ver.IndexBlob, password, ct);
            return Results.Ok(VersionTreeService.Children(idx, path));
        });

        // 从本地修复云端损坏/缺失的 blob（显式动作，后台 job）：持忙碌锁到完成，期间该备份不能做别的。修不了的标记不可恢复。
        group.MapPost("/{id:int}/repair", async (int id, int? version, CloudCheckLevel? cloud, StorageTier? rehydrate, bool? cleanupOrphans, IBackupConfigService svc, RepairRunner runner, CancellationToken ct) =>
        {
            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();
            var state = runner.Start(id, version, cloud ?? CloudCheckLevel.ExistenceSize, rehydrate, cleanupOrphans ?? false);
            return Results.Accepted($"/api/backup-configs/{id}/repair", RepairRunResponse.From(state));
        });

        group.MapGet("/{id:int}/repair", (int id, RepairRunner runner) =>
        {
            var state = runner.Get(id);
            return state is null ? Results.NotFound() : Results.Ok(RepairRunResponse.From(state));
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
        group.MapPost("/{id:int}/check", async (int id, int? version, CloudCheckLevel? cloud, LocalCheckLevel? local, StorageTier? rehydrate, bool? listOrphans, IBackupConfigService svc, IAccountService accounts, BackupChecker checker, IGlobalSettingsService settingsSvc, BackupBusyTracker busy, CancellationToken ct) =>
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
                    ListOrphans = listOrphans ?? false,
                };
                var result = await checker.CheckAsync(account, config.ContainerName, password, version, options, config.LocalRoot, ct,
                    downloadConcurrency: settings.DownloadConcurrency > 0 ? settings.DownloadConcurrency : 5);
                // Check 完成（无论是否发现问题）算成功；只有异常才置 Error（决策 2）。
                // best-effort：状态回写失败不应把已成功的检查结果变成 500。
                try { await svc.SetNormalAsync(id, ct); } catch { /* best-effort */ }
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                try { await svc.SetErrorAsync(id, ex.Message, ct); } catch { /* best-effort */ }
                throw;
            }
            finally
            {
                busy.Release(account.Id, config.ContainerName);
            }
        });

        return app;
    }

    /// <summary>
    /// 瞬时态派生（不落库，§4.2 决策 2）：优先看各 runner 对该 config id 是否有正在跑的运行态
    /// （BackupRunner/RestoreRunner/RepairRunner 已各自暴露 <c>Get(id)</c>，无需新增 accessor）；
    /// 都没有但 BackupBusyTracker 显示该 (账户,container) 忙碌，则视为 Checking——
    /// 检查端点 (/check) 与计划任务的备份/清理都同步持锁运行，不经过任何 Runner。
    /// </summary>
    private static string DeriveActivity(
        BackupConfig c, BackupRunner backupRunner, RestoreRunner restoreRunner, RepairRunner repairRunner, BackupBusyTracker busy)
    {
        if (backupRunner.Get(c.Id)?.Status == RunStatus.Running)
            return "BackingUp";
        if (restoreRunner.Get(c.Id)?.Status == RunStatus.Running)
            return "Restoring";
        if (repairRunner.Get(c.Id)?.Status == RunStatus.Running)
            return "Repairing";
        if (busy.IsBusy(c.AccountId, c.ContainerName))
            return "Checking";
        return "Idle";
    }
}
