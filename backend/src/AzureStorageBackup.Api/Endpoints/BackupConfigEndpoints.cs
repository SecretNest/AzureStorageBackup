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

        group.MapGet("/", async (IBackupConfigService svc, BackupRunner backupRunner, RestoreRunner restoreRunner, RepairRunner repairRunner, CheckRunner checkRunner, BackupBusyTracker busy, IKeyringHealth keyring, IEncryptionService encryption, IGlobalSettingsService settingsSvc, CancellationToken ct) =>
        {
            var list = await svc.ListAsync(ct);
            var settings = await settingsSvc.GetAsync(ct);
            return Results.Ok(list.Select(c =>
                BackupConfigResponse.From(c, settings, DeriveActivity(c, backupRunner, restoreRunner, repairRunner, checkRunner, busy), Pending(keyring, encryption, c))));
        });

        group.MapGet("/{id:int}", async (int id, IBackupConfigService svc, BackupRunner backupRunner, RestoreRunner restoreRunner, RepairRunner repairRunner, CheckRunner checkRunner, BackupBusyTracker busy, IKeyringHealth keyring, IEncryptionService encryption, IGlobalSettingsService settingsSvc, CancellationToken ct) =>
        {
            var c = await svc.GetAsync(id, ct);
            if (c is null)
                return Results.NotFound();
            var settings = await settingsSvc.GetAsync(ct);
            return Results.Ok(BackupConfigResponse.From(c, settings, DeriveActivity(c, backupRunner, restoreRunner, repairRunner, checkRunner, busy), Pending(keyring, encryption, c)));
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
        group.MapPost("/import", async (ImportRequest req, IAccountService accounts, TrackedInfoStore trackedInfo, IBackupConfigService svc, ILocalIndexCache indexCache, IEncryptionService encryption, IKeyringHealth keyring, IOperationLog log, IGlobalSettingsService settingsSvc, CheckRunner checkRunner, CancellationToken ct) =>
        {
            var account = await accounts.GetAsync(req.AccountId, ct);
            if (account is null)
                return Results.BadRequest(new { error = "Account not found." });
            // 排在读云之前：本地就能回答的问题不该先花一趟网络，何况那趟网络还会把云端信息文件
            // 种进 TrackedInfoStore——为一次注定要被拒绝的导入改动本地权威状态，是白留一份脏数据。
            if (await svc.FindAsync(req.AccountId, req.ContainerName, ct) is { } holder)
                return Results.Conflict(new { error = ContainerTaken(req.ContainerName, holder.Name) });

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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 取消（客户端断开 / 进程关停）不是「密码不对」：吞掉会把它伪装成用户错误，
                // 与仓库既有约定一致（见 a3ac967「孤儿清理不吞取消」），一律放行上抛。
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

            // B2：无 SourceRootHint 时 LocalRoot 落成空串，一旦设了 Backup__Root，这条配置之后
            // 任何操作都会撞上和「本地根真的越界」一模一样的 409 path_outside_root——IsInside 对
            // 空串和真正界外路径给出的是同一个拒绝，操作员从响应里分不清是自己配错了根，还是这份
            // 导入压根没抓到路径提示。这里在导入当下就把原因摊开，写进操作日志，而不是留到下次
            // 手滑点了 run 才让人一头雾水。
            if (string.IsNullOrEmpty(info.Backup.SourceRootHint))
            {
                await log.AppendAsync(
                    OperationLogLevel.Warning, $"import:{req.AccountId}/{req.ContainerName}",
                    $"Imported '{config.Name}' without a local root hint (LocalRoot is empty); " +
                    "set Local Root on this backup before running it.",
                    ct);
            }

            // 下载全部版本索引到本地缓存（版本文件是 metadata、不在 Archive）：备份/清理/还原此后
            // 一律读本地这一份，云端不再问——导入之后就没有"没有本地权威"这种状态了。
            //
            // 某个版本的索引读不出来**不中断整次导入**：坏的只是那一个版本，其余版本连同这条配置
            // 都还是好的，而配置建起来了用户才有地方去查、去修。把它写进操作日志，下面那次自动
            // 检查也会把牵连到的东西一并列出来。
            var identity = info.Backup.CreatedAt.UtcTicks;
            var unreadable = new List<int>();
            foreach (var v in info.Versions)
            {
                try
                {
                    await indexCache.ReadAsync(account, req.ContainerName, v.Version, identity, v.IndexBlob, req.Password, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    unreadable.Add(v.Version);
                    await log.AppendAsync(
                        OperationLogLevel.Warning, $"import:{req.AccountId}/{req.ContainerName}",
                        $"Could not read the file list of version {v.Version} ({v.IndexBlob}): {ex.Message}. "
                        + "That version cannot be restored or checked; the rest of this backup is unaffected.",
                        ct);
                }
            }
            if (unreadable.Count > 0)
            {
                await log.AppendAsync(
                    OperationLogLevel.Warning, $"import:{req.AccountId}/{req.ContainerName}",
                    $"Imported '{config.Name}' with {unreadable.Count} unreadable version file(s): "
                    + string.Join(", ", unreadable.Select(v => $"v{v}")) + ".",
                    ct);
            }

            // 账本抓全了，接着核一次账：云端那些 data blob 和分卷是不是都还在、尺寸对不对。
            // 只发 HEAD，不下载。**不查本地**——导入这一刻 LocalRoot 多半还是空的（信息文件里
            // 没有 SourceRootHint 时就是如此），拿它比对只会满屏报"本地缺失"。
            // 走内部调用而不是打自己的 /check 端点，正是为了绕开那上面的 LocalRoot 边界闸门。
            var checkStarted = req.CheckAfterImport ?? true;
            if (checkStarted)
            {
                checkRunner.Start(created.Id, version: null, new CheckOptions
                {
                    Cloud = CloudCheckLevel.ExistenceSize,
                    Local = LocalCheckLevel.None,
                });
            }

            var importSettings = await settingsSvc.GetAsync(ct);
            return Results.CreatedAtRoute("GetBackupConfig", new { id = created.Id }, new ImportResponse(
                BackupConfigResponse.From(created, importSettings, secretsUnavailable: Pending(keyring, encryption, created)),
                checkStarted,
                unreadable));
        });

        group.MapPost("/", async (BackupConfigRequest req, IBackupConfigService svc, IAccountService accounts, IEncryptionService encryption, IKeyringHealth keyring, PathBoundary boundary, IGlobalSettingsService settingsSvc, CancellationToken ct) =>
        {
            // 向导没有任何强校验的欠账（review 发现）：先做便宜的本地字符串检查，
            // 再做不碰文件系统/数据库的路径边界检查，最后才是需要查库的账户存在性检查——
            // 让最贵的检查排在最后，任何一步不合格都不必再往下走。
            if (string.IsNullOrWhiteSpace(req.LocalRoot))
                return Results.BadRequest(new { error = "LocalRoot is required." });
            if (string.IsNullOrWhiteSpace(req.ContainerName))
                return Results.BadRequest(new { error = "ContainerName is required." });
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });
            if (PathBoundaryGuard.Blocked(boundary, req.LocalRoot) is { } outside) return outside;
            if (await accounts.GetAsync(req.AccountId, ct) is null)
                return Results.BadRequest(new { error = "Account not found." });
            if (await svc.FindAsync(req.AccountId, req.ContainerName, ct) is { } holder)
                return Results.Conflict(new { error = ContainerTaken(req.ContainerName, holder.Name) });

            var created = await svc.CreateAsync(req.ToConfig(encryption), ct);
            var settings = await settingsSvc.GetAsync(ct);
            return Results.CreatedAtRoute("GetBackupConfig", new { id = created.Id }, BackupConfigResponse.From(created, settings, secretsUnavailable: Pending(keyring, encryption, created)));
        });

        group.MapPut("/{id:int}", async (int id, BackupConfigRequest req, IBackupConfigService svc, IEncryptionService encryption, IKeyringHealth keyring, IGlobalSettingsService settingsSvc, CancellationToken ct) =>
        {
            if (await svc.GetAsync(id, ct) is null)
                return Results.NotFound();
            // LocalRoot/ContainerName/Tier/加密性创建后锁定，已由 BackupConfigService.UpdateAsync 拒绝；
            // Name 不在锁定字段之列，仍可编辑，因此仍需在这里挡空白（与创建端点同一条规则）。
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });

            // 空密码 = 保留原值、非空 = 拒绝（决策 8），均由服务层判定：密文含随机 IV，不能在此比较。
            var update = req.ToConfig(encryption);

            try
            {
                var result = await svc.UpdateAsync(id, update, ct);
                if (result is null)
                    return Results.NotFound();
                var settings = await settingsSvc.GetAsync(ct);
                return Results.Ok(BackupConfigResponse.From(result, settings, secretsUnavailable: Pending(keyring, encryption, result)));
            }
            catch (InvalidOperationException ex)
            {
                // 基础字段创建后锁定（§4.5）：账户/container/本地根/Tier/加密性变更被拒。
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // deleteContainer=true（默认 false）：连云端 container 整体删除（不可逆，§4.3）。先删云端再删本地配置，
        // 避免云端删除失败时本地记录已丢失、用户无法重试。
        group.MapDelete("/{id:int}", async (int id, bool? deleteContainer, IBackupConfigService svc, IAccountService accounts, IContainerService containers, IOperationLog log, ILocalIndexCache indexCache, ILocalBackupStateStore localState, BackupJournalStore journals, IKeyringHealth keyring, KeyringRecovery recovery, BackupRunner backupRunner, RestoreRunner restoreRunner, RepairRunner repairRunner, CheckRunner checkRunner, BackupBusyTracker busy, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();

            // 正在跑操作时不许删。删配置**不会**停掉后台那次运行：它会继续跑完，继续占着
            // BackupBusyTracker 里 (account, container) 那把锁，而 _runs 是按 config id 存的，
            // 配置一删，界面就再也找不到它的进度——于是同一个 container 上新建的备份被拒（busy），
            // 状态却显示 BackingUp 且没有任何细节，像是凭空卡死。若同时勾了「删除 container」，
            // 那次运行还会继续往一个已经不存在的 container 上传。这些都是用户实际踩到的。
            var activity = DeriveActivity(config, backupRunner, restoreRunner, repairRunner, checkRunner, busy);
            if (activity != "Idle")
                return Results.Conflict(new
                {
                    error = $"This backup is currently {Humanize(activity)}. Wait for it to finish before deleting it.",
                });

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

                // 配置没了就再没人会来采纳这个容器上的 journal，留着它只会永远保住那批块不被清理
                // （清理判据认 journal，不认 configId）。**只删 journal 文件，不去删它引用的 blob**：
                // journal 记的既包括真上传，也包括 if-missing 命中，后者完全可能同时被一个已提交的
                // 版本索引引用着，删了就是把保留下来的版本挖穿。
                //
                // 失去保护之后由谁来收：等这个容器上再有配置时，**那个配置的第一轮备份**收尾会做
                // 一次孤儿扫描，用完整判据（读得到索引、认得出引用）把真孤儿扫掉。这条路成立靠的是
                // 紧挨着的上一步——localState.RemoveAsync 把本地权威状态清了，重建的配置因此认得出
                // 自己是第一轮（见 BackupOrchestrator 的 firstRun 与 BackupRunControl.SweepNeeded）。
                // 那两步的先后不重要（都是 best-effort，互不依赖），但少了那一步，这里就只剩
                // "用户恰好配了 Cleanup 计划任务"这一条指望，而那是配不配全凭他的。
                await BestEffort(logger, "discard backup journals",
                    () => { journals.DeleteAll(accountId, container); return Task.CompletedTask; });

                // 删掉的可能正是唯一一条待重设的解不开的密文（备份密码）：不收尾就翻不回 Healthy，
                // 用户直到下次重启前都会卡在「Lost 但无一条待重设」的死角（设计 §3.4 fix）。
                await recovery.TryCompleteAsync(ct);

                // 审计行写在清理**之后**：DeleteForContainerAsync 会把该 (account, container) 的日志
                // 全部删掉，写在前面等于自己把自己删了。删除是这里唯一会抹掉历史的操作，
                // 它本身却不留痕，日志页就会莫名其妙地整个变空——用户报过这个现象。
                //
                // 来源键带 accountId：这条曾是改版前的旧格式 "backup:{container}"，漏改了。少了
                // account 维度，两个账户下的同名 container 写出的是一模一样的行，谁也说不清是哪个；
                // 日志页按来源精确相等过滤，于是它哪个备份的视图里都不出现。写在清理之后这一点
                // 不受影响——那次清理早已跑完，它删不到自己。
                await BestEffort(logger, "record deletion", () => log.AppendAsync(
                    OperationLogLevel.Warning, $"backup:{accountId}/{container}",
                    (deleteContainer ?? false)
                        ? $"Backup config '{config.Name}' deleted, along with its cloud container."
                        : $"Backup config '{config.Name}' deleted; the cloud container was kept.",
                    ct, durable: true));
            }
            return ok ? Results.NoContent() : Results.NotFound();
        });

        // 启动一次备份（后台运行，进度轮询）
        group.MapPost("/{id:int}/run", async (int id, IBackupConfigService svc, BackupRunner runner, IKeyringHealth keyring, PathBoundary boundary, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();
            if (PathBoundaryGuard.Blocked(boundary, config.LocalRoot) is { } outside) return outside;

            var state = await runner.StartAsync(id);
            return Results.Accepted($"/api/backup-configs/{id}/run", BackupRunResponse.From(state));
        });

        // 查询运行进度/状态
        group.MapGet("/{id:int}/run", (int id, BackupRunner runner) =>
        {
            var state = runner.Get(id);
            return state is null ? Results.NotFound() : Results.Ok(BackupRunResponse.From(state));
        });

        // 挂起：安全落盘后停下，现场留着，下次点 Run 会原样接上。
        // **没有对应的 resume 端点**——恢复不是一种模式：每一轮备份开卷时都会去认还有效的 journal，
        // 所以"继续"就是再点一次 /run，走的是同一条执行体。
        group.MapPost("/{id:int}/suspend", async (int id, BackupRunner runner, CancellationToken ct) =>
            await StopAndWaitAsync(c => runner.SuspendAsync(id, c), ct) switch
            {
                StopOutcome.NothingRunning => Results.Conflict(new { error = "No backup is running." }),
                StopOutcome.StillStopping => Results.Accepted($"/api/backup-configs/{id}/run", new { stopping = true }),
                _ => Results.NoContent(),
            });

        // 卡在瞬时错误上自愈等待时，用户点「Retry now」不等计时器，立刻放行一次重试。
        group.MapPost("/{id:int}/retry-now", (int id, BackupRunner runner) =>
            runner.RetryNow(id)
                ? Results.NoContent()
                : Results.Conflict(new { error = "This backup is not waiting to retry." }));

        // 这个容器上有哪些中途停下的运行。程序刚起来时界面靠它把"有活儿没干完"摆出来等用户点，
        // 而不是替用户决定要不要接着跑。
        group.MapGet("/{id:int}/interrupted", async (
            int id, IBackupConfigService svc, BackupJournalStore journals, CancellationToken ct) =>
        {
            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();

            var runs = await journals.PeekAsync(config.AccountId, config.ContainerName, ct);
            return Results.Ok(runs.Select(r => new InterruptedRunResponse(
                r.RunId, r.Header.StartedAt, r.Records, r.SizeBytes,
                r.Header.ConfigId == id && r.Header.LocalRoot == config.LocalRoot)).ToList());
        });

        // 用户不想接着跑了：把现场丢掉。
        // 云上那批块并不在这里删——判断"它到底还被哪个版本引用着"要读版本索引，那需要备份密码，
        // 而这个端点拿不到。丢掉 journal 之后它们失去保护，下一次带孤儿扫描的清理会用完整判据收走
        // （Task 11）。
        group.MapDelete("/{id:int}/interrupted", async (
            int id, IBackupConfigService svc, BackupJournalStore journals, BackupRunner runner,
            CancellationToken ct) =>
        {
            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();
            // 正在跑的那一轮自己就握着一卷 journal，从它脚下把文件抽走只会让收尾时报一堆
            // 莫名其妙的 IO 错误。让用户先停下来。
            if (runner.Get(id) is { Status: RunStatus.Running })
                return Results.Conflict(new { error = "This backup is running; stop it first." });

            journals.DeleteAll(config.AccountId, config.ContainerName);
            return Results.NoContent();
        });

        // 启动还原（后台运行；targetRoot 缺省用配置的本地根，version 缺省用最新）
        group.MapPost("/{id:int}/restore", async (int id, RestoreRequestBody body, IBackupConfigService svc, RestoreRunner runner, IKeyringHealth keyring, PathBoundary boundary, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();

            var target = string.IsNullOrWhiteSpace(body.TargetRoot) ? config.LocalRoot : body.TargetRoot;
            if (PathBoundaryGuard.Blocked(boundary, target) is { } outside) return outside;
            var state = runner.Start(id, target, body.Version, body.Substitutions, body.SelectedPaths, body.Conflict, body.RehydratePriority);
            return Results.Accepted($"/api/backup-configs/{id}/restore", RestoreRunResponse.From(state));
        });

        // 某路径可从哪些版本恢复（含该路径且有存储、且未标记不可恢复的版本，就近排序），供还原时逐文件替代选择。
        group.MapGet("/{id:int}/file-versions", async (int id, string path, IBackupConfigService svc, IAccountService accounts, ILocalIndexCache indexCache, TrackedInfoStore trackedInfo, ISecretReader secrets, IKeyringHealth keyring, CancellationToken ct) =>
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
            // 本地权威缓存优先（与 /tree 一致）。这里尤其要紧：循环里**每个版本各读一次索引**，
            // 直接读云端就是每次点击「选择替代版本」下载 N 个索引 blob——延迟之外还是实打实的
            // Azure 出站流量费，而本地就有权威副本。
            var fvIdentity = info?.Backup.CreatedAt.UtcTicks ?? 0;
            foreach (var v in (info?.Versions ?? []).OrderByDescending(v => v.Version))
            {
                var idx = await indexCache.ReadAsync(account, config.ContainerName, v.Version, fvIdentity, v.IndexBlob, password, ct);
                if (idx.UnrecoverablePaths.Contains(path))
                    continue;
                var e = idx.Entries.FirstOrDefault(x => x.Path == path && x.Storage is not null);
                if (e is not null)
                    candidates.Add(new { v.Version, v.CreatedAt, length = e.Length });
            }
            return Results.Ok(candidates);
        });

        // 某版本被标记为不可恢复的文件路径（还原时驱动逐文件替代选择）。
        group.MapGet("/{id:int}/unrecoverable", async (int id, int? version, IBackupConfigService svc, IAccountService accounts, ILocalIndexCache indexCache, TrackedInfoStore trackedInfo, ISecretReader secrets, IKeyringHealth keyring, CancellationToken ct) =>
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
            var idx = await indexCache.ReadAsync(
                account, config.ContainerName, ver.Version, info.Backup.CreatedAt.UtcTicks, ver.IndexBlob, password, ct);
            return Results.Ok(idx.UnrecoverablePaths);
        });

        // 某版本里内容为**沿用**的文件：备份那几轮读不开源文件，索引沿用了更早版本的条目。
        // 与 /unrecoverable 对称，但语义不同：那边是数据已损坏、无内容可给；这边内容有效，只是旧。
        // 还原前需要知道这件事——否则还原了这个版本，却拿到更早时刻的内容而毫不知情。
        group.MapGet("/{id:int}/unreadable", async (int id, int? version, IBackupConfigService svc, IAccountService accounts, ILocalIndexCache indexCache, TrackedInfoStore trackedInfo, ISecretReader secrets, IKeyringHealth keyring, CancellationToken ct) =>
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
                return Results.Ok(Array.Empty<object>());
            var ver = version is { } vv ? info.Versions.FirstOrDefault(x => x.Version == vv) : info.Versions[^1];
            if (ver is null)
                return Results.Ok(Array.Empty<object>());
            var idx = await indexCache.ReadAsync(
                account, config.ContainerName, ver.Version, info.Backup.CreatedAt.UtcTicks, ver.IndexBlob, password, ct);
            return Results.Ok(idx.Entries
                .Where(e => e.UnreadableAt is not null)
                .Select(e => new { path = e.Path, unreadableAt = e.UnreadableAt })
                .ToList());
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
        group.MapPost("/{id:int}/repair", async (int id, int? version, CloudCheckLevel? cloud, StorageTier? rehydrate, bool? cleanupOrphans, IBackupConfigService svc, RepairRunner runner, IKeyringHealth keyring, PathBoundary boundary, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();
            if (PathBoundaryGuard.Blocked(boundary, config.LocalRoot) is { } outside) return outside;
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
            // 从新到旧：界面上 "Latest" 紧跟着的就该是次新的一版，与 /file-versions 的就近排序一致。
            var versions = (info?.Versions ?? []).OrderByDescending(v => v.Version).Select(v => new
            {
                v.Version,
                v.CreatedAt,
                v.StartedAt,   // 升级前写下的版本没有 → null，界面写「—」
                files = v.Stats.Files,
                bytes = v.Stats.Bytes,
                changedFiles = v.Stats.ChangedFiles,
            });
            return Results.Ok(versions);
        });

        // 完整性检查（Content 级下载解压重算 hash 深度校验）。**后台 job**：
        // 内容级检查要把整个备份下载一遍，几百 GB 就是几小时——同步端点时代请求会先被浏览器
        // 或反向代理超时掐断，检查白跑，而且全程没有任何进度可看。现在返回 202，用 GET 轮询。
        group.MapPost("/{id:int}/check", async (int id, int? version, CloudCheckLevel? cloud, LocalCheckLevel? local, StorageTier? rehydrate, bool? listOrphans, IBackupConfigService svc, CheckRunner runner, IKeyringHealth keyring, PathBoundary boundary, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();
            if (PathBoundaryGuard.Blocked(boundary, config.LocalRoot) is { } outside) return outside;

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
            var state = runner.Start(id, version, options);
            return Results.Accepted($"/api/backup-configs/{id}/check", CheckRunResponse.From(state));
        });

        // 最近一次检查的状态与报告。跑完之后**报告仍然留着**：关掉对话框再打开要能看回结果。
        group.MapGet("/{id:int}/check", (int id, CheckRunner runner) =>
        {
            var state = runner.Get(id);
            // 「从没查过」不是错误：检查对话框一打开就问一次，而 404 会在浏览器控制台留下一条
            // 红色报错，看上去像故障（用户就是这么报上来的）。204 = 没有可报告的检查。
            return state is null ? Results.NoContent() : Results.Ok(CheckRunResponse.From(state));
        });

        // 停止该备份上正在跑的操作。what 省略＝全部停；否则只停指定的一种
        // （一个配置可能同时在备份和还原——还原刻意不占忙碌锁，见 RestoreRunner 顶部注释）。
        //
        // 在此之前根本没有"停"这个动作：一次跑错了的备份只能等它跑完，或者重启整个容器——
        // 而用户跑在 NAS 上，重启会连带停掉别的服务；「正忙时不许删配置」又把删除这条退路堵上了。
        group.MapPost("/{id:int}/cancel", async (int id, string? what, bool? finishCurrentFiles,
            IBackupConfigService svc,
            BackupRunner backupRunner, RestoreRunner restoreRunner, RepairRunner repairRunner, CheckRunner checkRunner,
            CancellationToken ct) =>
        {
            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();

            var canceled = new List<string>();
            var stopping = false;

            // 备份这一支是**等落盘再返回**的，另外三个仍是发个信号就走——它们没有需要落盘的现场。
            // finishCurrentFiles=true：正在传的文件（含它所有分卷）传完再停，这部分算数；
            // false：立刻停，半截的分卷和在途的块都删掉，不留没法用的残渣。
            if (Wanted(what, "backup"))
                switch (await StopAndWaitAsync(c => backupRunner.CancelAsync(id, finishCurrentFiles ?? false, c), ct))
                {
                    case StopOutcome.Settled: canceled.Add("backup"); break;
                    case StopOutcome.StillStopping: canceled.Add("backup"); stopping = true; break;
                }

            if (Wanted(what, "restore") && restoreRunner.Cancel(id)) canceled.Add("restore");
            if (Wanted(what, "repair") && repairRunner.Cancel(id)) canceled.Add("repair");
            if (Wanted(what, "check") && checkRunner.Cancel(id)) canceled.Add("check");

            // 除备份外，停止仍是异步的：这里只发出取消信号，运行本身要等到下一个取消检查点才真的收尾。
            // 界面据此把按钮改成「Stopping…」，而不是立刻当成已经停了。
            return canceled.Count == 0
                ? Results.Conflict(new { error = "Nothing is running for this backup." })
                : Results.Ok(new { canceled, stopping });
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
                //
                // 这里查的是返回 JSON 里的自述标志位，而不是「解密成功」本身，看着像个洞，其实不是：
                // 写侧最终只有 BackupInfoStore.WriteInfoConditionalAsync 一条路，它按 password 是否为空
                // 二选一地决定 blob 名（IndexBlobName / EncryptedIndexBlobName），而 Backup.Encrypted
                // 由同一次写入的内容携带。因此经本应用写出的信息文件，标志位与所在 blob 名（即是否加密）
                // 不可能相左：Encrypted=true 意味着这份 JSON 只能来自 .enc 那条分支，也就意味着上面这次读
                // 确实是用提交的密码解开的。
                //
                // 「只有一条路」是可核验的，别被调用点数量迷惑：接口上另有 IBackupInfoStore.WriteInfoAsync，
                // 且 BackupOrchestrator / BackupRepairer / RetentionCleaner 都直接调它——但它的实现体本身
                // 就是一句 `=> WriteInfoConditionalAsync(..., ifMatch: null, ct)`（BackupInfoStore.WriteInfoAsync），
                // 并不自己拼 blob 名。故不变量成立的充要条件是：**WriteInfoAsync 继续保持这层委托，
                // 且不新增第三个自行决定 blob 名的写入实现**。任一条被打破，此处必须改成以解密结果为准。
                if (!info.Value.Info.Backup.Encrypted)
                    return Results.BadRequest(new { error = "This container's backup is not encrypted; the password cannot be verified." });
            }
            catch (SecretUnavailableException)
            {
                return Results.BadRequest(new { error = "Re-enter this backup's account credentials first." });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 取消（客户端断开 / 进程关停）不是「验证失败」，不能伪装成用户的密码错误
                // （与 a3ac967「孤儿清理不吞取消」同一约定）：放行上抛。
                return Results.BadRequest(new { error = $"Verification failed: {ex.Message}" });
            }

            // 验证要连云，与前面的存在性检查之间窗口不短：配置行可能已被删除。
            // FirstAsync 会抛成 500，而全仓约定是 404。
            var row = await db.BackupConfigs.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (row is null)
                return Results.NotFound();
            row.PasswordProtected = encryption.Encrypt(req.Password);
            await db.SaveChangesAsync(ct);

            await recovery.TryCompleteAsync(ct);
            return Results.NoContent();
        });

        // 迁移本地根路径（设计 docs/change-local-root-design.md）。
        // preview 与 apply 分开：preview 是纯查询、幂等、可反复重试（换个路径再试一次不留痕迹），
        // apply 的确认语义在日志里独立可辨。同形先例是 restore-estimate 与 restore。
        group.MapPost("/{id:int}/local-root/preview", async (
            int id, LocalRootPreviewRequest req, IBackupConfigService svc, IAccountService accounts,
            ILocalIndexCache indexCache, TrackedInfoStore trackedInfo, ISecretReader secrets,
            IKeyringHealth keyring, PathBoundary boundary, BackupBusyTracker busy, CancellationToken ct) =>
        {
            var prepared = await PrepareLocalRootAsync(
                id, req.NewRoot, svc, accounts, indexCache, trackedInfo, secrets, keyring, boundary, busy, ct);
            return prepared.Failure ?? Results.Ok(prepared.Preview);
        });

        group.MapPost("/{id:int}/local-root", async (
            int id, LocalRootChangeRequest req, IBackupConfigService svc, IAccountService accounts,
            ILocalIndexCache indexCache, TrackedInfoStore trackedInfo, ISecretReader secrets,
            IKeyringHealth keyring, PathBoundary boundary, BackupBusyTracker busy, IOperationLog log,
            IGlobalSettingsService settingsSvc, CancellationToken ct) =>
        {
            // 不信任前端传来的 preview 结果，自己重跑一遍完整校验——这正是 Inspect
            // 必须是纯查询、可安全重入的原因。preview 之后新根被拔掉、或备份在两次调用之间
            // 开跑，都由这一遍兜住。
            var prepared = await PrepareLocalRootAsync(
                id, req.NewRoot, svc, accounts, indexCache, trackedInfo, secrets, keyring, boundary, busy, ct);
            if (prepared.Failure is { } failure)
                return failure;

            var preview = prepared.Preview!;
            var needsForce = preview.Verdict is nameof(LocalRootVerdict.NeedsConfirm)
                or nameof(LocalRootVerdict.Rejected)
                or nameof(LocalRootVerdict.BaselineUnreadable);
            if (needsForce && !req.Force)
                return Results.Json(
                    new
                    {
                        error = "The new root does not match this backup's latest version index.",
                        code = "local_root_mismatch",
                        preview,
                    },
                    statusCode: StatusCodes.Status400BadRequest);

            var oldRoot = prepared.Config!.LocalRoot;
            var moved = await svc.ChangeLocalRootAsync(id, prepared.ResolvedRoot!, ct);
            if (moved is null)
                return Results.NotFound();

            // 来源键必须是全仓统一的 "{op}:{accountId}/{container}"（OperationLogService.cs:91-96）。
            // 写成裸 "backup" 会同时破两处：DeleteForContainerAsync 按 ":{accountId}/{container}"
            // 后缀清理，这条 Warning 级（长存）审计就再也删不掉；QueryAsync 按来源精确相等过滤，
            // 于是按备份看日志时，换根这件最该留痕的事反而看不见。
            //
            // NoBaseline / BaselineUnreadable 这两档一条都没抽样，样本计数得整句省掉
            // ——"0/0 sampled entries matched" 读起来像"全都对不上"，与实情正相反。
            // 换成 reason：BaselineUnreadable 的 reason 里是底层异常原文，而设计 §5 把它算作
            // NAS 上那位拿不到命令行的用户唯一的诊断。只写进 HTTP 响应等于随手一关就没了，
            // 得落在这条长存的审计行里。
            var compared = preview.Sampled > 0
                ? $", {preview.Matched}/{preview.Sampled} sampled entries matched"
                : string.IsNullOrEmpty(preview.Reason) ? "" : $", {preview.Reason}";
            await log.AppendAsync(
                OperationLogLevel.Warning, $"backup:{moved.AccountId}/{moved.ContainerName}",
                $"Local root of '{moved.Name}' changed from '{(string.IsNullOrEmpty(oldRoot) ? "(none)" : oldRoot)}' " +
                $"to '{moved.LocalRoot}' (verdict {preview.Verdict}{compared}" +
                $"{(needsForce ? ", forced" : "")}).",
                ct);

            var settings = await settingsSvc.GetAsync(ct);
            return Results.Ok(BackupConfigResponse.From(moved, settings));
        });

        return app;
    }

    /// <summary>
    /// 一个 container 只能挂一条备份配置。两条配置指到同一个 (账户, container) 上，就是两套互不
    /// 知情的版本号与索引写在同一个地方：后跑的那条读到的云端信息文件要么还没写出来、要么是别人的，
    /// 于是从 version 1 重新开始，把对方的 index.json 覆盖掉，对方的数据 blob 变成孤儿，
    /// 下一轮保留清理就把它们删了。<see cref="AppDbContext"/> 里的唯一索引兜住绕过端点的写入；
    /// 这里负责在写库之前就说清楚是谁占着它。
    /// </summary>
    private static string ContainerTaken(string container, string holder) =>
        $"Container '{container}' already holds the backup \"{holder}\". A container can only hold one "
        + "backup — pointing a second one at it would make both write their own version history to the "
        + "same place, and each would delete the other's data as it cleans up old versions. Pick another "
        + "container, or delete that backup first.";

    /// <summary>
    /// 该备份配置是否仍待重设密码。Healthy 时短路，列表端点不触发任何解密（设计 §3.1 的核心性质）；
    /// Lost 时逐条试解，使已重设成功的备份立刻停止显示「待重设」（设计 §3.3）。
    /// </summary>
    private static bool Pending(IKeyringHealth keyring, IEncryptionService encryption, BackupConfig config) =>
        keyring.Status == KeyringStatus.Lost && SecretAvailability.Unreadable(encryption, config);

    /// <summary>
    /// 瞬时态派生（不落库，§4.2 决策 2）：优先看各 runner 对该 config id 是否有正在跑的运行态
    /// （各 Runner 已各自暴露 <c>Get(id)</c>，无需新增 accessor）；
    /// 都没有但 BackupBusyTracker 显示忙碌，则取其记录的操作标签（BackingUp/Checking/CleaningUp）——
    /// 计划任务的备份/检查/清理都同步持锁运行，不经过任何 Runner。
    /// </summary>
    private static string DeriveActivity(
        BackupConfig c, BackupRunner backupRunner, RestoreRunner restoreRunner, RepairRunner repairRunner,
        CheckRunner checkRunner, BackupBusyTracker busy)
    {
        if (backupRunner.Get(c.Id)?.Status == RunStatus.Running)
            return "BackingUp";
        if (restoreRunner.Get(c.Id)?.Status == RunStatus.Running)
            return "Restoring";
        if (repairRunner.Get(c.Id)?.Status == RunStatus.Running)
            return "Repairing";
        if (checkRunner.Get(c.Id)?.Status == RunStatus.Running)
            return "Checking";
        // 非 Runner 的持锁操作（计划任务的备份/检查/清理）：读忙碌跟踪记录的实际操作标签，
        // 避免把计划备份/清理一律误标为 Checking。
        return busy.CurrentActivity(c.AccountId, c.ContainerName) ?? "Idle";
    }

    /// <summary>/cancel 的 what 过滤：省略即全选。</summary>
    private static bool Wanted(string? what, string kind) =>
        string.IsNullOrWhiteSpace(what) || string.Equals(what, kind, StringComparison.OrdinalIgnoreCase);

    /// <summary>停止请求的三种结局。</summary>
    private enum StopOutcome { NothingRunning, Settled, StillStopping }

    /// <summary>
    /// 发出停止请求并等它落盘完成，但**最多等 20 秒**。
    /// <para>
    /// 为什么要等：用户点完停止，要的是"现在现场已经安全了"，而不是"信号发出去了"。
    /// 为什么要封顶：Suspend 与 Finish current files 都会让正在传的文件（含它所有分卷）传完，
    /// 一个大文件可能要好几分钟；而用户跑在 NAS 上，前面多半有一层反向代理，六十秒就把连接掐了，
    /// 界面上看到的会是一条网络错误，尽管后台一切正常。
    /// </para>
    /// <para>超时不代表没停下：停止请求在 await 之前就发出去了，闸门也已经降级，运行一定会走到终态。</para>
    /// </summary>
    private static async Task<StopOutcome> StopAndWaitAsync(
        Func<CancellationToken, Task<bool>> stop, CancellationToken ct)
    {
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cap.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            return await stop(cap.Token) ? StopOutcome.Settled : StopOutcome.NothingRunning;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return StopOutcome.StillStopping;
        }
    }

    /// <summary>把 DeriveActivity 的驼峰标签写成一句话里读得通的样子（BackingUp → backing up）。</summary>
    private static string Humanize(string activity) =>
        string.Concat(activity.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + char.ToLowerInvariant(ch) : $"{char.ToLowerInvariant(ch)}"));

    /// <summary>删配置的善后步骤：吞异常并记 Warning，一步失败不阻断其余、也不把已成功的主删除变成 500。
    /// 取消除外——那不是「这一步失败了」，而是整条请求该停下（与 a3ac967 的孤儿清理同一约定）；
    /// 吞掉只会给每个剩余步骤各记一条误导性 Warning。</summary>
    private static async Task BestEffort(ILogger logger, string what, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Backup config delete: failed to {What}", what);
        }
    }

    private readonly record struct PreparedLocalRoot(
        IResult? Failure, BackupConfig? Config, string? ResolvedRoot, LocalRootPreviewResponse? Preview);

    /// <summary>
    /// preview 与 apply 共用的前置：取配置 → 忙检查 → 路径校验 → 取基线索引 → Inspect。
    /// 顺序短路，任一步失败就带着对应的 IResult 回去。
    /// </summary>
    private static async Task<PreparedLocalRoot> PrepareLocalRootAsync(
        int id, string newRoot, IBackupConfigService svc, IAccountService accounts,
        ILocalIndexCache indexCache, TrackedInfoStore trackedInfo, ISecretReader secrets,
        IKeyringHealth keyring, PathBoundary boundary, BackupBusyTracker busy, CancellationToken ct)
    {
        if (KeyringGuard.Blocked(keyring) is { } blocked)
            return new PreparedLocalRoot(blocked, null, null, null);

        var config = await svc.GetAsync(id, ct);
        if (config is null)
            return new PreparedLocalRoot(Results.NotFound(), null, null, null);

        // 忙检查在最前面：正在备份/还原/检查时换根，是在给一个正在读的目录抽地毯。
        if (busy.IsBusy(config.AccountId, config.ContainerName))
            return new PreparedLocalRoot(
                Results.Json(
                    new { error = "This backup is busy; try again once the current operation finishes.", code = "backup_busy" },
                    statusCode: StatusCodes.Status409Conflict),
                null, null, null);

        if (string.IsNullOrWhiteSpace(newRoot))
            return new PreparedLocalRoot(
                Results.BadRequest(new { error = "A new local root is required." }), null, null, null);
        if (!Path.IsPathRooted(newRoot))
            return new PreparedLocalRoot(
                Results.BadRequest(new { error = "The new local root must be an absolute path." }), null, null, null);

        // 越界走全仓统一的 409 + path_outside_root，不为本功能另立一套。
        if (PathBoundaryGuard.Blocked(boundary, newRoot) is { } outside)
            return new PreparedLocalRoot(outside, null, null, null);

        if (!Directory.Exists(newRoot))
            return new PreparedLocalRoot(
                Results.BadRequest(new { error = $"'{newRoot}' does not exist or is not a directory." }),
                null, null, null);
        try
        {
            _ = Directory.EnumerateFileSystemEntries(newRoot).Any();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new PreparedLocalRoot(
                Results.BadRequest(new { error = $"'{newRoot}' cannot be listed: {ex.Message}" }), null, null, null);
        }

        var baseline = await LoadBaselineAsync(config, accounts, indexCache, trackedInfo, secrets, ct);
        if (baseline.Error is { } error)
        {
            // 这个备份确实有历史，只是这一份索引读不出来——不能落到 Inspect(null) 那条 NoBaseline
            // 分支，那条分支是给「压根没有历史」用的，会被直接放行。用户在 NAS 上没有命令行，
            // 这条 Reason 里的异常消息是他们能看到的唯一诊断。
            var unreadable = new LocalRootPreviewResponse(
                nameof(LocalRootVerdict.BaselineUnreadable), Sampled: 0, Matched: 0, Missing: 0,
                SizeMismatch: 0, MtimeDiffers: 0, MatchRate: 0,
                Reason: $"The latest version index could not be read: {error}", Examples: []);
            return new PreparedLocalRoot(null, config, newRoot, unreadable);
        }

        var preview = LocalRootMigration.Inspect(newRoot, baseline.Index);
        return new PreparedLocalRoot(null, config, newRoot, preview);
    }

    /// <summary>
    /// 取最新版本索引作为比对基线的三种结果：<c>Index</c> 非空 = 取到了；两者皆空 = 确实没有基线
    /// （没账户/没信息文件/没版本，走 Inspect 判成 NoBaseline）；<c>Error</c> 非空 = 有历史但读取本身
    /// 失败——这三者必须分开，第三种绝不能被当成第二种直接放行（见下方 LoadBaselineAsync 的注释）。
    /// </summary>
    private readonly record struct BaselineLoad(VersionIndex? Index, string? Error);

    /// <summary>
    /// 取最新版本的索引作为比对基线。走本地权威缓存（与 /tree、/file-versions 同一套依赖）。
    /// 没有账户/没有信息文件/没有任何版本 —— 这是「真的没有基线」，交给 Inspect 判成 NoBaseline。
    /// 但信息文件损坏、密码解不开、索引 blob 读取失败 —— 这是「有基线但读不出来」，**不能**
    /// 也归进 NoBaseline：那条分支会被直接放行，而这恰恰是最该让用户多看一眼、需要 force 的情形
    /// （详见 Finding 1）。
    /// </summary>
    private static async Task<BaselineLoad> LoadBaselineAsync(
        BackupConfig config, IAccountService accounts, ILocalIndexCache indexCache,
        TrackedInfoStore trackedInfo, ISecretReader secrets, CancellationToken ct)
    {
        try
        {
            var account = await accounts.GetAsync(config.AccountId, ct);
            if (account is null)
                return default;

            var password = secrets.RevealBackupPassword(config);
            var info = await trackedInfo.LoadAsync(account, config.ContainerName, password, ct);
            var latest = info?.Versions.OrderByDescending(v => v.Version).FirstOrDefault();
            if (info is null || latest is null)
                return default;

            var index = await indexCache.ReadAsync(
                account, config.ContainerName, latest.Version,
                info.Backup.CreatedAt.UtcTicks, latest.IndexBlob, password, ct);
            return new BaselineLoad(index, null);
        }
        // 取消不是「失败」，是整条请求该停下——照旧不拦。
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BaselineLoad(null, ex.Message);
        }
    }
}
