# 预置密码访问控制 —— 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 进入系统需输入密码，密码由环境变量 `Auth__Password` 预置，无用户名；不设即无认证。

**Architecture:** ASP.NET Core cookie 认证，由现有 Data Protection 密钥环签发。用 `FallbackPolicy` 实现「默认全保护」，只给豁免项显式加 `AllowAnonymous()` —— 这样将来新增端点默认受保护，漏加的后果是「多挡一个」而不是「漏开一个洞」。密码比对走恒定时间比较，不经密钥环，因此密钥环丢失时仍可登录。

**Tech Stack:** .NET 10 / ASP.NET Core Minimal API、Cookie 认证（**在 `Microsoft.AspNetCore.App` 共享框架内，无需新增 PackageReference**）、React + TypeScript（Vite）、xUnit + `Microsoft.AspNetCore.Mvc.Testing`。

设计依据：[auth-password-design.md](auth-password-design.md)。实施前请通读该文件第 1 节的 8 条决策。

## Global Constraints

- 界面文案一律英文（含 API 返回给用户的文案）；代码注释与文档用中文，与现有代码保持一致。
- 配置键 `Auth:Password`，环境变量形式 `Auth__Password`。镜像**不得**为它设默认值。
- 未设置或为空 = 认证关闭，全部放行，启动记一条 Warning。
- 未认证的 API 请求返回 **401**，不重定向。
- 密码永不写入日志、永不出现在错误响应里。
- 不做用户名、多用户、密码修改界面、账户锁定，不保护静态资源。
- 不得产生 schema 变更。
- 后端全量测试命令：`dotnet test backend/AzureStorageBackup.slnx`，须全绿且 `dotnet build` 0 warnings。
- 前端：`cd frontend && npm run build` 与 `npm run lint` 均须干净。
- 提交信息用英文，`type: subject` 格式。

---

### Task 1: `AuthGate` —— 密码判定的唯一入口

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/AuthGate.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/AuthGateTests.cs`

**Interfaces:**
- Produces: `AuthGate(IConfiguration)`；`bool Required { get; }`；`bool Verify(string? candidate)`

- [ ] **Step 1: 写失败的测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/AuthGateTests.cs`：

```csharp
using AzureStorageBackup.Api.Services;
using Microsoft.Extensions.Configuration;

namespace AzureStorageBackup.Api.Tests;

public class AuthGateTests
{
    private static AuthGate Create(string? password)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(password is null
                ? []
                : new Dictionary<string, string?> { ["Auth:Password"] = password })
            .Build();
        return new AuthGate(config);
    }

    [Fact]
    public void Not_Required_When_Password_Is_Absent() => Assert.False(Create(null).Required);

    [Fact]
    public void Not_Required_When_Password_Is_Empty() => Assert.False(Create("").Required);

    [Fact]
    public void Required_When_Password_Is_Set() => Assert.True(Create("s3cret").Required);

    [Fact]
    public void Verify_Accepts_The_Configured_Password()
        => Assert.True(Create("s3cret").Verify("s3cret"));

    [Fact]
    public void Verify_Rejects_A_Wrong_Password()
        => Assert.False(Create("s3cret").Verify("wrong"));

    [Fact]
    public void Verify_Rejects_A_Password_Of_Different_Length()
        => Assert.False(Create("s3cret").Verify("s3cretx"));

    [Fact]
    public void Verify_Rejects_Null_And_Empty()
    {
        var sut = Create("s3cret");
        Assert.False(sut.Verify(null));
        Assert.False(sut.Verify(""));
    }

    [Fact]
    public void Verify_Always_True_When_Not_Required()
    {
        // 认证关闭时不该有任何东西被拒——调用方据此放行
        var sut = Create(null);
        Assert.True(sut.Verify(null));
        Assert.True(sut.Verify("anything"));
    }

    [Fact]
    public void Verify_Handles_Non_Ascii_Passwords()
        => Assert.True(Create("пароль密码").Verify("пароль密码"));
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~AuthGateTests`
Expected: 编译失败，`The type or namespace name 'AuthGate' could not be found`

- [ ] **Step 3: 实现**

创建 `backend/src/AzureStorageBackup.Api/Services/AuthGate.cs`：

