using Azure;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

public record CreateContainerRequest(string Name);

/// <summary>
/// Container management endpoints under an account (PRD 1.2).
/// Note: Azure Blob does not support renaming a container, hence list/create/delete only.
/// </summary>
public static class ContainerEndpoints
{
    /// <summary>
    /// Translate Azure failures into responses a client can use.
    ///
    /// Caught per endpoint rather than registered as a global handler: a global handler would also take over every unhandled
    /// exception outside the scope of this change, altering existing failure semantics (see the same note in KeyringGuard.cs).
    /// </summary>
    private static IResult MapAzureFailure(RequestFailedException ex)
    {
        // A 4xx is something the caller can fix (invalid name, no permission, already taken by someone else), so pass the
        // status code straight through — except 401: a 401 from an Azure storage account means "this request to the storage
        // account was not authenticated", not "this operator's login session expired". With StorageSharedKeyCredential an
        // authentication failure in Azure itself gives 403; in reality a 401 comes from an intermediate proxy (this project's
        // China / US Government cloud regions land through exactly such a proxy).
        // Passed straight through, the 401 handler in the frontend's client.ts would kick the operator back to the login page,
        // so we turn it into a 502 here, in the same class as the other failures nobody can act on.
        if (ex.Status is >= 400 and < 500 and not StatusCodes.Status401Unauthorized)
            return Results.Json(
                new { error = string.IsNullOrEmpty(ex.ErrorCode) ? ex.Message : $"{ex.ErrorCode}: {ex.Message}" },
                statusCode: ex.Status);

        // Status 0 means the request never got a response (DNS/proxy/network); 401 lands here for the same reason —
        // both are the upstream's problem, not this service's problem and not the user's session's problem. A 502 says which side is at fault.
        return Results.Json(
            new { error = "The storage account could not be reached. Check the endpoint, proxy, and network." },
            statusCode: StatusCodes.Status502BadGateway);
    }

    public static IEndpointRouteBuilder MapContainerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounts/{accountId:int}/containers").WithTags("Containers");

        // List/create/delete container all go to the cloud (design §3.1 explicitly lists "list containers" as an action that
        // needs credentials), so when the keyring is lost we must 409 at the entrance rather than let SecretReader throw somewhere deep inside.
        group.MapGet("/", async (
            int accountId, IAccountService accounts, IContainerService containers, IBackupConfigService configs,
            IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            var account = await accounts.GetAsync(accountId, ct);
            if (account is null)
                return Results.NotFound();

            try
            {
                var list = await containers.ListContainersAsync(account, ct);

                // The cloud-side presence can only say "is the info file there", and that file is written by the very last step
                // of a backup: a container halfway through its first backup already holds this run's uploaded data while the cloud
                // still carries no marker at all, so the listing reports it as an empty container — the user goes by that listing,
                // hands the same container to a second backup, and the two write their own indexes over each other. The authority
                // on occupancy is local: the config row is in the database from the moment it was created, with nothing to wait on in the cloud.
                var held = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var c in await configs.ListAsync(ct))
                    if (c.AccountId == accountId)
                        held.TryAdd(c.ContainerName, c.Name);

                return Results.Ok(list
                    .Select(c => held.TryGetValue(c.Name, out var owner) ? c with { InUseBy = owner } : c)
                    .ToList());
            }
            catch (RequestFailedException ex)
            {
                return MapAzureFailure(ex);
            }
        });

        group.MapPost("/", async (
            int accountId, CreateContainerRequest req,
            IAccountService accounts, IContainerService containers, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            // Validate before going to the cloud: for an invalid name Azure only says "contains invalid characters",
            // naming neither the offending character nor the rule, so relaying that to the user says nothing at all.
            if (ContainerName.Validate(req.Name) is { } invalid)
                return Results.BadRequest(new { error = invalid });

            var account = await accounts.GetAsync(accountId, ct);
            if (account is null)
                return Results.NotFound();

            try
            {
                await containers.CreateContainerAsync(account, req.Name, ct);
            }
            catch (RequestFailedException ex)
            {
                return MapAzureFailure(ex);
            }

            return Results.Created($"/api/accounts/{accountId}/containers/{Uri.EscapeDataString(req.Name)}", new { name = req.Name });
        });

        group.MapDelete("/{name}", async (
            int accountId, string name,
            IAccountService accounts, IContainerService containers, IBackupConfigService configs,
            IKeyringHealth keyring, BackupBusyTracker busy, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            var account = await accounts.GetAsync(accountId, ct);
            if (account is null)
                return Results.NotFound();

            // Held from before the ownership check through the cloud delete: the config-create/import
            // endpoints hold "Creating" across their own check-then-commit, and without this mark a config
            // could be created for this container in the window between "no config owns it" below and the
            // network-bound DeleteContainerAsync — the container then vanishes out from under a config that
            // was legitimately committed. refuseWhenReaders also keeps a mid-download restore protected.
            if (!busy.TryAcquire(accountId, name, "Deleting", refuseWhenReaders: true))
                return Results.Conflict(new
                {
                    error = $"Container '{name}' is busy with another operation; try again once it finishes.",
                });
            try
            {
                // A backup config still hangs off this container → deleting from here is not allowed. Wiping the cloud side while
                // leaving the config in the database keeps a backup listed with nothing behind it, and every operation the user
                // clicks into fails in some shape or another. The delete-the-backup path
                // (DELETE /api/backups/{id}?deleteContainer=true) is the right one: it clears the local index cache, backup state
                // and operation log along with it, and it also blocks "delete while an operation is running". All this does is
                // close the shortcut around it and point the user back there.
                // The check must happen **before touching the cloud**: delete first and report afterwards and the data is already gone; nothing you report then helps.
                // Scoped exactly by (account, container) — BackupConfig has a unique index on those two columns, different accounts
                // may hold containers of the same name, and matching by name alone would let one account's backup block an empty container of the same name in another.
                if (await configs.FindAsync(accountId, name, ct) is { } config)
                {
                    return Results.Conflict(new
                    {
                        error = $"Container '{name}' holds the backup \"{config.Name}\". Delete that backup "
                            + "instead — it offers to remove the container along with it, and only that path "
                            + "also clears the local index cache, backup state and logs. Removing the container "
                            + "here would leave the backup listed with nothing behind it.",
                    });
                }

                try
                {
                    await containers.DeleteContainerAsync(account, name, ct);
                }
                catch (RequestFailedException ex)
                {
                    return MapAzureFailure(ex);
                }

                return Results.NoContent();
            }
            finally
            {
                busy.Release(accountId, name);
            }
        });

        return app;
    }
}
