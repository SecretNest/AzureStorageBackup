using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>Response body for the 409. The gate and the defence-in-depth mapping share one shape, so the frontend only has to recognize a single code.</summary>
public sealed record KeyringLostError(string error, string code);

/// <summary>
/// Recovery-mode gate (design §3.3): while the keyring is lost, every action that needs credentials fails with a 409 at the entrance
/// and never reaches the orchestration layer — so no cloud operation is ever started with a password that cannot be decrypted.
/// </summary>
public static class KeyringGuard
{
    public static KeyringLostError Payload => new(
        "Data protection keys were lost; re-enter credentials before running this action.",
        "keyring_lost");

    public static IResult? Blocked(IKeyringHealth health) =>
        health.Status == KeyringStatus.Lost
            ? Results.Json(Payload, statusCode: StatusCodes.Status409Conflict)
            : null;
}

/// <summary>
/// Defence in depth (design §3.1): when a new code path bypasses the gate, or the keyring is swapped out while the process
/// is running (the canary not yet re-evaluated), the <see cref="SecretUnavailableException"/> from the choke point bubbles all
/// the way up. With nobody catching it the client gets a bare 500, which the frontend has no way to connect to "the keyring was
/// lost". Here it is mapped uniformly to 409 + keyring_lost, the same shape as <see cref="KeyringGuard"/>.
///
/// Deliberately not UseExceptionHandler/IExceptionHandler: that route means registering either a catch-all handler or
/// ProblemDetails, and both take over **every** unhandled exception, changing failure semantics well outside the scope of this fix
/// (including quietly turning exceptions that integration tests expect to be thrown into 500 responses). This recognizes only this one exception and rethrows the rest untouched.
/// </summary>
public static class SecretUnavailableMapping
{
    public static IApplicationBuilder UseSecretUnavailableMapping(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (SecretUnavailableException)
            {
                // Once the response has started (mid stream download, say) the status code cannot be changed, so let the exception keep bubbling.
                if (context.Response.HasStarted)
                    throw;

                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(KeyringGuard.Payload);
            }
        });
}
