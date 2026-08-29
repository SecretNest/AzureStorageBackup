using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>Account management endpoints. Responses carry no sensitive fields; on update, blank sensitive fields keep their existing values.</summary>
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

            Account created;
            try { created = await svc.CreateAsync(req.ToAccount(encryption), ct); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); } // duplicate endpoint
            // A freshly created account cannot be in use yet; passing an explicit empty list says "genuinely none" rather than "could not get it, so left blank".
            return Results.CreatedAtRoute("GetAccount", new { id = created.Id },
                AccountResponse.From(created, Pending(keyring, encryption, created), []));
        });

        group.MapPut("/{id:int}", async (int id, AccountRequest req, IAccountService svc, IEncryptionService encryption, IKeyringHealth keyring, CancellationToken ct) =>
        {
            var existing = await svc.GetAsync(id, ct);
            if (existing is null)
                return Results.NotFound();

            var update = req.ToAccount(encryption);
            // A blank sensitive field means "keep the existing value": copy the old ciphertext across, no decryption needed.
            if (string.IsNullOrEmpty(req.AccountKey))
                update.AccountKeyProtected = existing.AccountKeyProtected;
            if (string.IsNullOrEmpty(req.ProxyPassword))
                update.ProxyPasswordProtected = existing.ProxyPasswordProtected;

            Account? result;
            try { result = await svc.UpdateAsync(id, update, ct); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); } // duplicate endpoint
            if (result is null)
                return Results.NotFound();
            var usage = await svc.GetBackupUsageAsync(ct);
            // The keep-the-old-ciphertext branch carries exactly the ciphertext a lost keyring cannot decrypt, so SecretsUnavailable must report it honestly;
            // the branch that submitted new credentials writes ciphertext from the current keyring, and the per-record trial decryption correctly returns false.
            return Results.Ok(AccountResponse.From(
                result, Pending(keyring, encryption, result), usage.GetValueOrDefault(id)));
        });

        group.MapDelete("/{id:int}", async (int id, IAccountService svc, KeyringRecovery recovery, CancellationToken ct) =>
        {
            // Cannot delete while a backup still uses it. BackupConfig.AccountId has no foreign key constraint, so the
            // database layer cannot stop this — deleting leaves behind a pile of orphan configs whose AccountId points at
            // nothing, and they only blow up on the next real run (the "Account {id} not found" in
            // BackupRunner/CheckRunner/RestoreRunner). For a scheduled task that means failing at 3am and noticing the
            // next day; restore is worse — you find out only when you actually need the data back.
            // The UI already disables the delete button; this is the server side of the same check — without it, that disabling is only decoration.
            var usage = await svc.GetBackupUsageAsync(ct);
            if (usage.GetValueOrDefault(id) is { Count: > 0 } inUse)
                return Results.Conflict(new
                {
                    error = $"Account is used by {inUse.Count} backup(s): {string.Join(", ", inUse)}",
                    usedByBackups = inUse,
                });

            var ok = await svc.DeleteAsync(id, ct);
            // What was just deleted may have been the only undecryptable ciphertext still pending a reset: without finishing
            // recovery the status never flips back to Healthy, and until the next restart the user is stuck in the dead end of "Lost, but nothing to re-enter" (design §3.4 fix).
            if (ok)
                await recovery.TryCompleteAsync(ct);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        // Test connectivity with an unsaved config (nothing is persisted)
        group.MapPost("/test-connection", async (AccountRequest req, IBlobClientFactory factory, IEncryptionService encryption, CancellationToken ct) =>
        {
            // Same check as POST /: an empty key becomes an empty AccountKeyProtected, the decryption choke point throws
            // SecretUnavailableException, and the user is told "the keyring cannot decrypt" — when the real reason is simply that no key was entered.
            if (string.IsNullOrWhiteSpace(req.AccountKey))
                return Results.BadRequest(new { error = "AccountKey is required." });

            var result = await factory.TestConnectionAsync(req.ToAccount(encryption), ct);
            return Results.Ok(result);
        });

        // Test connectivity using an existing account's stored credentials (edit mode).
        //
        // When editing an existing account the Key box is empty ("Leave blank to keep current"), and the id-less
        // endpoint above rejects an empty Key with a flat 400 — so "I changed the endpoint or the proxy and want to
        // check the existing key still connects", the one thing you most need while editing, is exactly what you
        // cannot do. This endpoint fills that in: blank sensitive fields reuse the stored ciphertext, every other
        // field takes the edited value from the request, so what gets tested is "new config + old credentials",
        // the combination that actually needs verifying.
        //
        // It uses the same "blank means keep" copy logic as PUT (ciphertext moved across, never decrypted); the two
        // must stay in step — otherwise you get "tests fine but will not save", or the reverse.
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

        // Credential reset (design §3.4). Deliberately not reusing PUT — PUT is restricted in recovery mode, and here the credentials must be verified before anything is persisted.
        group.MapPost("/{id:int}/reset-secrets", async (
            int id, ResetAccountSecretsRequest req, IAccountService svc, IBlobClientFactory factory,
            IEncryptionService encryption, AppDbContext db, KeyringRecovery recovery, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.AccountKey))
                return Results.BadRequest(new { error = "AccountKey is required." });

            var existing = await svc.GetAsync(id, ct);
            if (existing is null)
                return Results.NotFound();

            // Build a throwaway account object from the candidate credentials and use it to reach the cloud; if verification fails, nothing is persisted.
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

            // Between the existence check above and this write the account may have been deleted (verification goes to the
            // cloud, so the window is not small): FirstAsync would surface as a 500, while the repo-wide convention is 404.
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
    /// Whether this account still needs a credential reset. Short-circuits while Healthy, so the list endpoints trigger no decryption at all (the core property of design §3.1);
    /// while Lost it trial-decrypts record by record, so an account that has already been reset immediately stops showing as "needs re-entry" (design §3.3).
    /// </summary>
    private static bool Pending(IKeyringHealth keyring, IEncryptionService encryption, Account account) =>
        keyring.Status == KeyringStatus.Lost && SecretAvailability.Unreadable(encryption, account);
}
