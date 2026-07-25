using Azure;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>备份配置管理端点（PRD §11 向导产物的持久化）。响应不含密码；更新时空密码保留原值。</summary>
public static class BackupConfigEndpoints
{
    public static IEndpointRouteBuilder MapBackupConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/backup-configs").WithTags("BackupConfigs");

        group.MapGet("/", async (IBackupConfigService svc, BackupRunner backupRunner, RestoreRunner restoreRunner, RepairRunner repairRunner, BackupBusyTracker busy, IKeyringHealth keyring, IEncryptionService encryption, CancellationToken ct) =>
        {
            var list = await svc.ListAsync(ct);
            return Results.Ok(list.Select(c =>
                BackupConfigResponse.From(c, DeriveActivity(c, backupRunner, restoreRunner, repairRunner, busy), Pending(keyring, encryption, c))));
        });

        group.MapGet("/{id:int}", async (int id, IBackupConfigService svc, BackupRunner backupRunner, RestoreRunner restoreRunner, RepairRunner repairRunner, BackupBusyTracker busy, IKeyringHealth keyring, IEncryptionService encryption, CancellationToken ct) =>
        {
            var c = await svc.GetAsync(id, ct);
            return c is null
                ? Results.NotFound()
                : Results.Ok(BackupConfigResponse.From(c, DeriveActivity(c, backupRunner, restoreRunner, repairRunner, busy), Pending(keyring, encryption, c)));
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
        group.MapPost("/import", async (ImportRequest req, IAccountService accounts, TrackedInfoStore trackedInfo, IBackupConfigService svc, ILocalIndexCache indexCache, IEncryptionService encryption, IKeyringHealth keyring, CancellationToken ct) =>
        {
            var account = await accounts.GetAsync(req.AccountId, ct);
            if (account is null)
                return Results.BadRequest(new { error = "Account not found." });

            (BackupInfoFile Info, string ETag)? seeded;
            try
            {
                seeded = await trackedInfo.SeedFromCloudAsync(account, req.ContainerName, req.Password, ct);
            }
            catch (SecretUnavailableException)
            {
                // 密钥环丢失导致读不了账户密钥，与备份密码无关——不能把责任推给用户输的密码
                // （与 reset-password 同处理）。
                return Results.BadRequest(new { error = "Re-enter this account's credentials first." });
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
                // 请求体里的密码是明文，落到实体上时立即加密（设计 §3.1）。
                PasswordProtected = info.Backup.Encrypted && !string.IsNullOrEmpty(req.Password)
                    ? encryption.Encrypt(req.Password)
                    : null,
            };
            var created = await svc.CreateAsync(config, ct);

            // 下载全部版本索引到本地缓存（版本文件是 metadata、不在 Archive）：之后备份/清理平时不再下载云端索引。
            var identity = info.Backup.CreatedAt.UtcTicks;
            foreach (var v in info.Versions)
                await indexCache.ReadAsync(account, req.ContainerName, v.Version, identity, v.IndexBlob, req.Password, ct);

            return Results.CreatedAtRoute("GetBackupConfig", new { id = created.Id }, BackupConfigResponse.From(created, secretsUnavailable: Pending(keyring, encryption, created)));
        });

        group.MapPost("/", async (BackupConfigRequest req, IBackupConfigService svc, IEncryptionService encryption, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.LocalRoot))
                return Results.BadRequest(new { error = "LocalRoot is required." });
            if (string.IsNullOrWhiteSpace(req.ContainerName))
                return Results.BadRequest(new { error = "ContainerName is required." });

            var created = await svc.CreateAsync(req.ToConfig(encryption), ct);
            return Results.CreatedAtRoute("GetBackupConfig", new { id = created.Id }, BackupConfigResponse.From(created, secretsUnavailable: Pending(keyring, encryption, created)));
        });

