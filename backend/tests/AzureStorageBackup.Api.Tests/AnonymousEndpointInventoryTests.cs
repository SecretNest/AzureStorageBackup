using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>
/// 默认全保护（FallbackPolicy）下，唯一的开口是显式 <c>AllowAnonymous()</c>。
/// 这份清单把开口集合钉死：将来任何一处多写一个 <c>.AllowAnonymous()</c>，
/// 这个测试就会红，并直接报出是哪条路由被打开了。
/// 没有它，一条被误开的业务路由会在 CI 全绿的情况下溜过去——默认拒绝的设计就白做了。
/// </summary>
public class AnonymousEndpointInventoryTests
{
    /// <summary>允许匿名的完整清单：3 个 auth 端点 + 2 个健康探针 + SPA 回退。</summary>
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
        _ = factory.CreateClient(); // WebApplicationFactory 惰性建主机，先逼它建起来

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

    /// <summary>稳定标识：HTTP 方法 + 路由模板原文，与端点的注册顺序、显示名无关。</summary>
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
