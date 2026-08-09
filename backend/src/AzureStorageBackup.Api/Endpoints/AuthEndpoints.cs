using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>
/// Login with a preset password (design §4.1). The three endpoints are marked AllowAnonymous **one by one** — otherwise nobody could ever get in.
/// Not marked on the group: an endpoint added to /api/auth later (say "change password") would silently inherit anonymous access,
/// and that is exactly the last place that should be anonymous.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>Fixed delay on a failed login, to make online brute force not worth it (design §4.3).</summary>
    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The failure path is serialized globally: a per-request 1-second sleep does not stop concurrent brute force — with N requests
    /// in flight at once the cost per attempt approaches 0. Serialized, N failures take N seconds of wall clock, so the cost finally grows linearly with attempts.
    /// This is a single-user tool, serialization touches only the failure path, and a successful login never queues, so you cannot lock yourself out with it.
    /// </summary>
    private static readonly SemaphoreSlim FailureGate = new(1, 1);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", async (LoginRequest req, AuthGate gate, HttpContext ctx, ILoggerFactory loggerFactory) =>
        {
            if (!gate.Required)
                return Results.NoContent(); // with authentication off, login is a no-op

            if (!gate.Verify(req.Password))
            {
                // Log only "someone tried, and from where" — never the submitted password (design §4.3).
                loggerFactory.CreateLogger(typeof(AuthEndpoints)).LogWarning(
                    "Failed login attempt from {RemoteIpAddress}.",
                    ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");

                // Deliberately not wired to RequestAborted: otherwise an attacker could release the gate early just by dropping
                // the connection, and the serialization would be worthless. The price is up to 1 extra second at shutdown.
                await FailureGate.WaitAsync();
                try
                {
                    await Task.Delay(FailureDelay);
                }
                finally
                {
                    FailureGate.Release();
                }

                return Results.Json(new { error = "Incorrect password." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "owner")],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true }); // makes the 30-day sliding session real (design §1 decision 7)

            return Results.NoContent();
        })
        .AllowAnonymous();

        group.MapPost("/logout", async (AuthGate gate, HttpContext ctx) =>
        {
            if (gate.Required)
                await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        })
        .AllowAnonymous();

        // The frontend's only basis for deciding anything, so it must be readable while unauthenticated (design §4.1).
        group.MapGet("/status", (AuthGate gate, HttpContext ctx) =>
            Results.Ok(new AuthStatusResponse(
                Required: gate.Required,
                Authenticated: !gate.Required || ctx.User.Identity?.IsAuthenticated == true)))
        .AllowAnonymous();

        return app;
    }
}

/// <summary>Login request body. No username (design decision 1).</summary>
public record LoginRequest(string Password);

/// <summary>Authentication status. When Required=false, Authenticated is always true and the frontend goes straight to the main UI.</summary>
public record AuthStatusResponse(bool Required, bool Authenticated);