        group.MapPut("/{id:int}", async (int id, BackupConfigRequest req, IBackupConfigService svc, IEncryptionService encryption, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (await svc.GetAsync(id, ct) is null)
                return Results.NotFound();

            // 空密码 = 保留原值、非空 = 拒绝（决策 8），均由服务层判定：密文含随机 IV，不能在此比较。
            var update = req.ToConfig(encryption);

            try
            {
                var result = await svc.UpdateAsync(id, update, ct);
                return result is null ? Results.NotFound() : Results.Ok(BackupConfigResponse.From(result, secretsUnavailable: Pending(keyring, encryption, result)));
            }
            catch (InvalidOperationException ex)
            {
                // 基础字段创建后锁定（§4.5）：账户/container/本地根/Tier/加密性变更被拒。
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // deleteContainer=true（默认 false）：连云端 container 整体删除（不可逆，§4.3）。先删云端再删本地配置，
        // 避免云端删除失败时本地记录已丢失、用户无法重试。
        group.MapDelete("/{id:int}", async (int id, bool? deleteContainer, IBackupConfigService svc, IAccountService accounts, IContainerService containers, IOperationLog log, ILocalIndexCache indexCache, ILocalBackupStateStore localState, IKeyringHealth keyring, KeyringRecovery recovery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();

            // 先于删配置行捕获 account/container：本地缓存/状态按 (accountId, container) 归属，配置行删完就拿不到了。
            var accountId = config.AccountId;
            var container = config.ContainerName;

            if (deleteContainer ?? false)
            {
                // 只有连删云端 container 这一支需要账户密钥 → 密钥环丢失时 409。
                // deleteContainer=false 那一支纯本地，必须保持不设闸门：它是决策 6 下
                // 「想不起备份密码」的唯一出口，恢复模式里也得能走。
                if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

                var account = await accounts.GetAsync(accountId, ct);
                if (account is null)
                    return Results.BadRequest(new { error = "Account not found." });
                await containers.DeleteContainerAsync(account, container, ct);
            }

            var ok = await svc.DeleteAsync(id, ct);
            if (ok)
            {
                // 善后清理各自 best-effort：配置行已删（主操作成功），单步失败不应回 500、也不阻断其余步骤。
                // 残留的孤儿日志/缓存/状态无害且可被后续清理/重建覆盖。
                var logger = loggerFactory.CreateLogger("BackupConfigDelete");
                await BestEffort(logger, "delete audit logs",
                    () => log.DeleteForContainerAsync(accountId, container, ct)); // 连带删审计日志（PRD 3.6）
                // 连带清本地权威缓存/状态（本地权威原则，设计 §3.3）：否则同 account+container 重建备份时会
                // 命中孤儿的 CachedVersionIndex/LocalBackupState 行，与新备份的版本身份错配。
                await BestEffort(logger, "evict local index cache",
                    () => indexCache.RemoveForContainerAsync(accountId, container, ct));
                await BestEffort(logger, "remove local backup state",
                    () => localState.RemoveAsync(accountId, container, ct));

                // 删掉的可能正是唯一一条待重设的解不开的密文（备份密码）：不收尾就翻不回 Healthy，
                // 用户直到下次重启前都会卡在「Lost 但无一条待重设」的死角（设计 §3.4 fix）。
                await recovery.TryCompleteAsync(ct);
            }
            return ok ? Results.NoContent() : Results.NotFound();
        });

        // 启动一次备份（后台运行，进度轮询）
        group.MapPost("/{id:int}/run", async (int id, IBackupConfigService svc, BackupRunner runner, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

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
        group.MapPost("/{id:int}/restore", async (int id, RestoreRequestBody body, IBackupConfigService svc, RestoreRunner runner, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();

            var target = string.IsNullOrWhiteSpace(body.TargetRoot) ? config.LocalRoot : body.TargetRoot;
            var state = runner.Start(id, target, body.Version, body.Substitutions, body.SelectedPaths, body.Conflict, body.RehydratePriority);
            return Results.Accepted($"/api/backup-configs/{id}/restore", RestoreRunResponse.From(state));
        });

        // 某路径可从哪些版本恢复（含该路径且有存储、且未标记不可恢复的版本，就近排序），供还原时逐文件替代选择。
        group.MapGet("/{id:int}/file-versions", async (int id, string path, IBackupConfigService svc, IAccountService accounts, IBackupInfoStore store, TrackedInfoStore trackedInfo, ISecretReader secrets, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();
            var account = await accounts.GetAsync(config.AccountId, ct);
            if (account is null)
                return Results.BadRequest(new { error = "Account not found." });

            var password = secrets.RevealBackupPassword(config);
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
        group.MapGet("/{id:int}/unrecoverable", async (int id, int? version, IBackupConfigService svc, IAccountService accounts, IBackupInfoStore store, TrackedInfoStore trackedInfo, ISecretReader secrets, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();
            var account = await accounts.GetAsync(config.AccountId, ct);
            if (account is null)
                return Results.BadRequest(new { error = "Account not found." });

            var password = secrets.RevealBackupPassword(config);
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
            ISecretReader secrets, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();
            var account = await accounts.GetAsync(config.AccountId, ct);
            if (account is null)
                return Results.BadRequest(new { error = "Account not found." });

            var password = secrets.RevealBackupPassword(config);
            var info = await trackedInfo.LoadAsync(account, config.ContainerName, password, ct);
            if (info is null || info.Versions.Count == 0)
                return Results.Ok(Array.Empty<TreeNode>());
            var ver = version is { } vv ? info.Versions.FirstOrDefault(x => x.Version == vv) : info.Versions[^1];
            if (ver is null)
                return Results.Ok(Array.Empty<TreeNode>()); // 指定版本不存在 → 空结果，与 /unrecoverable、/file-versions 一致

            var identity = info.Backup.CreatedAt.UtcTicks;
            var idx = await indexCache.ReadAsync(account, config.ContainerName, ver.Version, identity, ver.IndexBlob, password, ct);
            return Results.Ok(VersionTreeService.Children(idx, path));
        });

        // 还原下载量/解压量估算（§4.1b，需求 A + 决策 5）：选中路径先本地纯算下载量（去重后的存储对象体积合计，
        // 共享 pack/去重 blob 只计一次）与解压量，再对各去重对象首卷发起 HEAD 查活化状态（Archive/待就绪）。
        group.MapPost("/{id:int}/restore-estimate", async (int id, RestoreEstimateRequestBody body,
            IBackupConfigService svc, IAccountService accounts, TrackedInfoStore trackedInfo, ILocalIndexCache indexCache,
            IBlobClientFactory factory, IGlobalSettingsService settingsSvc, ISecretReader secrets, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();
            var account = await accounts.GetAsync(config.AccountId, ct);
            if (account is null)
                return Results.BadRequest(new { error = "Account not found." });

            var password = secrets.RevealBackupPassword(config);
            var info = await trackedInfo.LoadAsync(account, config.ContainerName, password, ct);
            if (info is null || info.Versions.Count == 0)
                return Results.NotFound(new { error = "No versions found." });
            var ver = body.Version is { } vv ? info.Versions.FirstOrDefault(x => x.Version == vv) : info.Versions[^1];
            if (ver is null)
                return Results.NotFound(new { error = "Version not found." });

            var identity = info.Backup.CreatedAt.UtcTicks;
            var idx = await indexCache.ReadAsync(account, config.ContainerName, ver.Version, identity, ver.IndexBlob, password, ct);
            var estimate = RestoreEstimator.Compute(idx, info, body.Paths ?? []);

            // 各去重存储对象的首卷 blob 名 + 分卷数（pack 用 PackInfo.Volumes；blob 用该 Ref 首个条目的 StorageRef.Volumes）。
            var volumesByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var e in idx.Entries)
            {
                if (e.Storage is null) continue;
                var key = e.Storage.Kind == "pack" ? "pack:" + e.Storage.Ref : "blob:" + e.Storage.Ref;
                if (volumesByKey.ContainsKey(key)) continue;
                volumesByKey[key] = e.Storage.Kind == "pack"
                    ? (info.Packs.TryGetValue(e.Storage.Ref, out var pack) ? pack.Volumes : 1)
                    : e.Storage.Volumes;
            }

            var container = factory.CreateServiceClient(account).GetBlobContainerClient(config.ContainerName);
            var settings = await settingsSvc.GetAsync(ct);
            var concurrency = settings.DownloadConcurrency > 0 ? settings.DownloadConcurrency : 5;
            using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
            var archived = 0;
            var rehydratePending = 0;
            await Task.WhenAll(estimate.DistinctObjects.Select(async key =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var baseName = key.StartsWith("pack:", StringComparison.Ordinal) ? $"packs/{key[5..]}.7z" : key[5..];
                    var volumes = volumesByKey.GetValueOrDefault(key, 1);
                    var firstVolume = VolumeBlobIO.VolumeNames(baseName, volumes)[0];
                    var props = (await container.GetBlobClient(firstVolume).GetPropertiesAsync(cancellationToken: ct)).Value;
                    if (props.AccessTier == "Archive")
                        Interlocked.Increment(ref archived);
                    if (!string.IsNullOrEmpty(props.ArchiveStatus))
                        Interlocked.Increment(ref rehydratePending);
                }
                catch (RequestFailedException)
                {
                    // best effort：单个对象 HEAD 失败（如已被删）不影响其余对象的估算，直接跳过其活化计数。
                }
                finally
                {
                    gate.Release();
                }
            }));

            return Results.Ok(new
            {
                downloadBytes = estimate.DownloadBytes,
                uncompressedBytes = estimate.UncompressedBytes,
                fileCount = estimate.FileCount,
                archivedObjects = archived,
                rehydratePending,
            });
        });

        // 从本地修复云端损坏/缺失的 blob（显式动作，后台 job）：持忙碌锁到完成，期间该备份不能做别的。修不了的标记不可恢复。
        group.MapPost("/{id:int}/repair", async (int id, int? version, CloudCheckLevel? cloud, StorageTier? rehydrate, bool? cleanupOrphans, IBackupConfigService svc, RepairRunner runner, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

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
        group.MapGet("/{id:int}/versions", async (int id, IBackupConfigService svc, IAccountService accounts, TrackedInfoStore trackedInfo, ISecretReader secrets, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();
            var account = await accounts.GetAsync(config.AccountId, ct);
            if (account is null)
                return Results.BadRequest(new { error = "Account not found." });

            var password = secrets.RevealBackupPassword(config);
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
        group.MapPost("/{id:int}/check", async (int id, int? version, CloudCheckLevel? cloud, LocalCheckLevel? local, StorageTier? rehydrate, bool? listOrphans, IBackupConfigService svc, IAccountService accounts, BackupChecker checker, IGlobalSettingsService settingsSvc, BackupBusyTracker busy, ISecretReader secrets, IKeyringHealth keyring, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

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
                var password = secrets.RevealBackupPassword(config);
                var settings = await settingsSvc.GetAsync(ct);
                var options = new CheckOptions
                {
                    Cloud = cloud ?? CloudCheckLevel.ExistenceSize,
                    Local = local ?? LocalCheckLevel.Content,
                    // 显式转为 AccessTier?：AccessTier 有 string 隐式转换构造函数，三元表达式与裸 null 混用时
                    // 编译器会走「null→string→AccessTier(string)」这条隐式转换路径而非「AccessTier→AccessTier?」，
                    // 导致 rehydrate 为空时对 AccessTier(string) 传 null 触发 ArgumentNullException（真实生产 bug）。
                    RehydrateTier = rehydrate is { } t ? (AccessTier?)BackupRequestMapper.MapTier(t) : null,
                    ListOrphans = listOrphans ?? false,
                };
                var result = await checker.CheckAsync(account, config.ContainerName, password, version, options, config.LocalRoot, ct,
                    downloadConcurrency: settings.DownloadConcurrency > 0 ? settings.DownloadConcurrency : 5);
                // Check 完成（无论是否发现问题）算成功；只有异常才置 Error（决策 2）。
                // best-effort：状态回写失败不应把已成功的检查结果变成 500。
                await svc.WriteStatusAsync(id, error: null, loggerFactory.CreateLogger("BackupStatus"), ct);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                await svc.WriteStatusAsync(id, ex.Message, loggerFactory.CreateLogger("BackupStatus"), ct);
                throw;
            }
            finally
            {
                busy.Release(account.Id, config.ContainerName);
            }
        });

        // 备份密码重设（设计 §3.4）。验证依据：加密备份的信息文件本身就是用该密码加密的 7z，
        // 它是元数据根节点、容器内最小的加密对象，解得开即证明密码正确。
        group.MapPost("/{id:int}/reset-password", async (
            int id, ResetBackupPasswordRequest req, IBackupConfigService svc, IAccountService accounts,
            IBackupInfoStore store, IEncryptionService encryption, AppDbContext db,
            KeyringRecovery recovery, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(req.Password))
                return Results.BadRequest(new { error = "Password is required." });

            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();
            if (string.IsNullOrEmpty(config.PasswordProtected))
                return Results.BadRequest(new { error = "This backup is not encrypted; there is no password to restore." });

            var account = await accounts.GetAsync(config.AccountId, ct);
            if (account is null)
                return Results.BadRequest(new { error = "Account not found." });

            // 顺序依赖：连云需要账户密钥，故账户必须先恢复。
            try
            {
                // 纯读，不可用 TrackedInfoStore.SeedFromCloudAsync——那会回填本地权威状态。
                var info = await store.ReadInfoWithETagAsync(account, config.ContainerName, req.Password, ct);
                if (info is null)
                    return Results.BadRequest(new { error = "No backup info file found in the container." });
                // ReadInfoWithETagAsync 优先探测未加密 blob 名——若容器里恰好有一份未加密的信息文件，
                // 它会用 password: null 读回来，提交的密码根本没被用于解密。必须核对返回内容确实来自
                // 加密对象，否则会把任意字符串当密码落库，真密码永久丢失。
                if (!info.Value.Info.Backup.Encrypted)
                    return Results.BadRequest(new { error = "This container's backup is not encrypted; the password cannot be verified." });
            }
            catch (SecretUnavailableException)
            {
                return Results.BadRequest(new { error = "Re-enter this backup's account credentials first." });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = $"Verification failed: {ex.Message}" });
            }

            var row = await db.BackupConfigs.FirstAsync(c => c.Id == id, ct);
            row.PasswordProtected = encryption.Encrypt(req.Password);
            await db.SaveChangesAsync(ct);

            await recovery.TryCompleteAsync(ct);
            return Results.NoContent();
        });

        return app;
    }

    /// <summary>
    /// 该备份配置是否仍待重设密码。Healthy 时短路，列表端点不触发任何解密（设计 §3.1 的核心性质）；
    /// Lost 时逐条试解，使已重设成功的备份立刻停止显示「待重设」（设计 §3.3）。
    /// </summary>
    private static bool Pending(IKeyringHealth keyring, IEncryptionService encryption, BackupConfig config) =>
        keyring.Status == KeyringStatus.Lost && SecretAvailability.Unreadable(encryption, config);

    /// <summary>
    /// 瞬时态派生（不落库，§4.2 决策 2）：优先看各 runner 对该 config id 是否有正在跑的运行态
    /// （BackupRunner/RestoreRunner/RepairRunner 已各自暴露 <c>Get(id)</c>，无需新增 accessor）；
    /// 都没有但 BackupBusyTracker 显示忙碌，则取其记录的操作标签（BackingUp/Checking/CleaningUp）——
    /// 检查端点 (/check) 与计划任务的备份/检查/清理都同步持锁运行，不经过任何 Runner。
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
        // 非 Runner 的持锁操作（手动 /check、计划 备份/检查/清理）：读忙碌跟踪记录的实际操作标签，
        // 避免把计划备份/清理一律误标为 Checking。
        return busy.CurrentActivity(c.AccountId, c.ContainerName) ?? "Idle";
    }

    /// <summary>删配置的善后步骤：吞异常并记 Warning，一步失败不阻断其余、也不把已成功的主删除变成 500。</summary>
    private static async Task BestEffort(ILogger logger, string what, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { logger.LogWarning(ex, "Backup config delete: failed to {What}", what); }
    }
}
