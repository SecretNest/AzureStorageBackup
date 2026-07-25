using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>
/// 预置密码登录（设计 §4.1）。三个端点**逐个**标 AllowAnonymous——否则永远登不进来。
/// 不标在 group 上：将来往 /api/auth 里加的端点（比如「修改密码」）会默默继承匿名，
/// 那正是最不该匿名的地方。
/// </summary>
public static class AuthEndpoints
{
    /// <summary>登录失败的固定延迟，使在线爆破不划算（设计 §4.3）。</summary>
    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 失败路径全局串行：每请求各睡 1 秒挡不住并发爆破——N 个请求同时在飞，摊到每次尝试
    /// 的代价接近 0。串行后 N 次失败要花 N 秒真实时间，代价才随尝试次数线性增长。
    /// 单用户工具，串行只影响失败路径，登录成功永不排队，故不会把自己锁在门外。
    /// </summary>
    private static readonly SemaphoreSlim FailureGate = new(1, 1);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", async (LoginRequest req, AuthGate gate, HttpContext ctx, ILoggerFactory loggerFactory) =>
        {
            if (!gate.Required)
                return Results.NoContent(); // 认证关闭时登录是空操作

            if (!gate.Verify(req.Password))
            {
                // 只记「有人试了、从哪来」，绝不记提交的密码（设计 §4.3）。
                loggerFactory.CreateLogger(typeof(AuthEndpoints)).LogWarning(
                    "Failed login attempt from {RemoteIpAddress}.",
                    ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");

                // 刻意不接 RequestAborted：否则攻击者只要立刻掐断连接就能提前释放闸门，
                // 串行化形同虚设。代价是关机时最多多等 1 秒。
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
                new AuthenticationProperties { IsPersistent = true }); // 落地 30 天滑动会话（设计 §1 决策 7）

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

        // 前端唯一的决策依据，未认证时也必须可读（设计 §4.1）。
        group.MapGet("/status", (AuthGate gate, HttpContext ctx) =>
            Results.Ok(new AuthStatusResponse(
                Required: gate.Required,
                Authenticated: !gate.Required || ctx.User.Identity?.IsAuthenticated == true)))
        .AllowAnonymous();

        return app;
    }
}

/// <summary>登录请求体。无用户名（设计决策 1）。</summary>
public record LoginRequest(string Password);

/// <summary>认证状态。Required=false 时 Authenticated 恒为 true，前端据此直接进主界面。</summary>
public record AuthStatusResponse(bool Required, bool Authenticated);