```csharp
using System.Security.Cryptography;
using System.Text;

namespace AzureStorageBackup.Api.Services;

/// <summary>
/// 预置密码判定（设计 §2、§4.3）。密码来自环境变量明文，**不经 Data Protection**——
/// 因此密钥环丢失时仍能登录，进而走密钥环恢复流程（设计 §5）。
/// 单例：构造时读一次配置，之后不再变（改密码需改环境变量并重启）。
/// </summary>
public sealed class AuthGate
{
    private readonly byte[]? _expected;

    public AuthGate(IConfiguration config)
    {
        var password = config["Auth:Password"];
        _expected = string.IsNullOrEmpty(password) ? null : Encoding.UTF8.GetBytes(password);
    }

    /// <summary>是否启用认证。未配置密码时为 false，全部放行。</summary>
    public bool Required => _expected is not null;

    /// <summary>
    /// 校验密码。未启用认证时恒为 true。
    /// 用恒定时间比较防时序侧信道；长度不同直接失败（长度差异本就无法隐藏）。
    /// </summary>
    public bool Verify(string? candidate)
    {
        if (_expected is null)
            return true;
        if (string.IsNullOrEmpty(candidate))
            return false;

        var actual = Encoding.UTF8.GetBytes(candidate);
        return CryptographicOperations.FixedTimeEquals(_expected, actual);
    }
}
```

- [ ] **Step 4: 运行测试，确认通过**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~AuthGateTests`
Expected: PASS，9 passed

- [ ] **Step 5: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/AuthGate.cs \
        backend/tests/AzureStorageBackup.Api.Tests/AuthGateTests.cs
git commit -m "feat: add AuthGate for preset-password verification"
```

---

### Task 2: 后端接线 —— cookie 认证、默认保护、三个端点

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Endpoints/AuthEndpoints.cs`
- Modify: `backend/src/AzureStorageBackup.Api/Program.cs`（DI 区、`:136-139` CORS、`:168-191` 管线）
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/HealthEndpoints.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/AuthEndpointsTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `AuthGate.Required` / `AuthGate.Verify`
- Produces: `POST /api/auth/login`（body `LoginRequest(string Password)`）、`POST /api/auth/logout`、`GET /api/auth/status`（`AuthStatusResponse(bool Required, bool Authenticated)`）

**三个必须踩对的点（设计 §3、§4.2）：**

1. **`CookieSecurePolicy.SameAsRequest`，绝不能用 `Always`。** 镜像默认监听 HTTP（`Dockerfile:33`）；强制 `Secure` 会让浏览器根本不回传 cookie，症状是「登录成功但立刻又被要求登录」。
2. **`MapFallbackToFile("index.html")` 必须 `.AllowAnonymous()`。** 它是一个**端点**，会被 `FallbackPolicy` 拦截。漏掉的话未登录时连 `index.html` 都拿不到，登录页根本渲染不出来 —— 变成「要登录得先登录」的死锁。静态资源本身由 `UseStaticFiles` 处理、不走端点路由，不受影响。
3. **健康探针必须 `.AllowAnonymous()`。** 否则 `docker healthcheck` 与编排层探针全变 401，容器被判不健康后反复重启。

- [ ] **Step 1: 写失败的测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/AuthEndpointsTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AzureStorageBackup.Api.Tests;

public class AuthEndpointsTests
{
    private sealed record AuthStatus(bool Required, bool Authenticated);

    /// <summary>启用认证的测试主机；password 为 null 表示不设密码。</summary>
    private static TestWebAppFactory Factory(string? password) =>
        password is null
            ? new TestWebAppFactory()
            : new AuthTestWebAppFactory(password);

    private sealed class AuthTestWebAppFactory(string password) : TestWebAppFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Auth:Password", password);
        }
    }

    /// <summary>不跟随重定向、保留 cookie 的客户端。</summary>
    private static HttpClient Client(WebApplicationFactory<Program> f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    [Fact]
    public async Task Without_A_Password_Everything_Is_Open()
    {
        using var factory = Factory(null);
        var client = Client(factory);

        var status = await client.GetFromJsonAsync<AuthStatus>("/api/auth/status");
        Assert.False(status!.Required);

        var accounts = await client.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.OK, accounts.StatusCode);
    }

    [Fact]
    public async Task With_A_Password_Api_Requires_Authentication()
    {
        using var factory = Factory("s3cret");
        var client = Client(factory);

        var accounts = await client.GetAsync("/api/accounts");

        Assert.Equal(HttpStatusCode.Unauthorized, accounts.StatusCode);
    }

    [Fact]
    public async Task Status_Is_Reachable_Without_Authentication()
    {
        using var factory = Factory("s3cret");
        var client = Client(factory);

        var res = await client.GetAsync("/api/auth/status");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var status = await res.Content.ReadFromJsonAsync<AuthStatus>();
        Assert.True(status!.Required);
        Assert.False(status.Authenticated);
    }

    [Fact]
    public async Task Health_Probes_Stay_Open_When_A_Password_Is_Set()
    {
        // 探针被挡住会让 docker healthcheck 判定容器不健康并反复重启
        using var factory = Factory("s3cret");
        var client = Client(factory);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health/ready")).StatusCode);
    }

    [Fact]
    public async Task Spa_Fallback_Stays_Open_When_A_Password_Is_Set()
    {
        // 挡住 index.html 会让登录页根本渲染不出来——「要登录得先登录」
        using var factory = Factory("s3cret");
        var client = Client(factory);

        var res = await client.GetAsync("/");

        Assert.NotEqual(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Correct_Password_Grants_Access_To_The_Api()
    {
        using var factory = Factory("s3cret");
        var client = Client(factory);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { password = "s3cret" });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        var accounts = await client.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.OK, accounts.StatusCode);

        var status = await client.GetFromJsonAsync<AuthStatus>("/api/auth/status");
        Assert.True(status!.Authenticated);
    }

    [Fact]
    public async Task Wrong_Password_Is_Rejected_And_Grants_Nothing()
    {
        using var factory = Factory("s3cret");
        var client = Client(factory);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        var accounts = await client.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, accounts.StatusCode);
    }

    [Fact]
    public async Task Logout_Revokes_Access()
    {
        using var factory = Factory("s3cret");
        var client = Client(factory);
        await client.PostAsJsonAsync("/api/auth/login", new { password = "s3cret" });

        var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var accounts = await client.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, accounts.StatusCode);
    }

    [Fact]
    public async Task Login_Works_While_The_Keyring_Is_Lost()
    {
        // 设计 §5：密码比对读环境变量明文、不经密钥环，所以密钥环丢失时仍能登录，
        // 进而走恢复流程。若登录反过来依赖密钥环，就成了「要恢复得先登录，要登录得先恢复」。
        using var factory = Factory("s3cret");
        var client = Client(factory);
        factory.Services.GetRequiredService<IKeyringHealth>().Set(KeyringStatus.Lost);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { password = "s3cret" });

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        // 登录成功后应当能看到恢复所需的状态端点
        var keyring = await client.GetAsync("/api/system/keyring");
        Assert.Equal(HttpStatusCode.OK, keyring.StatusCode);
    }
}
```

