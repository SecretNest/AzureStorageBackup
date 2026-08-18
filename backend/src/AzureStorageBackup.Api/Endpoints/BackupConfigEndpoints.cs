using Azure;
using Azure.Storage.Blobs.Models;
using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>Backup config management endpoints (persistence for the output of the PRD §11 wizard). Responses carry no password; on update, an empty password keeps the existing value.</summary>
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

        // Manual error clearing (decision 2): same semantics as "auto-clear on the next success", so the user can dismiss it themselves.
        group.MapPost("/{id:int}/reset-status", async (int id, IBackupConfigService svc, CancellationToken ct) =>
        {
            if (await svc.GetAsync(id, ct) is null)
                return Results.NotFound();
            await svc.ResetStatusAsync(id, ct);
            return Results.NoContent();
        });

        // Import an existing backup: read the container's info file to rebuild the config, seed the local authoritative state, and pull every version index into the local cache (roadmap, PRD 1.5, §3.3)
        group.MapPost("/import", async (ImportRequest req, IAccountService accounts, TrackedInfoStore trackedInfo, IBackupConfigService svc, ILocalIndexCache indexCache, IEncryptionService encryption, IKeyringHealth keyring, IOperationLog log, IGlobalSettingsService settingsSvc, CheckRunner checkRunner, CancellationToken ct) =>
        {
            var account = await accounts.GetAsync(req.AccountId, ct);
            if (account is null)
                return Results.BadRequest(new { error = "Account not found." });
            // Ahead of any cloud read: a question the local database can answer should not cost a network round trip first, especially since that round trip would also
            // seed the cloud info file into TrackedInfoStore — mutating the local authoritative state for an import that is bound to be rejected just leaves dirty data behind.
            if (await svc.FindAsync(req.AccountId, req.ContainerName, ct) is { } holder)
                return Results.Conflict(new { error = ContainerTaken(req.ContainerName, holder.Name) });

            (BackupInfoFile Info, string ETag)? seeded;
            try
            {
                seeded = await trackedInfo.SeedFromCloudAsync(account, req.ContainerName, req.Password, ct);
            }
            catch (SecretUnavailableException)
            {
                // A lost keyring means the account key cannot be read, which has nothing to do with the backup password — do not pin the blame on what the user typed
                // (handled the same way as reset-password).
                return Results.BadRequest(new { error = "Re-enter this account's credentials first." });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Cancellation (client disconnect / process shutdown) is not "wrong password": swallowing it would disguise it as a user error.
                // Consistent with the existing convention in this repo (see a3ac967 "orphan cleanup does not swallow cancellation"), always let it propagate.
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
                // The password in the request body is plaintext; encrypt it the moment it lands on the entity (design §3.1).
                PasswordProtected = info.Backup.Encrypted && !string.IsNullOrEmpty(req.Password)
                    ? encryption.Encrypt(req.Password)
                    : null,
            };
            var created = await svc.CreateAsync(config, ct);

            // B2: with no SourceRootHint, LocalRoot ends up an empty string, and once Backup__Root is set, every later operation
            // on this config hits exactly the same 409 path_outside_root as a genuinely out-of-bounds local root — IsInside gives the
            // identical refusal for an empty string and for a real out-of-bounds path, so from the response the operator cannot tell
            // whether they configured the root wrong or this import simply never captured a path hint. Spell the reason out right here
            // at import time, in the operation log, instead of leaving it to bewilder someone the next time they slip and click run.
            if (string.IsNullOrEmpty(info.Backup.SourceRootHint))
            {
                await log.AppendAsync(
                    OperationLogLevel.Warning, $"import:{req.AccountId}/{req.ContainerName}",
                    $"Imported '{config.Name}' without a local root hint (LocalRoot is empty); " +
                    "set Local Root on this backup before running it.",
                    ct);
            }

            // Download every version index into the local cache (version files are metadata and never in Archive): from now on
            // backup/cleanup/restore all read this local copy and never ask the cloud again — after an import there is no such thing as "no local authority".
            //
            // A version whose index cannot be read **does not abort the whole import**: only that one version is broken, the rest of them
            // and this config are fine, and the user needs the config to exist before they have anywhere to investigate or repair from. Write it
            // into the operation log; the automatic check below will list everything it drags in as well.
            var identity = info.Backup.CreatedAt.UtcTicks;
            var unreadable = new List<int>();
            foreach (var v in info.Versions)
            {
                try
                {
                    await indexCache.ReadAsync(account, req.ContainerName, v.Version, identity, v.IndexBlob, req.Password, v.IndexVolumes, ct);
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

            // The ledger is complete, so now audit it: are all those cloud data blobs and volumes still there, and are the sizes right?
            // HEAD requests only, no downloads. **Do not check locally** — at this moment LocalRoot is most likely still empty (which is exactly
            // what happens when the info file has no SourceRootHint), and comparing against it would just fill the screen with "missing locally".
            // Going through an internal call instead of hitting our own /check endpoint is precisely to bypass the LocalRoot boundary gate on it.
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
            // Paying off the debt of a wizard with no hard validation at all (found in review): do the cheap local string checks first,
            // then the path boundary check that touches neither the file system nor the database, and only then the account-existence check that needs a database query —
            // the most expensive check goes last, and any step that fails means we never get further.
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
            // LocalRoot/ContainerName/Tier/encryptedness are locked after creation and already refused by BackupConfigService.UpdateAsync;
            // Name is not one of the locked fields and stays editable, so blanks still have to be blocked here (the same rule as on the create endpoint).
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Name is required." });

            // Empty password = keep the existing value, non-empty = refuse (decision 8), both decided in the service layer: the ciphertext carries a random IV, so it cannot be compared here.
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
                // Base fields are locked after creation (§4.5): changes to account/container/local root/Tier/encryptedness are refused.
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // deleteContainer=true (default false): delete the whole cloud container along with it (irreversible, §4.3). Delete the cloud side first, then the local config,
        // so a failed cloud deletion does not leave the local record already gone with no way for the user to retry.
        group.MapDelete("/{id:int}", async (int id, bool? deleteContainer, IBackupConfigService svc, IAccountService accounts, IContainerService containers, IOperationLog log, ILocalIndexCache indexCache, ILocalBackupStateStore localState, BackupJournalStore journals, IKeyringHealth keyring, KeyringRecovery recovery, BackupRunner backupRunner, RestoreRunner restoreRunner, RepairRunner repairRunner, CheckRunner checkRunner, BackupBusyTracker busy, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();

            // No deleting while an operation is running. Deleting the config does **not** stop the background run: it keeps going to
            // completion and keeps holding the (account, container) lock in BackupBusyTracker, while _runs is keyed by config id —
            // delete the config and the UI can never find its progress again, so a newly created backup on the same container gets refused
            // (busy) while the status says BackingUp with no detail at all, looking like it wedged out of nowhere. If "delete the container"
            // was ticked as well, that run also keeps uploading into a container that no longer exists. Users hit every one of these for real.
            var activity = DeriveActivity(config, backupRunner, restoreRunner, repairRunner, checkRunner, busy);
            if (activity != "Idle")
                return Results.Conflict(new
                {
                    error = $"This backup is currently {Humanize(activity)}. Wait for it to finish before deleting it.",
                });

            // Capture account/container before deleting the config row: the local cache/state belong to (accountId, container), and once the row is gone they are out of reach.
            var accountId = config.AccountId;
            var container = config.ContainerName;

            if (deleteContainer ?? false)
            {
                // Only the branch that also deletes the cloud container needs the account key → 409 when the keyring is lost.
                // The deleteContainer=false branch is purely local and must stay ungated: under decision 6 it is the only way out of
                // "I cannot remember the backup password", and it has to work in recovery mode too.
                if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

                var account = await accounts.GetAsync(accountId, ct);
                if (account is null)
                    return Results.BadRequest(new { error = "Account not found." });
                await containers.DeleteContainerAsync(account, container, ct);
            }

            var ok = await svc.DeleteAsync(id, ct);
            if (ok)
            {
                // Each cleanup step is best-effort on its own: the config row is already gone (the main operation succeeded), so a single failing step must not return 500 or block the remaining steps.
                // Leftover orphan logs/cache/state are harmless and get overwritten by a later cleanup/rebuild.
                var logger = loggerFactory.CreateLogger("BackupConfigDelete");
                await BestEffort(logger, "delete audit logs",
                    () => log.DeleteForContainerAsync(accountId, container, ct)); // delete the audit logs along with it (PRD 3.6)
                // Purge the local authoritative cache/state along with it (local-authority principle, design §3.3): otherwise rebuilding a backup on the same
                // account+container hits orphan CachedVersionIndex/LocalBackupState rows whose version identity does not match the new backup.
                await BestEffort(logger, "evict local index cache",
                    () => indexCache.RemoveForContainerAsync(accountId, container, ct));
                await BestEffort(logger, "remove local backup state",
                    () => localState.RemoveAsync(accountId, container, ct));

                // With the config gone, nobody will ever adopt this container's journal again, and keeping it around only protects
                // that batch of blocks from cleanup forever (the cleanup criterion looks at journals, not at configId). **Delete the journal
                // files only, never the blobs they reference**: a journal records both real uploads and if-missing hits, and the latter may well
                // also be referenced by an already-committed version index — deleting them would punch a hole through a retained version.
                //
                // Who collects them once they lose that protection: when this container gets a config again, the **first backup run of that
                // config** does an orphan sweep on the way out, using the full criterion (index readable, references recognizable) to sweep the
                // real orphans away. That path only works because of the step immediately above — localState.RemoveAsync clears the local
                // authoritative state, which is how the rebuilt config recognizes it is on its first run (see firstRun in BackupOrchestrator and BackupRunControl.SweepNeeded).
                // The order of those two steps does not matter (both best-effort, neither depends on the other), but without that step all that
                // is left here is the hope that "the user happens to have configured a Cleanup scheduled task", and that is entirely up to them.
                await BestEffort(logger, "discard backup journals",
                    () => { journals.DeleteAll(accountId, container); return Task.CompletedTask; });

                // What we just deleted may have been the one and only undecryptable ciphertext awaiting reset (the backup password): without
                // finishing up, the keyring never flips back to Healthy and the user is stuck in the "Lost but nothing left to reset" dead end until the next restart (design §3.4 fix).
                await recovery.TryCompleteAsync(ct);

                // The audit line is written **after** the cleanup: DeleteForContainerAsync wipes every log for that (account, container),
                // so writing it first would mean deleting itself. Deletion is the one operation here that erases history, and if it left no
                // trace of its own the log page would inexplicably go completely empty — a user reported exactly that.
                //
                // The source key carries accountId: this line used to be in the pre-revamp format "backup:{container}" and was missed in the
                // change. Without the account dimension, the same container name under two accounts writes identical lines and nobody can tell
                // which is which; the log page filters by exact source equality, so it shows up in neither backup's view. Writing it after the
                // cleanup does not affect this — that cleanup finished long before, and it cannot reach this line.
                await BestEffort(logger, "record deletion", () => log.AppendAsync(
                    OperationLogLevel.Warning, $"backup:{accountId}/{container}",
                    (deleteContainer ?? false)
                        ? $"Backup config '{config.Name}' deleted, along with its cloud container."
                        : $"Backup config '{config.Name}' deleted; the cloud container was kept.",
                    ct, durable: true));
            }
            return ok ? Results.NoContent() : Results.NotFound();
        });

        // Start one backup run (runs in the background, progress by polling)
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

        // Query the run's progress/status
        group.MapGet("/{id:int}/run", (int id, BackupRunner runner) =>
        {
            var state = runner.Get(id);
            return state is null ? Results.NotFound() : Results.Ok(BackupRunResponse.From(state));
        });

        // Suspend: stop once everything is safely on disk, leave the scene in place, and the next click on Run picks it up as it was.
        // **There is no matching resume endpoint** — resuming is not a mode: every backup run looks for a still-valid journal when it opens,
        // so "continue" is just clicking /run again, going down the very same execution path.
        group.MapPost("/{id:int}/suspend", async (int id, BackupRunner runner, CancellationToken ct) =>
            await StopAndWaitAsync(c => runner.SuspendAsync(id, ct: c), ct) switch
            {
                StopOutcome.NothingRunning => Results.Conflict(new { error = "No backup is running." }),
                StopOutcome.StillStopping => Results.Accepted($"/api/backup-configs/{id}/run", new { stopping = true }),
                _ => Results.NoContent(),
            });

        // While parked on a transient error waiting to self-heal, "Retry now" lets the user skip the timer and release one retry immediately.
        group.MapPost("/{id:int}/retry-now", (int id, BackupRunner runner) =>
            runner.RetryNow(id)
                ? Results.NoContent()
                : Results.Conflict(new { error = "This backup is not waiting to retry." }));

        // A real pause: every stage finishes the item in hand and parks, and the run stays alive holding its
        // staging quota. Distinct from Suspend, which tears the run down and makes the next start re-diff and
        // re-probe everything. Mirrors retry-now's shape: both reach into a live run's gate, and answer a
        // conflict rather than a silent success when there is nothing to act on.
        // The conflict covers three cases and says all three, because from the operator's chair they are one
        // question — "why did nothing happen?" — and each of them is a different answer:
        //   · there is no run at all;
        //   · there is one winding down (Suspend, Stop, or a patience auto-suspend), which still reports itself as
        //     Running for minutes while the upload in hand finishes;
        //   · there is one that has not finished starting — BackupRunner.Pause needs the run's control, and that is
        //     assigned a few awaits into RunCoreAsync, after the config, the account, the settings and the backup
        //     password have been loaded.
        // Answering 204 for any of them is the silent success this comment promises not to give. The third used to
        // be told it was not running, which is false on both counts the old wording offered.
        group.MapPost("/{id:int}/pause", (int id, BackupRunner runner) =>
            runner.Pause(id)
                ? Results.NoContent()
                : Results.Conflict(new { error = "No backup is running, or it is still starting or already stopping." }));

        // Lift a user pause. If a transient error is also holding the gate, the run stays parked on that one —
        // see PauseInfo.Source / BackupRunResponse.PausedByUser for how the UI tells the two apart.
        group.MapPost("/{id:int}/resume", (int id, BackupRunner runner) =>
            runner.Resume(id)
                ? Results.NoContent()
                : Results.Conflict(new { error = "This backup is not paused." }));

        // Which runs on this container stopped partway. Right after startup the UI uses this to put "there is unfinished work" in front of the user and wait for a click,
        // rather than deciding on their behalf whether to keep going.
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

        // The user does not want to continue: throw the scene away.
        // The blocks in the cloud are not deleted here — deciding "which version still references this" means reading version indexes, which
        // needs the backup password, and this endpoint cannot get it. Once the journal is gone they lose their protection, and the next cleanup
        // with an orphan sweep collects them using the full criterion (Task 11).
        group.MapDelete("/{id:int}/interrupted", async (
            int id, IBackupConfigService svc, BackupJournalStore journals, BackupRunner runner,
            CancellationToken ct) =>
        {
            var config = await svc.GetAsync(id, ct);
            if (config is null)
                return Results.NotFound();
            // The run in progress is holding a journal of its own, and pulling the file out from under it only produces a pile of
            // baffling IO errors on the way out. Make the user stop it first.
            if (runner.Get(id) is { Status: RunStatus.Running })
                return Results.Conflict(new { error = "This backup is running; stop it first." });

            journals.DeleteAll(config.AccountId, config.ContainerName);
            return Results.NoContent();
        });

        // Start a restore (runs in the background; targetRoot defaults to the config's local root, version defaults to the latest)
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

        // Which versions a given path can be restored from (versions that contain the path, have storage, and are not marked unrecoverable, ordered nearest-first), for per-file substitution during restore.
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
            // Local authoritative cache first (same as /tree). It matters especially here: the loop reads **one index per version**,
            // so reading straight from the cloud means downloading N index blobs on every click of "pick a substitute version" — on top of the
            // latency that is real Azure egress traffic charges, while an authoritative copy is sitting right here locally.
            var fvIdentity = info?.Backup.CreatedAt.UtcTicks ?? 0;
            foreach (var v in (info?.Versions ?? []).OrderByDescending(v => v.Version))
            {
                var idx = await indexCache.ReadAsync(account, config.ContainerName, v.Version, fvIdentity, v.IndexBlob, password, v.IndexVolumes, ct);
                if (idx.UnrecoverablePaths.Contains(path))
                    continue;
                var e = idx.Entries.FirstOrDefault(x => x.Path == path && x.Storage is not null);
                if (e is not null)
                    candidates.Add(new { v.Version, v.CreatedAt, length = e.Length });
            }
            return Results.Ok(candidates);
        });

        // The file paths marked unrecoverable in a given version (drives per-file substitution during restore).
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
                account, config.ContainerName, ver.Version, info.Backup.CreatedAt.UtcTicks, ver.IndexBlob, password, ver.IndexVolumes, ct);
            return Results.Ok(idx.UnrecoverablePaths);
        });

        // Files in a version whose content was **carried over**: on those backup runs the source file could not be opened, so the index reused an entry from an earlier version.
        // Symmetric with /unrecoverable but different in meaning: there the data is corrupt and there is no content to give; here the content is valid, just old.
        // The user needs to know this before restoring — otherwise they restore this version and unknowingly get content from an earlier point in time.
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
                account, config.ContainerName, ver.Version, info.Backup.CreatedAt.UtcTicks, ver.IndexBlob, password, ver.IndexVolumes, ct);
            return Results.Ok(idx.Entries
                .Where(e => e.UnreadableAt is not null)
                .Select(e => new { path = e.Path, unreadableAt = e.UnreadableAt })
                .ToList());
        });

        // Lazily loaded directory tree for restore (§4.1a, decision 1): returns the direct children (subdirectories + files) of the path directory so the frontend can expand level by level
        // instead of pulling the whole tree at once. The data source is the version index, local authoritative cache first, falling back to the cloud only when it is missing or the identity does not match (handled inside ILocalIndexCache.ReadAsync).
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
                return Results.Ok(Array.Empty<TreeNode>()); // the requested version does not exist → empty result, same as /unrecoverable and /file-versions

            var identity = info.Backup.CreatedAt.UtcTicks;
            var idx = await indexCache.ReadAsync(account, config.ContainerName, ver.Version, identity, ver.IndexBlob, password, ver.IndexVolumes, ct);
            return Results.Ok(VersionTreeService.Children(idx, path));
        });

        // Restore download/uncompressed volume estimate (§4.1b, requirement A + decision 5): for the selected paths, first compute the download volume purely locally (the total size of the
        // deduplicated storage objects, a shared pack/deduplicated blob counted only once) and the uncompressed volume, then HEAD the first volume of each deduplicated object for its rehydration state (Archive / pending).
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
            var idx = await indexCache.ReadAsync(account, config.ContainerName, ver.Version, identity, ver.IndexBlob, password, ver.IndexVolumes, ct);
            var estimate = RestoreEstimator.Compute(idx, info, body.Paths ?? []);

            // First-volume blob name + volume count for each deduplicated storage object (packs use PackInfo.Volumes; blobs use StorageRef.Volumes from the first entry with that Ref).
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
                    // best effort: a failed HEAD on a single object (e.g. it has already been deleted) must not affect the estimate for the rest, so just skip its rehydration count.
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

        // Repair corrupt/missing cloud blobs from local files (an explicit action, background job): holds the busy lock until it finishes, during which this backup can do nothing else. Whatever cannot be repaired is marked unrecoverable.
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

        // List all versions of a backup (for picking a version when restoring/checking). Goes through the local authoritative info file and normally does not read the cloud.
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
            // Newest first: in the UI the entry right after "Latest" should be the second-newest version, consistent with the nearest-first ordering of /file-versions.
            var versions = (info?.Versions ?? []).OrderByDescending(v => v.Version).Select(v => new
            {
                v.Version,
                v.CreatedAt,
                v.StartedAt,   // versions written before the upgrade do not have one → null, and the UI shows "—"
                files = v.Stats.Files,
                bytes = v.Stats.Bytes,
                changedFiles = v.Stats.ChangedFiles,
            });
            return Results.Ok(versions);
        });

        // Integrity check (Content level: download, extract and recompute hashes for a deep verification). **A background job**:
        // a content-level check downloads the entire backup, which for a few hundred GB means hours — back in the synchronous-endpoint days
        // the request got cut off by a browser or reverse-proxy timeout first, the check ran for nothing, and there was no progress to watch the whole time. Now it returns 202 and you poll with GET.
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
                // Cast explicitly to AccessTier?: AccessTier has an implicit conversion constructor from string, and when a ternary is mixed with a bare null
                // the compiler takes the "null → string → AccessTier(string)" implicit conversion path rather than "AccessTier → AccessTier?",
                // so when rehydrate is empty it passes null to AccessTier(string) and throws ArgumentNullException (a real production bug).
                RehydrateTier = rehydrate is { } t ? (AccessTier?)BackupRequestMapper.MapTier(t) : null,
                ListOrphans = listOrphans ?? false,
            };
            var state = runner.Start(id, version, options);
            return Results.Accepted($"/api/backup-configs/{id}/check", CheckRunResponse.From(state));
        });

        // The status and report of the most recent check. **The report stays around** after the run finishes: closing the dialog and reopening it has to bring the result back.
        group.MapGet("/{id:int}/check", (int id, CheckRunner runner) =>
        {
            var state = runner.Get(id);
            // "Never checked" is not an error: the check dialog asks once as soon as it opens, and a 404 leaves a red error in the
            // browser console that looks like a malfunction (which is exactly how a user reported it). 204 = there is no check to report.
            return state is null ? Results.NoContent() : Results.Ok(CheckRunResponse.From(state));
        });

        // Stop whatever operations are running on this backup. Omitting what = stop everything; otherwise stop only the specified kind
        // (one config may be backing up and restoring at the same time — restore deliberately does not take the busy lock, see the comment at the top of RestoreRunner).
        //
        // Before this there was no "stop" action at all: a backup started by mistake could only be waited out or dealt with by restarting the whole container —
        // and the user runs on a NAS, where a restart takes other services down with it, while "no deleting a config while it is busy" had closed off deletion as an escape route too.
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

            // The backup branch **waits for the flush to disk before returning**; the other three still just raise a signal and leave — they have no scene that needs flushing.
            // finishCurrentFiles=true: finish uploading the file currently in flight (including all of its volumes) and then stop, and that part counts;
            // false: stop immediately and delete the half-written volumes and in-flight blocks, leaving no unusable residue.
            if (Wanted(what, "backup"))
                switch (await StopAndWaitAsync(c => backupRunner.CancelAsync(id, finishCurrentFiles ?? false, c), ct))
                {
                    case StopOutcome.Settled: canceled.Add("backup"); break;
                    case StopOutcome.StillStopping: canceled.Add("backup"); stopping = true; break;
                }

            if (Wanted(what, "restore") && restoreRunner.Cancel(id)) canceled.Add("restore");
            if (Wanted(what, "repair") && repairRunner.Cancel(id)) canceled.Add("repair");
            if (Wanted(what, "check") && checkRunner.Cancel(id)) canceled.Add("check");

            // Apart from backup, stopping is still asynchronous: this only raises the cancellation signal, and the run itself does not actually wind down until the next cancellation checkpoint.
            // The UI uses that to switch the button to "Stopping…" instead of treating it as already stopped.
            return canceled.Count == 0
                ? Results.Conflict(new { error = "Nothing is running for this backup." })
                : Results.Ok(new { canceled, stopping });
        });

        // Backup password reset (design §3.4). The basis for verification: for an encrypted backup, the info file is itself a 7z encrypted with that password;
        // it is the metadata root node and the smallest encrypted object in the container, so being able to open it proves the password is correct.
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

            // Ordering dependency: reaching the cloud needs the account key, so the account has to be recovered first.
            try
            {
                // Read-only; TrackedInfoStore.SeedFromCloudAsync must not be used here — that would seed the local authoritative state.
                var info = await store.ReadInfoWithETagAsync(account, config.ContainerName, req.Password, ct);
                if (info is null)
                    return Results.BadRequest(new { error = "No backup info file found in the container." });
                // ReadInfoWithETagAsync probes the unencrypted blob name first — if the container happens to hold an unencrypted info
                // file, it reads it back with password: null and the submitted password was never used for decryption at all. We must verify
                // the returned content really came from an encrypted object, otherwise an arbitrary string gets stored as the password and the real one is lost forever.
                //
                // What is checked here is the self-declared flag inside the returned JSON rather than "decryption succeeded" itself, which looks
                // like a hole but is not: on the write side there is ultimately only one path, BackupInfoStore.WriteInfoConditionalAsync, which picks
                // the blob name (IndexBlobName / EncryptedIndexBlobName) by whether password is empty, while Backup.Encrypted is carried by the
                // content of that same write. So for an info file written by this application, the flag and the blob name it lives under (i.e. whether
                // it is encrypted) cannot disagree: Encrypted=true means this JSON can only have come from the .enc branch, which in turn means the
                // read above really was opened with the submitted password.
                //
                // "Only one path" is verifiable — do not be fooled by the number of call sites: the interface also has IBackupInfoStore.WriteInfoAsync,
                // and BackupOrchestrator / BackupRepairer / RetentionCleaner all call it directly — but its implementation body is literally the one line
                // `=> WriteInfoConditionalAsync(..., ifMatch: null, ct)` (BackupInfoStore.WriteInfoAsync), and it does not assemble a blob name itself.
                // So the invariant holds if and only if **WriteInfoAsync keeps delegating like this and no third write implementation that decides its
                // own blob name is added**. Break either condition and this check must be changed to rely on the decryption result instead.
                if (!info.Value.Info.Backup.Encrypted)
                    return Results.BadRequest(new { error = "This container's backup is not encrypted; the password cannot be verified." });
            }
            catch (SecretUnavailableException)
            {
                return Results.BadRequest(new { error = "Re-enter this backup's account credentials first." });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Cancellation (client disconnect / process shutdown) is not "verification failed" and must not be disguised as a wrong password from the user
                // (the same convention as a3ac967 "orphan cleanup does not swallow cancellation"): let it propagate.
                return Results.BadRequest(new { error = $"Verification failed: {ex.Message}" });
            }

            // Verification goes to the cloud, so the window between it and the existence check above is not short: the config row may already have been deleted.
            // FirstAsync would blow up as a 500, while the repo-wide convention is 404.
            var row = await db.BackupConfigs.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (row is null)
                return Results.NotFound();
            row.PasswordProtected = encryption.Encrypt(req.Password);
            await db.SaveChangesAsync(ct);

            await recovery.TryCompleteAsync(ct);
            return Results.NoContent();
        });

        // Migrate the local root path (design docs/configuration.md).
        // preview and apply are separate: preview is a pure query, idempotent and freely retryable (trying another path leaves no trace),
        // while apply's confirmation semantics are independently identifiable in the log. The same shape already exists in restore-estimate and restore.
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
            // Do not trust the preview result the frontend sends; rerun the full validation ourselves — which is exactly why Inspect
            // has to be a pure query that is safe to re-enter. The new root being unplugged after the preview, or the backup starting
            // between the two calls, are both caught by this second pass.
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

            // The source key must be the repo-wide "{op}:{accountId}/{container}" (OperationLogService.cs:91-96).
            // Writing a bare "backup" breaks two things at once: DeleteForContainerAsync cleans up by the ":{accountId}/{container}"
            // suffix, so this Warning-level (long-lived) audit line could never be deleted again; and QueryAsync filters by exact source
            // equality, so when reading the log per backup, a root change — the thing that most deserves a trace — is nowhere to be seen.
            //
            // The NoBaseline / BaselineUnreadable verdicts sample nothing at all, so the sample count has to be dropped from the sentence
            // entirely — "0/0 sampled entries matched" reads like "nothing matched at all", the exact opposite of the truth.
            // Use reason instead: BaselineUnreadable's reason carries the underlying exception verbatim, and design §5 counts that as the
            // only diagnostic available to the user on the NAS who cannot get to a command line. Writing it only into the HTTP response
            // means it is gone the moment they close the dialog; it has to land in this long-lived audit line.
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
    /// A container can hold only one backup config. Two configs pointing at the same (account, container) means two mutually
    /// unaware sets of version numbers and indexes written to the same place: whichever runs second reads a cloud info file that either
    /// has not been written yet or belongs to the other one, so it starts over from version 1, overwrites the other's index.json, turns
    /// the other's data blobs into orphans, and the next retention cleanup deletes them. The unique index in <see cref="AppDbContext"/>
    /// catches writes that bypass the endpoint; this message's job is to say who is holding it, before anything is written to the database.
    /// </summary>
    private static string ContainerTaken(string container, string holder) =>
        $"Container '{container}' already holds the backup \"{holder}\". A container can only hold one "
        + "backup — pointing a second one at it would make both write their own version history to the "
        + "same place, and each would delete the other's data as it cleans up old versions. Pick another "
        + "container, or delete that backup first.";

    /// <summary>
    /// Whether this backup config is still awaiting a password reset. When Healthy it short-circuits, so the list endpoint triggers no
    /// decryption at all (the core property of design §3.1); when Lost it tries to decrypt each one, so a backup that has already been reset successfully immediately stops showing "needs reset" (design §3.3).
    /// </summary>
    private static bool Pending(IKeyringHealth keyring, IEncryptionService encryption, BackupConfig config) =>
        keyring.Status == KeyringStatus.Lost && SecretAvailability.Unreadable(encryption, config);

    /// <summary>
    /// Derives the transient state (not persisted, §4.2 decision 2): first look at whether any runner has a running state for this config id
    /// (each Runner already exposes <c>Get(id)</c>, so no new accessor is needed);
    /// if none does but BackupBusyTracker says busy, take the operation label it recorded (BackingUp/Checking/CleaningUp) —
    /// scheduled backup/check/cleanup all run synchronously while holding the lock and never go through a Runner.
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
        // Lock-holding operations that are not Runners (scheduled backup/check/cleanup): read the actual operation label recorded by the
        // busy tracker, so scheduled backups/cleanups are not all mislabeled as Checking.
        return busy.CurrentActivity(c.AccountId, c.ContainerName) ?? "Idle";
    }

    /// <summary>The what filter for /cancel: omitting it selects everything.</summary>
    private static bool Wanted(string? what, string kind) =>
        string.IsNullOrWhiteSpace(what) || string.Equals(what, kind, StringComparison.OrdinalIgnoreCase);

    /// <summary>The three outcomes of a stop request.</summary>
    private enum StopOutcome { NothingRunning, Settled, StillStopping }

    /// <summary>
    /// The cap on how long we wait for the flush to disk. This number is not part of the HTTP contract — it is purely a seam opened up for
    /// the test project: with <c>InternalsVisibleTo</c> (see AssemblyInfo.cs) the 20 seconds can be turned down to milliseconds, which is the
    /// only affordable way to test the "genuinely did not settle, answer 202/200 stopping:true" branch; otherwise one honest timeout test would really wait 20 seconds.
    /// In production it is always 20 seconds; tests must remember to set it back in a finally block, since it is a static field shared across the process.
    /// </summary>
    internal static TimeSpan StopWaitCap = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Raise the stop request and wait for it to finish flushing to disk, but **for at most <see cref="StopWaitCap"/> (20 seconds in production)**.
    /// <para>
    /// Why wait: after clicking stop the user wants "the scene is safe now", not "the signal has been sent".
    /// Why cap it: both Suspend and Finish current files let the file currently in flight (including all of its volumes) finish uploading,
    /// and one large file can take several minutes; meanwhile the user runs on a NAS, most likely behind a reverse proxy that cuts the connection
    /// at sixty seconds, and what they see in the UI is a network error even though everything in the background is fine.
    /// </para>
    /// <para>A timeout does not mean it did not stop: the stop request went out before the await and the gate has already been lowered, so the run is certain to reach a terminal state.</para>
    /// </summary>
    private static async Task<StopOutcome> StopAndWaitAsync(
        Func<CancellationToken, Task<bool>> stop, CancellationToken ct)
    {
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cap.CancelAfter(StopWaitCap);
        try
        {
            return await stop(cap.Token) ? StopOutcome.Settled : StopOutcome.NothingRunning;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return StopOutcome.StillStopping;
        }
    }

    /// <summary>Turns DeriveActivity's camel-case label into something that reads correctly inside a sentence (BackingUp → backing up).</summary>
    private static string Humanize(string activity) =>
        string.Concat(activity.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + char.ToLowerInvariant(ch) : $"{char.ToLowerInvariant(ch)}"));

    /// <summary>A cleanup step for config deletion: swallow the exception and log a Warning, so one failing step neither blocks the rest nor turns an already successful main deletion into a 500.
    /// Cancellation is the exception — that is not "this step failed" but "the whole request should stop" (the same convention as the orphan cleanup in a3ac967);
    /// swallowing it would only log one misleading Warning per remaining step.</summary>
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
    /// The preamble shared by preview and apply: fetch the config → busy check → path validation → load the baseline index → Inspect.
    /// Short-circuits in order; any step that fails returns straight back with the corresponding IResult.
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

        // The busy check comes first: changing the root while a backup/restore/check is running is pulling the rug out from under a directory that is being read.
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

        // Out-of-bounds goes through the repo-wide 409 + path_outside_root; no separate scheme just for this feature.
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
            // This backup does have history, it is just that this one index cannot be read — it must not fall into the NoBaseline branch of
            // Inspect(null), which is meant for "there is no history at all" and gets let straight through. The user on the NAS has no command line,
            // and the exception message in this Reason is the only diagnostic they can see.
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
    /// The three outcomes of loading the latest version index as a comparison baseline: <c>Index</c> non-null = we got it; both null = there
    /// genuinely is no baseline (no account / no info file / no versions, which Inspect judges as NoBaseline); <c>Error</c> non-null = there is
    /// history but the read itself failed — these three must stay apart, and the third must never be treated as the second and let straight through (see the comment on LoadBaselineAsync below).
    /// </summary>
    private readonly record struct BaselineLoad(VersionIndex? Index, string? Error);

    /// <summary>
    /// Load the latest version's index as a comparison baseline. Goes through the local authoritative cache (the same set of dependencies as /tree and /file-versions).
    /// No account / no info file / no versions at all — that is "there really is no baseline", handed to Inspect to judge as NoBaseline.
    /// But a corrupt info file, a password that will not decrypt, or a failed index blob read — that is "there is a baseline but it cannot be read", and it **must not**
    /// be lumped into NoBaseline either: that branch gets let straight through, whereas this is precisely the case that most deserves a second look from the user and requires force
    /// (see Finding 1 for details).
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
                info.Backup.CreatedAt.UtcTicks, latest.IndexBlob, password, latest.IndexVolumes, ct);
            return new BaselineLoad(index, null);
        }
        // Cancellation is not a "failure", it means the whole request should stop — as always, do not intercept it.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BaselineLoad(null, ex.Message);
        }
    }
}
