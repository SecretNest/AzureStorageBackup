using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// With everything protected by default (FallbackPolicy), the only openings are explicit <c>AllowAnonymous()</c> calls.
/// This inventory pins that set of openings down: add one more <c>.AllowAnonymous()</c> anywhere in the future and
/// this test goes red, naming exactly which route was opened.
/// Without it, an accidentally opened business route slips through with CI fully green — and the deny-by-default design was for nothing.
/// </summary>
public class AnonymousEndpointInventoryTests
{
    /// <summary>The complete anonymous allowlist: 3 auth endpoints + 2 health probes + the SPA fallback.</summary>
    private static readonly string[] Expected =
    [
        "GET /api/auth/status",
        "GET /api/health",
        "GET /api/health/ready",
        "GET,HEAD {*path:nonfile}",
        "POST /api/auth/login",
        "POST /api/auth/logout",
    ];

    private sealed class AuthEnabledFactory : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:Password", "s3cret");
        }
    }

    [Fact]
    public void Only_The_Six_Documented_Endpoints_Are_Anonymous()
    {
        using var factory = new AuthEnabledFactory();
        _ = factory.CreateClient(); // WebApplicationFactory builds the host lazily; force it up first

        var actual = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(e => e.Metadata.OfType<IAllowAnonymous>().Any())
            .Select(Describe)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var opened = actual.Except(Expected, StringComparer.Ordinal).ToArray();
        var closed = Expected.Except(actual, StringComparer.Ordinal).ToArray();

        Assert.True(
            opened.Length == 0 && closed.Length == 0,
            "The set of AllowAnonymous endpoints changed."
                + (opened.Length > 0
                    ? "\n  Newly anonymous (remove AllowAnonymous, or add it to the expected list on purpose): "
                        + string.Join(", ", opened)
                    : string.Empty)
                + (closed.Length > 0
                    ? "\n  No longer anonymous (login/health/SPA fallback must stay reachable): "
                        + string.Join(", ", closed)
                    : string.Empty)
                + "\n  Full actual set: " + string.Join(", ", actual));
    }

    /// <summary>Stable identity: HTTP methods + the raw route pattern, independent of endpoint registration order and display name.</summary>
    private static string Describe(Endpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        var verbs = methods is { Count: > 0 }
            ? string.Join(",", methods.OrderBy(m => m, StringComparer.Ordinal))
            : "*";
        var pattern = (endpoint as RouteEndpoint)?.RoutePattern.RawText
            ?? endpoint.DisplayName
            ?? "<unknown>";
        return $"{verbs} {pattern}";
    }
}