该用例需在文件顶部补 `using AzureStorageBackup.Api.Services;` 与 `using Microsoft.Extensions.DependencyInjection;`。

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~AuthEndpointsTests`
Expected: FAIL —— `Without_A_Password_Everything_Is_Open` 因 `/api/auth/status` 返回 404 而失败；带密码的用例因未启用认证而拿到 200 而非 401

- [ ] **Step 3: 新建端点文件**

创建 `backend/src/AzureStorageBackup.Api/Endpoints/AuthEndpoints.cs`：

```csharp
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>预置密码登录（设计 §4.1）。三个端点均 AllowAnonymous——否则永远登不进来。</summary>
public static class AuthEndpoints
{
    /// <summary>登录失败的固定延迟，使在线爆破不划算（设计 §4.3）。</summary>
    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(1);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth").AllowAnonymous();

        group.MapPost("/login", async (LoginRequest req, AuthGate gate, HttpContext ctx) =>
        {
            if (!gate.Required)
                return Results.NoContent(); // 认证关闭时登录是空操作

            if (!gate.Verify(req.Password))
            {
                await Task.Delay(FailureDelay);
                return Results.Json(new { error = "Incorrect password." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "owner")],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

            return Results.NoContent();
        });

        group.MapPost("/logout", async (AuthGate gate, HttpContext ctx) =>
        {
            if (gate.Required)
                await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        group.MapGet("/status", (AuthGate gate, HttpContext ctx) =>
            Results.Ok(new AuthStatusResponse(
                Required: gate.Required,
                Authenticated: !gate.Required || ctx.User.Identity?.IsAuthenticated == true)));

        return app;
    }
}

/// <summary>登录请求体。无用户名（设计决策 1）。</summary>
public record LoginRequest(string Password);

/// <summary>认证状态。Required=false 时 Authenticated 恒为 true，前端据此直接进主界面。</summary>
public record AuthStatusResponse(bool Required, bool Authenticated);
```

- [ ] **Step 4: 接入 DI 与认证配置**

`backend/src/AzureStorageBackup.Api/Program.cs`，在 CORS 注册（`:136`）之前插入：

```csharp
// --- 预置密码访问控制（设计 §2/§3）---
var authGate = new AuthGate(builder.Configuration);
builder.Services.AddSingleton(authGate);

if (authGate.Required)
{
    builder.Services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "asb_auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            // 镜像默认监听 HTTP；硬编码 Always 会让浏览器根本不回传 cookie，
            // 症状是「登录成功但立刻又被要求登录」。跟随请求协议才对。
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
            // SPA + fetch：未认证返回 401，重定向只会让 fetch 拿到一份 HTML。
            options.Events.OnRedirectToLogin = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

    // 默认全保护：将来新增端点自动受保护，漏加的后果是「多挡一个」而非「漏开一个洞」。
    builder.Services.AddAuthorization(options =>
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build());
}
```

文件顶部补 `using Microsoft.AspNetCore.Authentication.Cookies;` 和 `using Microsoft.AspNetCore.Authorization;`。

**不要新增 PackageReference** —— Cookie 认证在 `Microsoft.AspNetCore.App` 共享框架内。

- [ ] **Step 5: 接入管线并放行豁免项**

`Program.cs`，在 `app.UseStaticFiles();`（`:172`）之后、`app.UseSecretUnavailableMapping();`（`:176`）之前插入：

```csharp
if (authGate.Required)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
else
{
    app.Logger.LogWarning("Authentication is disabled: Auth__Password is not set.");
}
```

注册 auth 端点 —— 在 `app.MapHealthEndpoints();`（`:178`）之前加：

```csharp
app.MapAuthEndpoints();
```

把 SPA 兜底改为放行（`:191`）：

```csharp
app.MapFallbackToFile("index.html").AllowAnonymous();
```

`backend/src/AzureStorageBackup.Api/Endpoints/HealthEndpoints.cs`：给两个探针端点各追加 `.AllowAnonymous()`，与既有的 `.WithName(...)`/`.WithTags(...)` 链式调用并列。

- [ ] **Step 6: 允许跨域携带 cookie（仅影响本地开发）**

`Program.cs:136-139` 的 CORS 策略追加 `.AllowCredentials()`：

```csharp
builder.Services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
```

生产是同源单镜像部署，此项只为本地 Vite dev server（`localhost:5173` → `localhost:8080`）能带上 cookie。

- [ ] **Step 7: 运行测试，确认通过**

Run: `dotnet test backend/AzureStorageBackup.slnx --filter FullyQualifiedName~AuthEndpointsTests`
Expected: PASS，9 passed

- [ ] **Step 8: 全量测试**

Run: `dotnet test backend/AzureStorageBackup.slnx`
Expected: 全绿，0 warnings。既有测试均未设 `Auth:Password`，因此走认证关闭路径，行为不变。

- [ ] **Step 9: 提交**

```bash
git add -A backend/
git commit -m "feat: gate the API behind a preset password"
```

---

### Task 3: 前端登录门

**Files:**
- Create: `frontend/src/api/auth.ts`
- Create: `frontend/src/components/LoginPage.tsx`
- Modify: `frontend/src/App.tsx`
- Modify: `frontend/src/api/client.ts`

**Interfaces:**
- Consumes: Task 2 的 `GET /api/auth/status`、`POST /api/auth/login`、`POST /api/auth/logout`

- [ ] **Step 1: 新增 auth API 模块**

创建 `frontend/src/api/auth.ts`：

```typescript
import { api } from './client'

export interface AuthStatus {
  required: boolean
  authenticated: boolean
}

export const authApi = {
  status: () => api.get<AuthStatus>('/auth/status'),
  login: (password: string) => api.post<void>('/auth/login', { password }),
  logout: () => api.post<void>('/auth/logout', {}),
}
```

- [ ] **Step 2: 让 401 能通知到 App**

`frontend/src/api/client.ts` —— 在 `request` 抛出 `ApiError` 之前，对 401 触发一个回调。在文件中 `const BASE = '/api'` 之后加：

```typescript
// 会话过期时由 App 重新挂上登录页（设计 §6）。
let onUnauthorized: (() => void) | null = null

export function setUnauthorizedHandler(handler: () => void) {
  onUnauthorized = handler
}
```

并在 `request` 内 `if (!res.ok) {` 之后、构造 `ApiError` 之前插入：

```typescript
    if (res.status === 401) onUnauthorized?.()
```

- [ ] **Step 3: 新增登录页**

创建 `frontend/src/components/LoginPage.tsx`：

```tsx
import { useState } from 'react'
import { authApi } from '../api/auth'

/** 预置密码登录页（设计 §6）。无用户名；文案一律英文。 */
export function LoginPage({ onSignedIn }: { onSignedIn: () => void }) {
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await authApi.login(password)
      setPassword('')
      onSignedIn()
    } catch {
      setError('Incorrect password.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div style={{ maxWidth: 320, margin: '6rem auto', padding: '0 1rem' }}>
      <h1 style={{ fontSize: '1.25rem', marginBottom: '1rem' }}>Azure Storage Backup</h1>
      <form onSubmit={submit}>
        <input
          type="password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          placeholder="Password"
          autoFocus
          style={{ width: '100%', padding: '0.5rem', marginBottom: '0.75rem' }}
        />
        <button type="submit" disabled={busy || !password} style={{ width: '100%', padding: '0.5rem' }}>
          {busy ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
      {error && <p style={{ color: '#b91c1c', marginTop: '0.75rem' }}>{error}</p>}
    </div>
  )
}
```

- [ ] **Step 4: 在 App 里挂上门**

`frontend/src/App.tsx` —— 加 import：

```tsx
import { useEffect, useState } from 'react'
import { LoginPage } from './components/LoginPage'
import { authApi, type AuthStatus } from './api/auth'
import { setUnauthorizedHandler } from './api/client'
```

在 `function App() {` 内、`const [tab, ...]` 之后加：

```tsx
  const [auth, setAuth] = useState<AuthStatus | null>(null)

  const refreshAuth = () => {
    authApi.status().then(setAuth).catch(() => setAuth({ required: true, authenticated: false }))
  }
  useEffect(() => {
    setUnauthorizedHandler(() => setAuth({ required: true, authenticated: false }))
    refreshAuth()
  }, [])
```

在 `return (` 之前加两个早退分支：

```tsx
  // 状态未知时不渲染任何东西，避免主界面闪一下再被登录页替换
  if (auth === null) return null

  // 未认证时**不挂载**主界面组件——挂了它们会各自发请求，拿回一片 401
  if (auth.required && !auth.authenticated)
    return <LoginPage onSignedIn={refreshAuth} />
```

在 `<nav>` 内、`{tabs.map(...)}` 之后加登出按钮：

```tsx
        {auth.required && (
          <button
            type="button"
            onClick={() => authApi.logout().then(() => setAuth({ required: true, authenticated: false }))}
            style={{ marginLeft: 'auto' }}
          >
            Log out
          </button>
        )}
```

- [ ] **Step 5: 构建与 lint**

```bash
cd frontend && npm run build && npm run lint
```

Expected: 构建成功、无 TypeScript 报错；oxlint 干净

- [ ] **Step 6: 提交**

```bash
git add -A frontend/
git commit -m "feat: add the login gate to the UI"
```

---

### Task 4: 文档

**Files:**
- Modify: `README.md`（环境变量表与其下方的注记）

- [ ] **Step 1: 增加环境变量条目**

`README.md` 的环境变量表中，在 `Scheduler__TimeZone` 一行之后插入：

```markdown
| `Auth__Password` | Password required to open the UI. Unset or empty = no authentication (the app logs a warning at startup). There is no username. | *(unset)* |
```

- [ ] **Step 2: 增加说明段**

在该表下方已有的两条 `>` 注记之后，追加：

```markdown
> Setting `Auth__Password` puts a single password in front of the whole UI — there is no username, and changing the password means changing the variable and restarting. The session cookie is signed with the Data Protection key ring in `/keys`, so losing that directory signs you out and you will have to log in again; the password itself is read straight from the environment, so a lost key ring never locks you out.
>
> **Serve this behind an HTTPS reverse proxy in production.** Over plain HTTP both the password and the session cookie travel in the clear — the password gate keeps out people who do not know it, not anyone who can watch the traffic.
```

- [ ] **Step 3: 提交**

```bash
git add README.md
git commit -m "docs: document Auth__Password and its HTTPS caveat"
```

---

## 完成后的验证

- [ ] `dotnet test backend/AzureStorageBackup.slnx` 全绿、`dotnet build` 0 warnings
- [ ] `cd frontend && npm run build && npm run lint` 干净
- [ ] 手工验证：不带 `Auth__Password` 启动 → 界面直接可用；带 `-e Auth__Password=test` 启动 → 出现登录页，错密码被拒、对密码进入、刷新页面仍在登录态、点 `Log out` 回到登录页；两种模式下 `curl /api/health/ready` 都不是 401
