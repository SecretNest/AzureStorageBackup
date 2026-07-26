# 网页界面视觉改版 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把界面从 Vite 脚手架残留 + 内联样式的状态，改造成一套基于 CSS 变量的紧凑运维控制台外观；同时修掉新建 container 返回裸 500 的缺陷。

**Architecture:** 后端逐端点捕获 `RequestFailedException` 并新增 container 名校验器，不引入全局异常处理器。前端建立单一全局样式表 `src/index.css`（design token + 元素默认样式 + 少量语义类），把 `App.tsx` 的顶部按钮式 tab 改为左侧栏外壳，然后逐页删除 211 处内联 `style`。

**Tech Stack:** .NET / ASP.NET Core Minimal API、xUnit；React 19 + TypeScript + Vite 8、原生 CSS。**不新增任何 npm 依赖。**

## Global Constraints

- **界面文案一律英文**。本文档与代码注释用中文，但任何用户可见字符串必须是英文。
- **`frontend/package.json` 不得新增依赖**，dependencies 与 devDependencies 均不变。
- **不引入全局异常处理器**（`UseExceptionHandler` / `IExceptionHandler` / `AddProblemDetails`）。`backend/src/AzureStorageBackup.Api/Endpoints/KeyringGuard.cs:30` 记录了这个决定：全局 handler 会接管全部未处理异常，改变本轮范围之外的失败语义。
- **后端错误响应形状统一为 `new { error = "…" }`**，与 `AccountEndpoints.cs:31` 等一致。不使用 ProblemDetails。
- **不改动任何交互流程**：不拆分 `BackupConfigsPage`、不新增页面、不引入路由、不引入前端测试框架。
- 后端测试命令：`cd backend && dotnet test`。前端检查命令：`cd frontend && npm run build && npm run lint`。
- 每个任务结束时提交一次。

---

### Task 1: Container 名校验器

**Files:**
- Create: `backend/src/AzureStorageBackup.Api/Services/ContainerName.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/ContainerNameTests.cs`

**Interfaces:**
- Consumes: 无
- Produces: `static string? AzureStorageBackup.Api.Services.ContainerName.Validate(string? name)` — 合法返回 `null`，非法返回一句英文说明。

- [ ] **Step 1: 写失败测试**

创建 `backend/tests/AzureStorageBackup.Api.Tests/ContainerNameTests.cs`：

```csharp
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Tests;

public class ContainerNameTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("my-backup-2024")]
    [InlineData("a1b")]
    [InlineData("0123456789")]
    public void Accepts_Valid_Names(string name) =>
        Assert.Null(ContainerName.Validate(name));

    // Validate 返回 string?，直接塞进 Assert.Contains 会触发可空警告；
    // 先断言非空，编译器随后把它收窄为 string。
    private static void AssertRejected(string? name, string expectedFragment)
    {
        var message = ContainerName.Validate(name);
        Assert.NotNull(message);
        Assert.Contains(expectedFragment, message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    public void Rejects_Too_Short(string? name) => AssertRejected(name, "3 and 63");

    [Fact]
    public void Rejects_Too_Long() => AssertRejected(new string('a', 64), "3 and 63");

    [Theory]
    [InlineData("MyBackup")]
    [InlineData("my_backup")]
    [InlineData("my.backup")]
    [InlineData("my backup")]
    public void Rejects_Disallowed_Characters(string name) =>
        AssertRejected(name, "lowercase letters, digits, and hyphens");

    [Theory]
    [InlineData("-abc")]
    [InlineData("abc-")]
    public void Rejects_Hyphen_At_Either_End(string name) =>
        AssertRejected(name, "begin and end");

    [Fact]
    public void Rejects_Consecutive_Hyphens() => AssertRejected("a--b", "consecutive");
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `cd backend && dotnet test --filter FullyQualifiedName~ContainerNameTests`
Expected: 编译失败，`error CS0103: The name 'ContainerName' does not exist in the current context`

- [ ] **Step 3: 实现校验器**

创建 `backend/src/AzureStorageBackup.Api/Services/ContainerName.cs`：

```csharp
namespace AzureStorageBackup.Api.Services;

/// <summary>
/// Azure Blob container 命名规则的本地校验。
///
/// 存在的理由是错误消息：Azure 对非法名回的是 "The specifed resource name contains
/// invalid characters."，既不指出是哪个字符、也不说明规则，用户看到只能瞎猜。在连云之前
/// 自己判一次，就能给出可操作的说明。
/// </summary>
public static class ContainerName
{
    /// <summary>合法返回 <c>null</c>；非法返回一句英文说明（直接作为 API 的 error 文案）。</summary>
    public static string? Validate(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length is < 3 or > 63)
            return "Container name must be between 3 and 63 characters long.";

        foreach (var c in name)
            if (!(c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c == '-'))
                return "Container name may only contain lowercase letters, digits, and hyphens.";

        if (name[0] == '-' || name[^1] == '-')
            return "Container name must begin and end with a letter or a digit.";

        if (name.Contains("--", StringComparison.Ordinal))
            return "Container name may not contain consecutive hyphens.";

        return null;
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `cd backend && dotnet test --filter FullyQualifiedName~ContainerNameTests`
Expected: PASS，15 个用例全绿

- [ ] **Step 5: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Services/ContainerName.cs \
        backend/tests/AzureStorageBackup.Api.Tests/ContainerNameTests.cs
git commit -m "feat: validate container names locally before calling Azure"
```

---

### Task 2: Container 端点的错误映射

**Files:**
- Modify: `backend/src/AzureStorageBackup.Api/Endpoints/ContainerEndpoints.cs`
- Test: `backend/tests/AzureStorageBackup.Api.Tests/ContainerEndpointErrorTests.cs`

**Interfaces:**
- Consumes: `ContainerName.Validate(string?)`（Task 1）；`IContainerService`（`Services/IContainerService.cs`）
- Produces: 无新公开 API；三个端点的失败响应体统一为 `{ "error": "…" }`

- [ ] **Step 1: 写失败测试**

先看 `backend/tests/AzureStorageBackup.Api.Tests/TestWebAppFactory.cs` 了解测试宿主如何替换服务。创建 `backend/tests/AzureStorageBackup.Api.Tests/ContainerEndpointErrorTests.cs`：

```csharp
using System.Net;
using System.Net.Http.Json;
using Azure;
using AzureStorageBackup.Api.Endpoints;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorageBackup.Api.Tests;

/// <summary>用假 IContainerService 抛出指定异常，验证端点的错误映射，不需要 Azurite。</summary>
file sealed class ThrowingContainerService(Exception toThrow) : IContainerService
{
    public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(Account a, CancellationToken ct = default) =>
        throw toThrow;
    public Task CreateContainerAsync(Account a, string name, CancellationToken ct = default) =>
        throw toThrow;
    public Task DeleteContainerAsync(Account a, string name, CancellationToken ct = default) =>
        throw toThrow;
}

public class ContainerEndpointErrorTests(TestWebAppFactory factory) : IClassFixture<TestWebAppFactory>
{
    private sealed record ErrorBody(string error);

    private HttpClient ClientThrowing(Exception ex) =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.AddSingleton<IContainerService>(new ThrowingContainerService(ex));
        })).CreateClient();

    // 复用 ContainerEndpointsTests 里已验证过的请求形状。建账户不连云，
    // 所以这里不需要 Azurite——这正是本组测试与那组的区别。
    private static async Task<int> CreateAccountAsync(HttpClient client)
    {
        var res = await client.PostAsJsonAsync("/api/accounts", new AccountRequest(
            Name: "err-" + Guid.NewGuid().ToString("N")[..8],
            Description: null,
            BlobEndpoint: "http://127.0.0.1:10000/devstoreaccount1",
            Region: AzureRegion.Global,
            AccountKey: "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",
            UseProxy: false,
            ProxyMode: ProxyMode.Independent,
            ProxyHost: null,
            ProxyPort: null,
            ProxyUsername: null,
            ProxyPassword: null));
        res.EnsureSuccessStatusCode();
        var acct = await res.Content.ReadFromJsonAsync<AccountResponse>();
        return acct!.Id;
    }

    [Fact]
    public async Task Invalid_Name_Returns_400_Without_Calling_Azure()
    {
        // 服务一被调用就抛，所以能拿到 400 就证明校验发生在连云之前。
        var client = ClientThrowing(new InvalidOperationException("must not be called"));
        var id = await CreateAccountAsync(client);

        var res = await client.PostAsJsonAsync($"/api/accounts/{id}/containers", new { name = "Bad_Name" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Contains("lowercase letters, digits, and hyphens", body!.error);
    }

    [Fact]
    public async Task Azure_4xx_Is_Passed_Through_With_A_Readable_Message()
    {
        var client = ClientThrowing(new RequestFailedException(403, "This request is not authorized.", "AuthorizationFailure", null));
        var id = await CreateAccountAsync(client);

        var res = await client.PostAsJsonAsync($"/api/accounts/{id}/containers", new { name = "valid-name" });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Contains("AuthorizationFailure", body!.error);
    }

    [Fact]
    public async Task Unreachable_Storage_Account_Becomes_502()
    {
        // Status 0 是 SDK 表示「请求根本没发出去/没拿到响应」的方式。
        var client = ClientThrowing(new RequestFailedException(0, "No such host is known."));
        var id = await CreateAccountAsync(client);

        var res = await client.PostAsJsonAsync($"/api/accounts/{id}/containers", new { name = "valid-name" });

        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Contains("could not be reached", body!.error);
    }

    [Fact]
    public async Task List_Also_Maps_Azure_Failures()
    {
        var client = ClientThrowing(new RequestFailedException(0, "No such host is known."));
        var id = await CreateAccountAsync(client);

        var res = await client.GetAsync($"/api/accounts/{id}/containers");

        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `cd backend && dotnet test --filter FullyQualifiedName~ContainerEndpointErrorTests`
Expected: FAIL — 非法名那条拿到 500（异常冒泡）而非 400；4xx 那条拿到 500 而非 403；502 两条同样拿到 500

- [ ] **Step 3: 实现映射**

把 `backend/src/AzureStorageBackup.Api/Endpoints/ContainerEndpoints.cs` 整体替换为：

```csharp
using Azure;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

public record CreateContainerRequest(string Name);

/// <summary>
/// 账户下的 container 管理端点（PRD 1.2）。
/// 注：Azure Blob 不支持 container 重命名，故只有列举/创建/删除。
/// </summary>
public static class ContainerEndpoints
{
    /// <summary>
    /// 把 Azure 的失败翻译成客户端能用的响应。
    ///
    /// 逐端点捕获而非注册全局 handler：全局 handler 会一并接管本轮范围之外的所有未处理
    /// 异常，改变既有失败语义（见 KeyringGuard.cs 的同类说明）。
    /// </summary>
    private static IResult MapAzureFailure(RequestFailedException ex)
    {
        // 4xx 是调用方能修的（名字非法、无权限、已被他人占用），原样透传状态码。
        if (ex.Status is >= 400 and < 500)
            return Results.Json(
                new { error = string.IsNullOrEmpty(ex.ErrorCode) ? ex.Message : $"{ex.ErrorCode}: {ex.Message}" },
                statusCode: ex.Status);

        // Status 0 表示请求没能拿到响应（DNS/代理/网络）。这和 5xx 一样是上游的问题，
        // 不是本服务的问题——用 502 说清楚责任在哪一侧。
        return Results.Json(
            new { error = "The storage account could not be reached. Check the endpoint, proxy, and network." },
            statusCode: StatusCodes.Status502BadGateway);
    }

    public static IEndpointRouteBuilder MapContainerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/accounts/{accountId:int}/containers").WithTags("Containers");

        // 列/建/删 container 都要连云（设计 §3.1 明列「列容器」为需要凭据的动作），
        // 密钥环丢失时必须在入口 409，而不是让 SecretReader 在深处抛异常。
        group.MapGet("/", async (
            int accountId, IAccountService accounts, IContainerService containers, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            var account = await accounts.GetAsync(accountId, ct);
            if (account is null)
                return Results.NotFound();

            try
            {
                var list = await containers.ListContainersAsync(account, ct);
                return Results.Ok(list);
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

            // 连云之前先判：Azure 对非法名只回一句「contains invalid characters」，
            // 不说是哪个字符也不说规则，照搬给用户等于没说。
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
            IAccountService accounts, IContainerService containers, IKeyringHealth keyring, CancellationToken ct) =>
        {
            if (KeyringGuard.Blocked(keyring) is { } blocked) return blocked;

            var account = await accounts.GetAsync(accountId, ct);
            if (account is null)
                return Results.NotFound();

            try
            {
                await containers.DeleteContainerAsync(account, name, ct);
            }
            catch (RequestFailedException ex)
            {
                return MapAzureFailure(ex);
            }

            return Results.NoContent();
        });

        return app;
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `cd backend && dotnet test --filter FullyQualifiedName~ContainerEndpoint`
Expected: PASS。`ContainerEndpointErrorTests` 4 条全绿，`ContainerEndpointsTests` 原有用例不回归（Azurite 未运行时会 Skip，属正常）

- [ ] **Step 5: 跑全量后端测试**

Run: `cd backend && dotnet test`
Expected: PASS，无新增失败

- [ ] **Step 6: 提交**

```bash
git add backend/src/AzureStorageBackup.Api/Endpoints/ContainerEndpoints.cs \
        backend/tests/AzureStorageBackup.Api.Tests/ContainerEndpointErrorTests.cs
git commit -m "fix: turn Azure container failures into usable responses instead of a bare 500"
```

---

### Task 3: 前端错误解析与 container 名前置校验

**Files:**
- Modify: `frontend/src/api/client.ts`
- Modify: `frontend/src/api/containers.ts`
- Modify: `frontend/src/pages/ContainersPage.tsx:23-32`（`create`）与 JSX 中的新建表单区

**Interfaces:**
- Consumes: Task 2 的 `{ error }` 响应体
- Produces: `ApiError` 新增只读属性 `code?: string`；`frontend/src/api/containers.ts` 导出 `validateContainerName(name: string): string | null`

- [ ] **Step 1: 让 client.ts 读懂后端的错误形状**

把 `frontend/src/api/client.ts:12-40` 替换为：

```typescript
export class ApiError extends Error {
  status: number
  /** 后端在部分场景附带的机器可读码，例如 keyring_lost。 */
  code?: string

  constructor(status: number, message: string, code?: string) {
    super(message)
    this.status = status
    this.code = code
    this.name = 'ApiError'
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    headers: { 'Content-Type': 'application/json' },
    // fetch 默认 same-origin，会话 cookie 在跨域部署（SPA 单独托管）下根本不会被带上，
    // 后端为此开的 AllowCredentials() 就白开了。include 是 same-origin 的超集，同源部署不受影响。
    credentials: 'include',
    ...init,
  })

  if (!res.ok) {
    if (res.status === 401) onUnauthorized?.()
    const text = await res.text().catch(() => '')

    // 后端统一用 { error, code? } 报错（见 AccountEndpoints.cs、KeyringGuard.cs）。
    // 不解析的话，用户看到的是整段 JSON 原文，或者——响应体为空时——回落成
    // "Internal Server Error" 这种毫无信息量的字样。
    let message = text || res.statusText
    let code: string | undefined
    try {
      const body = JSON.parse(text) as { error?: unknown; code?: unknown }
      if (typeof body.error === 'string' && body.error) message = body.error
      if (typeof body.code === 'string') code = body.code
    } catch {
      // 非 JSON（如反代返回的 HTML 错误页）：保留原文。
    }

    throw new ApiError(res.status, message, code)
  }

  // 204 无内容
  if (res.status === 204) return undefined as T
  return (await res.json()) as T
}
```

- [ ] **Step 2: 在 containers.ts 加入与后端等价的校验规则**

在 `frontend/src/api/containers.ts` 末尾追加，并把 `remove` 改为编码路径段：

```typescript
/**
 * Azure container 命名规则。与后端 Services/ContainerName.cs 保持等价——
 * 后端是权威，这份存在只是为了在敲键时就给出反馈，而不是等一趟网络往返。
 * 改动其中一处务必同步另一处。
 */
export const containerNameRule =
  '3–63 characters; lowercase letters, digits, and hyphens only; must begin and end with a letter or digit; no consecutive hyphens.'

export function validateContainerName(name: string): string | null {
  if (name.length < 3 || name.length > 63)
    return 'Container name must be between 3 and 63 characters long.'
  if (!/^[a-z0-9-]+$/.test(name))
    return 'Container name may only contain lowercase letters, digits, and hyphens.'
  if (name.startsWith('-') || name.endsWith('-'))
    return 'Container name must begin and end with a letter or a digit.'
  if (name.includes('--'))
    return 'Container name may not contain consecutive hyphens.'
  return null
}
```

同时把 `containersApi.remove` 改为：

```typescript
  remove: (accountId: number, name: string) =>
    api.del(`/accounts/${accountId}/containers/${encodeURIComponent(name)}`),
```

- [ ] **Step 3: 在 ContainersPage 用上校验**

`frontend/src/pages/ContainersPage.tsx`——把第 2 行的 import 改为包含新导出：

```typescript
import {
  containersApi,
  backupPresenceLabels,
  infoFileName,
  validateContainerName,
  containerNameRule,
  type ContainerInfo,
} from '../api/containers'
```

把 `create`（原 23-32 行）替换为：

```typescript
  const trimmedName = newName.trim()
  const nameError = trimmedName ? validateContainerName(trimmedName) : null

  const create = async () => {
    if (!trimmedName || nameError) return
    try {
      await containersApi.create(account.id, trimmedName)
      setNewName('')
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }
```

把新建表单那一段（原 59-68 行）替换为：

```tsx
      <div style={{ margin: '1rem 0' }}>
        <input
          placeholder="New container name"
          value={newName}
          onChange={(e) => setNewName(e.target.value)}
        />{' '}
        <button type="button" onClick={create} disabled={!trimmedName || !!nameError}>
          Create Container
        </button>
        <div>{nameError ?? containerNameRule}</div>
      </div>
```

同时把该文件里其余两处 `setError(String(e))`（`load` 与 `remove` 内）改为 `setError(e instanceof Error ? e.message : String(e))`——否则 `String(e)` 会把消息前面缀上 `ApiError: `。

- [ ] **Step 4: 构建与 lint**

Run: `cd frontend && npm run build && npm run lint`
Expected: 构建成功，无 TypeScript 报错；oxlint 无 error

- [ ] **Step 5: 手工验证**

启动后端与 `npm run dev`，进入某账户的 Containers 页：
- 输入 `Bad_Name` → 按钮禁用，下方显示 "may only contain lowercase letters, digits, and hyphens"
- 输入合法名并创建 → 成功
- 停掉后端再点创建 → 显示可读的失败原因，不再是 `ApiError: Internal Server error`

- [ ] **Step 6: 提交**

```bash
git add frontend/src/api/client.ts frontend/src/api/containers.ts frontend/src/pages/ContainersPage.tsx
git commit -m "fix: surface the server's error text and validate container names before submit"
```

---

### Task 4: 清理模板残留并建立 design token

**Files:**
- Modify: `frontend/src/index.css`（整体重写）
- Delete: `frontend/src/App.css`、`frontend/src/assets/hero.png`、`frontend/src/assets/react.svg`、`frontend/src/assets/vite.svg`、`frontend/public/icons.svg`
- Modify: `frontend/public/favicon.svg`

**Interfaces:**
- Consumes: 无
- Produces: 全部 CSS 变量（见下方 `:root`）、元素默认样式。后续任务只使用这些变量，不再出现字面色值。

**注意：`App.css` 没有被任何文件 import（只有 `main.tsx` 引 `index.css`），删除它不影响任何东西。有害的 `#root { width: 1126px }` 在 `index.css` 里。**

- [ ] **Step 1: 删除未被引用的模板资源**

```bash
cd frontend
git rm src/App.css src/assets/hero.png src/assets/react.svg src/assets/vite.svg public/icons.svg
```

- [ ] **Step 2: 确认没有残留引用**

Run: `cd frontend && grep -rn "App.css\|hero.png\|react.svg\|vite.svg\|icons.svg" src/ index.html`
Expected: 无输出

- [ ] **Step 3: 重写 index.css 的 token 与基础层**

把 `frontend/src/index.css` **整体替换**为：

```css
/* ── Design tokens ──────────────────────────────────────────────────────────
   浅色为默认，深色只覆盖颜色。所有组件一律引用变量，不写字面色值。 */
:root {
  --bg: #ffffff;
  --bg-subtle: #f7f7f8;
  --bg-raised: #ffffff;
  --bg-hover: #f1f1f4;

  --border: #e4e4e7;
  --border-strong: #cfcfd6;

  --text: #18181b;
  --text-muted: #6b6b76;
  --text-faint: #9a9aa4;

  --accent: #2563eb;
  --accent-hover: #1d4ed8;
  --accent-fg: #ffffff;
  --accent-subtle: rgba(37, 99, 235, 0.1);

  --ok: #15803d;
  --ok-bg: rgba(21, 128, 61, 0.1);
  --ok-border: rgba(21, 128, 61, 0.35);
  --warn: #b45309;
  --warn-bg: rgba(180, 83, 9, 0.1);
  --warn-border: rgba(180, 83, 9, 0.35);
  --danger: #b91c1c;
  --danger-bg: rgba(185, 28, 28, 0.1);
  --danger-border: rgba(185, 28, 28, 0.35);

  --font-sans: ui-sans-serif, system-ui, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
  --font-mono: ui-monospace, 'SFMono-Regular', Consolas, 'Liberation Mono', monospace;

  --sp-1: 4px;
  --sp-2: 8px;
  --sp-3: 12px;
  --sp-4: 16px;
  --sp-5: 24px;
  --sp-6: 32px;

  --r-sm: 4px;
  --r-md: 6px;
  --r-lg: 8px;

  /* 阴影只给浮层。平面区域用描边分隔——这是紧凑控制台与卡片式 SaaS 的分界。 */
  --shadow-overlay: 0 16px 48px rgba(0, 0, 0, 0.18), 0 2px 8px rgba(0, 0, 0, 0.08);

  --control-h: 32px;
  --sidebar-w: 220px;
  --content-max: 1280px;

  color-scheme: light dark;
  font: 14px/1.5 var(--font-sans);
  color: var(--text);
  background: var(--bg);
  -webkit-font-smoothing: antialiased;
  -moz-osx-font-smoothing: grayscale;
}

@media (prefers-color-scheme: dark) {
  :root {
    --bg: #0f1014;
    --bg-subtle: #16171d;
    --bg-raised: #1a1b22;
    --bg-hover: #21232c;

    --border: #2a2c35;
    --border-strong: #3a3d48;

    --text: #e8e8ec;
    --text-muted: #9a9ba6;
    --text-faint: #6e7080;

    --accent: #60a5fa;
    --accent-hover: #93c5fd;
    --accent-fg: #0f1014;
    --accent-subtle: rgba(96, 165, 250, 0.15);

    --ok: #4ade80;
    --ok-bg: rgba(74, 222, 128, 0.12);
    --ok-border: rgba(74, 222, 128, 0.35);
    --warn: #fbbf24;
    --warn-bg: rgba(251, 191, 36, 0.12);
    --warn-border: rgba(251, 191, 36, 0.35);
    --danger: #f87171;
    --danger-bg: rgba(248, 113, 113, 0.12);
    --danger-border: rgba(248, 113, 113, 0.35);

    --shadow-overlay: 0 16px 48px rgba(0, 0, 0, 0.5), 0 2px 8px rgba(0, 0, 0, 0.3);
  }
}

/* ── Base ─────────────────────────────────────────────────────────────────── */
*,
*::before,
*::after {
  box-sizing: border-box;
}

body {
  margin: 0;
  background: var(--bg);
  color: var(--text);
}

h1,
h2,
h3 {
  color: var(--text);
  font-weight: 600;
  margin: 0;
}
h1 {
  font-size: 20px;
  letter-spacing: -0.2px;
}
h2 {
  font-size: 16px;
  margin: var(--sp-5) 0 var(--sp-2);
}
h3 {
  font-size: 15px;
}

p {
  margin: 0 0 var(--sp-3);
}

a {
  color: var(--accent);
}

code,
.mono {
  font-family: var(--font-mono);
  font-size: 0.9em;
}

code {
  background: var(--bg-subtle);
  border: 1px solid var(--border);
  border-radius: var(--r-sm);
  padding: 1px 5px;
}

hr {
  border: none;
  border-top: 1px solid var(--border);
  margin: var(--sp-5) 0;
}

/* 焦点：内圈用背景色隔开，外圈用强调色，深浅底上都看得见。 */
:focus-visible {
  outline: none;
  box-shadow: 0 0 0 2px var(--bg), 0 0 0 4px var(--accent);
  border-radius: var(--r-sm);
}

/* ── Controls ─────────────────────────────────────────────────────────────── */
button {
  font: inherit;
  height: var(--control-h);
  padding: 0 var(--sp-3);
  border: 1px solid var(--border-strong);
  border-radius: var(--r-md);
  background: var(--bg-raised);
  color: var(--text);
  cursor: pointer;
  white-space: nowrap;
  transition: background-color 0.12s, border-color 0.12s;
}
button:hover:not(:disabled) {
  background: var(--bg-hover);
}
button:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

input,
select,
textarea {
  font: inherit;
  color: var(--text);
  background: var(--bg-raised);
  border: 1px solid var(--border-strong);
  border-radius: var(--r-md);
  padding: 0 var(--sp-2);
  height: var(--control-h);
  max-width: 100%;
}
textarea {
  height: auto;
  padding: var(--sp-2);
  font-family: var(--font-mono);
  resize: vertical;
}
input[type='checkbox'] {
  height: auto;
  width: auto;
  accent-color: var(--accent);
  margin: 0;
}
input:disabled,
select:disabled,
textarea:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
input::placeholder,
textarea::placeholder {
  color: var(--text-faint);
}

fieldset {
  border: 1px solid var(--border);
  border-radius: var(--r-md);
  padding: var(--sp-3) var(--sp-4);
}
legend {
  color: var(--text-muted);
  font-size: 12px;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  padding: 0 var(--sp-1);
}

/* ── Tables ───────────────────────────────────────────────────────────────── */
table {
  width: 100%;
  border-collapse: collapse;
}
thead th {
  text-align: left;
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--text-muted);
  background: var(--bg-subtle);
  border-bottom: 1px solid var(--border);
  padding: var(--sp-2) var(--sp-3);
  white-space: nowrap;
}
tbody td {
  border-bottom: 1px solid var(--border);
  padding: var(--sp-2) var(--sp-3);
  vertical-align: top;
}
tbody tr:hover {
  background: var(--bg-hover);
}
```

- [ ] **Step 4: 替换 favicon**

把 `frontend/public/favicon.svg` 内容替换为（当前是 Vite 的紫色闪电，由 `index.html:5` 引用）：

```svg
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" width="32" height="32">
  <rect width="32" height="32" rx="7" fill="#2563eb"/>
  <path d="M16 7c-4.4 0-8 1.6-8 3.6v10.8C8 23.4 11.6 25 16 25s8-1.6 8-3.6V10.6C24 8.6 20.4 7 16 7Zm0 2.4c3.6 0 6 1.2 6 1.6s-2.4 1.6-6 1.6-6-1.2-6-1.6 2.4-1.6 6-1.6Zm6 12c0 .4-2.4 1.6-6 1.6s-6-1.2-6-1.6v-2.5c1.5.8 3.7 1.3 6 1.3s4.5-.5 6-1.3Zm0-5c0 .4-2.4 1.6-6 1.6s-6-1.2-6-1.6v-2.5c1.5.8 3.7 1.3 6 1.3s4.5-.5 6-1.3Z" fill="#fff"/>
</svg>
```

- [ ] **Step 5: 构建并目视确认**

Run: `cd frontend && npm run build && npm run lint`
Expected: 通过

跑 `npm run dev` 打开界面：此时布局还是旧的（外壳未改），但控件、表格、字号已经统一，页面不再被强制居中和限宽 1126px。

- [ ] **Step 6: 提交**

```bash
git add -A frontend/src/index.css frontend/public/favicon.svg
git commit -m "style: replace the Vite scaffold leftovers with a real token layer"
```

---

### Task 5: 应用外壳

**Files:**
- Modify: `frontend/src/App.tsx:47-87`
- Modify: `frontend/src/components/LoginPage.tsx:31-53`
- Modify: `frontend/src/index.css`（追加 layout 段）

**Interfaces:**
- Consumes: Task 4 的全部 token
- Produces: class `app-shell` / `sidebar` / `sidebar-brand` / `sidebar-nav` / `nav-item` / `nav-item-active` / `sidebar-footer` / `app-main` / `page-header` / `auth-page` / `auth-card`

- [ ] **Step 1: 追加 layout 样式**

在 `frontend/src/index.css` 末尾追加：

```css
/* ── Shell ────────────────────────────────────────────────────────────────── */
.app-shell {
  display: grid;
  grid-template-columns: var(--sidebar-w) minmax(0, 1fr);
  min-height: 100svh;
}

.sidebar {
  display: flex;
  flex-direction: column;
  background: var(--bg-subtle);
  border-right: 1px solid var(--border);
  padding: var(--sp-4) 0;
  position: sticky;
  top: 0;
  height: 100svh;
}

.sidebar-brand {
  font-size: 13px;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: var(--text-muted);
  padding: 0 var(--sp-4) var(--sp-4);
}

.sidebar-nav {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 0 var(--sp-2);
  overflow-y: auto;
}

.sidebar-footer {
  margin-top: auto;
  padding: var(--sp-4) var(--sp-3) 0;
}

/* 导航项是 <button>，所以要把按钮的默认外观整个抹掉再重画。 */
.nav-item {
  height: auto;
  padding: var(--sp-2) var(--sp-3);
  border: none;
  border-left: 2px solid transparent;
  border-radius: var(--r-md);
  background: transparent;
  color: var(--text-muted);
  text-align: left;
  font-weight: 500;
}
.nav-item:hover:not(:disabled) {
  background: var(--bg-hover);
  color: var(--text);
}
.nav-item-active,
.nav-item-active:hover:not(:disabled) {
  background: var(--accent-subtle);
  border-left-color: var(--accent);
  border-radius: 0 var(--r-md) var(--r-md) 0;
  color: var(--text);
  font-weight: 600;
}

.app-main {
  min-width: 0;
  max-width: var(--content-max);
  padding: var(--sp-5);
}

.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--sp-3);
  margin-bottom: var(--sp-4);
}

/* 窄屏：侧栏塌成顶部横向滚动的 tab 条。React 结构不变，只换 CSS。 */
@media (max-width: 900px) {
  .app-shell {
    grid-template-columns: minmax(0, 1fr);
  }
  .sidebar {
    position: static;
    height: auto;
    flex-direction: row;
    align-items: center;
    gap: var(--sp-2);
    border-right: none;
    border-bottom: 1px solid var(--border);
    padding: var(--sp-2);
    overflow-x: auto;
  }
  .sidebar-brand {
    padding: 0 var(--sp-2);
    white-space: nowrap;
  }
  .sidebar-nav {
    flex-direction: row;
    padding: 0;
  }
  .nav-item {
    border-left: none;
    border-bottom: 2px solid transparent;
    white-space: nowrap;
  }
  .nav-item-active {
    border-radius: var(--r-md);
    border-left: none;
    border-bottom-color: var(--accent);
  }
  .sidebar-footer {
    margin-top: 0;
    margin-left: auto;
    padding: 0 var(--sp-2);
  }
  .app-main {
    padding: var(--sp-4);
  }
}

/* ── Auth ─────────────────────────────────────────────────────────────────── */
.auth-page {
  min-height: 100svh;
  display: grid;
  place-items: center;
  padding: var(--sp-4);
}

.auth-card {
  width: 320px;
  max-width: 100%;
  background: var(--bg-raised);
  border: 1px solid var(--border);
  border-radius: var(--r-lg);
  padding: var(--sp-5);
}
.auth-card h1 {
  margin-bottom: var(--sp-4);
}
.auth-card input,
.auth-card button {
  width: 100%;
}
.auth-card button {
  margin-top: var(--sp-3);
  background: var(--accent);
  border-color: var(--accent);
  color: var(--accent-fg);
  font-weight: 600;
}
.auth-card button:hover:not(:disabled) {
  background: var(--accent-hover);
  border-color: var(--accent-hover);
}
```

- [ ] **Step 2: 改 App.tsx 的渲染部分**

把 `frontend/src/App.tsx:47-87`（`return (` 到 `)` 之间）替换为：

```tsx
  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="sidebar-brand">Azure Storage Backup</div>
        <nav className="sidebar-nav">
          {tabs.map((t) => (
            <button
              key={t.key}
              type="button"
              onClick={() => setTab(t.key)}
              className={tab === t.key ? 'nav-item nav-item-active' : 'nav-item'}
              aria-current={tab === t.key ? 'page' : undefined}
            >
              {t.label}
            </button>
          ))}
        </nav>
        {auth.required && (
          <div className="sidebar-footer">
            <button
              type="button"
              // 无论服务端登出成功与否都清掉本地状态：失败却停在主界面，
              // 会让人以为自己已经退出了——在共用机器上这就是个安全问题。
              onClick={() => {
                const signedOut = () => setAuth({ required: true, authenticated: false })
                authApi.logout().then(signedOut, signedOut)
              }}
            >
              Log out
            </button>
          </div>
        )}
      </aside>

      <main className="app-main">
        <KeyringBanner onGoToAccounts={() => setTab('accounts')} />

        {tab === 'accounts' && <AccountsPage />}
        {tab === 'backups' && <BackupConfigsPage />}
        {tab === 'discovered' && <BackupsPage />}
        {tab === 'groups' && <GroupsPage />}
        {tab === 'tasks' && <TasksPage />}
        {tab === 'notifications' && <NotificationsPage />}
        {tab === 'logs' && <LogsPage />}
        {tab === 'settings' && <SettingsPage />}
      </main>
    </div>
  )
```

- [ ] **Step 3: 改 LoginPage 的渲染部分**

把 `frontend/src/components/LoginPage.tsx:31-53`（`return (` 到 `)` 之间）替换为：

```tsx
  return (
    <div className="auth-page">
      <div className="auth-card">
        <h1>Azure Storage Backup</h1>
        <form onSubmit={submit}>
          <input
            type="password"
            name="password"
            // 让密码管理器认得出这是登录框并愿意保存/填充——一串又长又随机的密码
            // 只有在能被自动填充时才用得下去。
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            placeholder="Password"
            autoFocus
          />
          <button type="submit" disabled={busy || !password}>
            {busy ? 'Signing in…' : 'Sign in'}
          </button>
        </form>
        {error && <p className="text-danger" style={{ marginTop: '0.75rem' }}>{error}</p>}
      </div>
    </div>
  )
```

（`text-danger` 在 Task 6 定义；此处先写上，Task 6 完成前它只是无样式的类名，不影响功能。）

- [ ] **Step 4: 构建与 lint**

Run: `cd frontend && npm run build && npm run lint`
Expected: 通过

- [ ] **Step 5: 手工确认三态**

`npm run dev` 后确认：宽屏见左侧栏且选中项有强调色竖条；把窗口拉窄到 900px 以下，侧栏变成顶部可横向滚动的 tab 条；系统切到深色主题后侧栏、正文、控件均为深色且文字可读。

- [ ] **Step 6: 提交**

```bash
git add frontend/src/index.css frontend/src/App.tsx frontend/src/components/LoginPage.tsx
git commit -m "feat: replace the tab strip with a sidebar shell"
```

---

### Task 6: 组件类与 Field 去重

**Files:**
- Modify: `frontend/src/index.css`（追加 components 段）
- Modify: `frontend/src/components/modal.tsx`
- Modify: `frontend/src/components/modalStyles.ts`
- Modify: `frontend/src/components/KeyringBanner.tsx:17-43`

**Interfaces:**
- Consumes: Task 4 / Task 5 的 token 与 layout
- Produces:
  - class：`btn-primary`、`btn-danger`、`btn-ghost`、`w-sm`、`w-md`、`w-lg`、`w-full`、`panel`、`field`、`field-label`、`empty-state`、`alert`、`alert-warn`、`alert-error`、`alert-ok`、`badge`、`badge-ok`、`badge-warn`、`badge-danger`、`text-muted`、`text-danger`、`text-ok`、`row`、`stack`、`toolbar`
  - `components/modal.tsx` 导出唯一的 `Field({ label, children }: { label: string; children: ReactNode })`
  - `components/modalStyles.ts` 的 `overlayStyle` / `panelStyle` 改为不含字面色值

- [ ] **Step 1: 追加组件样式**

在 `frontend/src/index.css` 末尾追加：

```css
/* ── Buttons ──────────────────────────────────────────────────────────────── */
.btn-primary {
  background: var(--accent);
  border-color: var(--accent);
  color: var(--accent-fg);
  font-weight: 600;
}
.btn-primary:hover:not(:disabled) {
  background: var(--accent-hover);
  border-color: var(--accent-hover);
}

.btn-danger {
  color: var(--danger);
  border-color: var(--danger-border);
}
.btn-danger:hover:not(:disabled) {
  background: var(--danger-bg);
}

/* 表格行内操作：一行里挤三四个按钮时，边框会把行切碎。 */
.btn-ghost {
  border-color: transparent;
  background: transparent;
  color: var(--text-muted);
  padding: 0 var(--sp-2);
}
.btn-ghost:hover:not(:disabled) {
  background: var(--bg-hover);
  color: var(--text);
}

/* ── Control widths ───────────────────────────────────────────────────────── */
.w-sm { width: 160px; }
.w-md { width: 280px; }
.w-lg { width: 480px; }
.w-full { width: 100%; }

/* ── Blocks ───────────────────────────────────────────────────────────────── */
.panel {
  background: var(--bg-raised);
  border: 1px solid var(--border);
  border-radius: var(--r-lg);
  padding: var(--sp-4);
  margin-top: var(--sp-5);
}
.panel > h2:first-child,
.panel > h3:first-child {
  margin-top: 0;
}

.field {
  display: grid;
  grid-template-columns: 200px minmax(0, 1fr);
  gap: var(--sp-3);
  align-items: start;
  margin: var(--sp-2) 0;
}
.field-label {
  color: var(--text-muted);
  padding-top: 6px;
}

@media (max-width: 900px) {
  .field {
    grid-template-columns: minmax(0, 1fr);
    gap: var(--sp-1);
  }
  .field-label {
    padding-top: 0;
  }
}

.empty-state {
  color: var(--text-muted);
  padding: var(--sp-5) var(--sp-3);
  text-align: center;
}

.toolbar {
  display: flex;
  flex-wrap: wrap;
  gap: var(--sp-3);
  align-items: center;
  margin-bottom: var(--sp-3);
}

.row {
  display: flex;
  gap: var(--sp-2);
  align-items: center;
}

.stack {
  display: flex;
  flex-direction: column;
  gap: var(--sp-2);
}

/* ── Alerts & badges ──────────────────────────────────────────────────────── */
.alert {
  border: 1px solid var(--border);
  background: var(--bg-subtle);
  color: var(--text);
  border-radius: var(--r-md);
  padding: var(--sp-3) var(--sp-4);
  margin-bottom: var(--sp-4);
}
.alert-warn { border-color: var(--warn-border); background: var(--warn-bg); }
.alert-error { border-color: var(--danger-border); background: var(--danger-bg); }
.alert-ok { border-color: var(--ok-border); background: var(--ok-bg); }

.badge {
  display: inline-flex;
  align-items: center;
  border: 1px solid var(--border);
  background: var(--bg-subtle);
  color: var(--text-muted);
  border-radius: 999px;
  padding: 1px var(--sp-2);
  font-size: 12px;
  white-space: nowrap;
}
.badge-ok { color: var(--ok); border-color: var(--ok-border); background: var(--ok-bg); }
.badge-warn { color: var(--warn); border-color: var(--warn-border); background: var(--warn-bg); }
.badge-danger { color: var(--danger); border-color: var(--danger-border); background: var(--danger-bg); }

/* ── Text ─────────────────────────────────────────────────────────────────── */
.text-muted { color: var(--text-muted); }
.text-faint { color: var(--text-faint); font-size: 12px; }
.text-danger { color: var(--danger); }
.text-ok { color: var(--ok); }
.text-warn { color: var(--warn); }

/* ── Modal ────────────────────────────────────────────────────────────────── */
.modal-overlay {
  position: fixed;
  inset: 0;
  z-index: 50;
  display: flex;
  align-items: flex-start;
  justify-content: center;
  padding: 4vh var(--sp-4);
  background: rgba(0, 0, 0, 0.45);
  backdrop-filter: blur(2px);
  overflow-y: auto;
}
.modal-panel {
  background: var(--bg-raised);
  border: 1px solid var(--border);
  border-radius: var(--r-lg);
  box-shadow: var(--shadow-overlay);
  padding: var(--sp-5);
  min-width: 620px;
  max-width: 90vw;
  max-height: 88vh;
  overflow: auto;
}
.modal-panel > h3:first-child {
  margin-bottom: var(--sp-3);
}

@media (max-width: 900px) {
  .modal-panel {
    min-width: 0;
    width: 100%;
  }
}
```

- [ ] **Step 2: 统一 Field**

把 `frontend/src/components/modal.tsx` 整体替换为：

```tsx
import type { ReactNode } from 'react'

// 表单字段行，供各页面与 Modal/Dialog 复用。
// 曾经有四份各不相同的副本（label 宽 130/140/200、对齐方式不一），是界面参差不齐的主因之一。
export function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="field">
      <span className="field-label">{label}</span>
      <span>{children}</span>
    </label>
  )
}
```

- [ ] **Step 3: 删掉三份重复的 Field**

对以下三个文件：删除文件末尾的私有 `function Field(...)`，改为从 `../components/modal` 导入。

- `frontend/src/pages/AccountsPage.tsx:417-424` → 该文件已在第 12 行 import `modalStyles`；把第 12 行改为：
  ```typescript
  import { overlayStyle, panelStyle } from '../components/modalStyles'
  import { Field } from '../components/modal'
  ```
  并删掉第 1 行 import 中不再使用的 `type ReactNode`。
- `frontend/src/pages/SettingsPage.tsx:148-155` → 顶部加 `import { Field } from '../components/modal'`，删掉第 1 行的 `type ReactNode`。
- `frontend/src/pages/NotificationsPage.tsx:147-154` → 同上。

- [ ] **Step 4: modalStyles 去掉字面色值**

把 `frontend/src/components/modalStyles.ts` 整体替换为：

```typescript
// 弹窗共用样式。真正的样式在 index.css 的 .modal-overlay / .modal-panel，
// 这里只保留 className 常量，避免各处再手抄一次字符串。
// 原先这里硬编码 background:'#fff'，深色模式下白底配深色 token 是坏的。
export const overlayStyle = 'modal-overlay'
export const panelStyle = 'modal-panel'
```

随后把使用处从 `style={overlayStyle}` 改为 `className={overlayStyle}`，`style={panelStyle}` 改为 `className={panelStyle}`。使用处共三个文件，用以下命令定位：

Run: `cd frontend && grep -rn "overlayStyle\|panelStyle" src/`
Expected: `components/PathBrowser.tsx:38,39`、`components/RestoreDialog.tsx`、`pages/AccountsPage.tsx:372,373` 各处

- [ ] **Step 5: KeyringBanner 用 alert 类**

把 `frontend/src/components/KeyringBanner.tsx:17-43` 的 `return` 替换为：

```tsx
  return (
    <div role="alert" className="alert alert-warn">
      <strong>Data protection keys were lost</strong> — {pending} credential
      {pending === 1 ? '' : 's'} need to be re-entered before backups can run.
      {status.accountsPending > 0 && (
        <>
          {' '}
          Start with{' '}
          <button type="button" className="btn-ghost" onClick={onGoToAccounts}>
            Accounts
          </button>
          {' '}({status.accountsPending} pending), then re-enter backup passwords
          ({status.backupConfigsPending} pending).
        </>
      )}
    </div>
  )
```

- [ ] **Step 6: 构建与 lint**

Run: `cd frontend && npm run build && npm run lint`
Expected: 通过。若报 `'ReactNode' is declared but its value is never read`，说明 Step 3 漏删了某个文件的 import。

- [ ] **Step 7: 提交**

```bash
git add frontend/src/index.css frontend/src/components/
git add frontend/src/pages/AccountsPage.tsx frontend/src/pages/SettingsPage.tsx frontend/src/pages/NotificationsPage.tsx
git commit -m "refactor: collapse four Field copies into one and move modal styling into CSS"
```

---

### Task 7: 迁移 AccountsPage 与 ContainersPage

**Files:**
- Modify: `frontend/src/pages/AccountsPage.tsx`（17 处内联 style）
- Modify: `frontend/src/pages/ContainersPage.tsx`（8 处内联 style）

**Interfaces:**
- Consumes: Task 6 的全部 class
- Produces: 无新接口。本任务确立后续页面沿用的替换约定。

**替换约定（后续所有页面任务共用）：**

| 现有内联 style | 替换为 |
|---|---|
| `style={{ display:'flex', justifyContent:'space-between', alignItems:'center' }}`（页面标题行） | `className="page-header"` |
| `style={{ color:'crimson' }}` | `className="text-danger"` |
| `style={{ color:'#666' }}` / `{ color:'#888' }` | `className="text-muted"` |
| `style={{ fontSize:'0.8rem', color:'#888' }}` 等小字灰字 | `className="text-faint"` |
| `style={{ color:'green' }}` | `className="text-ok"` |
| `style={{ color:'#b45309' }}` / `{ color:'#a60' }` / `{ color:'#b06a00' }` | `className="text-warn"` |
| `style={{ width:'100%', borderCollapse:'collapse', marginTop:'1rem' }}` | 整个删掉（元素选择器已覆盖） |
| `style={{ textAlign:'left', borderBottom:'1px solid #ccc' }}`（`thead tr`） | 整个删掉 |
| `style={{ borderBottom:'1px solid #eee' }}`（`tbody tr`） | 整个删掉 |
| `style={{ padding:'1rem 0', color:'#666' }}`（空态 `td`） | `className="empty-state"` |
| `style={{ marginTop:'1.5rem', padding:'1rem', border:'1px solid #ccc' }}` | `className="panel"` |
| `style={{ textAlign:'right', whiteSpace:'nowrap' }}`（操作列 `td`） | `style={{ textAlign: 'right', whiteSpace: 'nowrap' }}` 保留——这是布局而非外观 |
| `style={{ marginTop:'1rem' }}`（按钮组） | `className="row"` + 需要间距时 `style={{ marginTop: '1rem' }}` 保留 |
| `style={{ fontFamily:'monospace', ... }}` | `className="mono"` |
| 表格行内的 `<button>` | 加 `className="btn-ghost"`；`Delete` 再加 `btn-danger` |
| 表单主提交按钮（Create / Save） | 加 `className="btn-primary"` |

- [ ] **Step 1: 迁移 AccountsPage**

按上表替换 `frontend/src/pages/AccountsPage.tsx` 中全部 17 处。另需：

- 第 165 行 `<h1>Accounts</h1>` 所在的 div → `className="page-header"`，其中 `New Account` 按钮加 `className="btn-primary"`。
- 第 237-243 行 Blob Endpoint 的 `<input>` 加 `className="w-lg mono"`。
- 第 257-264 行 Account Key 的 `<input>` 加 `className="w-lg mono"`。
- 第 288-302 行 Proxy Host 的 `<input>` 加 `className="w-md"`；Proxy Port 加 `className="w-sm"`。
- 第 303-316 行 Proxy Username / Proxy Password 各加 `className="w-md"`。
- 第 255 行 `<span style={{ color:'#888', fontSize:'0.8rem', marginLeft:'0.4rem' }}>` → `className="text-faint"` 并保留 `style={{ marginLeft: '0.4rem' }}`。
- 第 197 行 `Credential required` 的 span → `className="badge badge-warn"`，并去掉 `marginLeft` 改由外层 `.row` 处理。
- `ResetSecretsModal` 里第 381-396 行两个密码 `<input>` 加 `className="w-lg"`。

- [ ] **Step 2: 迁移 ContainersPage**

- 第 46-48 行 `← Back to accounts` 按钮加 `className="btn-ghost"`，并把它移到 `page-header` 之前保持独立一行。
- 第 50-55 行的 div → `className="page-header"`。
- 第 59-68 行（Task 3 已改过的新建区）改为：
  ```tsx
        <div className="toolbar">
          <input
            className="w-md"
            placeholder="New container name"
            value={newName}
            onChange={(e) => setNewName(e.target.value)}
          />
          <button
            type="button"
            className="btn-primary"
            onClick={create}
            disabled={!trimmedName || !!nameError}
          >
            Create Container
          </button>
          <span className={nameError ? 'text-danger' : 'text-faint'}>
            {nameError ?? containerNameRule}
          </span>
        </div>
  ```
- 表格头 `<tr>`、行 `<tr>`、表格本身的 style 全部删除。
- 第 88-93 行 `infoFileName` 的 span → `className="text-faint"`。
- 第 96-98 行 `Delete` 按钮加 `className="btn-ghost btn-danger"`。
- 空列表分支（第 73 行 `<p>`）→ `<p className="empty-state">`。

- [ ] **Step 3: 确认这两个文件已无字面色值**

Run: `cd frontend && grep -n "crimson\|#666\|#888\|#ccc\|#eee\|green'\|#b45309" src/pages/AccountsPage.tsx src/pages/ContainersPage.tsx`
Expected: 无输出

- [ ] **Step 4: 构建与 lint**

Run: `cd frontend && npm run build && npm run lint`
Expected: 通过

- [ ] **Step 5: 提交**

```bash
git add frontend/src/pages/AccountsPage.tsx frontend/src/pages/ContainersPage.tsx
git commit -m "style: move Accounts and Containers onto the shared classes"
```

---

### Task 8: 迁移 GroupsPage、BackupsPage、LogsPage

**Files:**
- Modify: `frontend/src/pages/GroupsPage.tsx`（15 处）
- Modify: `frontend/src/pages/BackupsPage.tsx`（6 处）
- Modify: `frontend/src/pages/LogsPage.tsx`（13 处）

**Interfaces:**
- Consumes: Task 6 的 class 与 Task 7 的替换约定
- Produces: 无

- [ ] **Step 1: 迁移 GroupsPage**

按 Task 7 的对照表替换。另需：

- 第 136 行表单容器 → `className="panel"`。
- 第 138-140 行 `Name` label → 改用 `<Field label="Name">`（从 `../components/modal` 导入），内层 `<input>` 加 `className="w-md"`。
- 第 152 行成员勾选区 → `className="stack"` 并保留 `style={{ maxHeight: 200, overflow: 'auto' }}`，边框改为 `border: '1px solid var(--border)'`，padding 用 `var(--sp-2)`。
- 第 163 行越界成员 label 的 `color:'#a60'` → `className="text-warn"`。
- 第 171 行 `Create`/`Save` 按钮加 `className="btn-primary"`。

- [ ] **Step 2: 迁移 BackupsPage**

- 第 26-31 行 → `className="page-header"`；`Refresh` 按钮保持默认次要样式。
- 第 32-34 行说明文字 → `className="text-muted"`。
- 表格三处 style 删除。
- 第 39、41 行的 `<p>` → `className="empty-state"`。

- [ ] **Step 3: 迁移 LogsPage**

- 第 63 行 `<h1>Logs</h1>` 外面补一层 `<div className="page-header">`。
- 第 66 行筛选条 → `className="toolbar"`，删除内联 style。
- 第 79 行 Source 输入框加 `className="w-md"`。
- 第 5 行的 `levelColor` 常量删除；第 120 行改为按等级给 badge：
  ```tsx
                <td>
                  <span className={
                    l.level === 2 ? 'badge badge-danger'
                    : l.level === 1 ? 'badge badge-warn'
                    : 'badge'
                  }>
                    {levelLabels[l.level]}
                  </span>
                </td>
  ```
- 第 117 行时间列 → `className="text-faint"` 并保留 `style={{ whiteSpace: 'nowrap' }}`。
- 第 123 行 Source 列 → `className="mono text-faint"`。
- 第 131-140 行 System 区：`<h2>` 去掉内联 margin（元素样式已给），两个 `<p>` 与 `<ul>` 分别用 `text-muted` 与 `mono text-faint`。
- 第 110 行空态 `td` → `className="empty-state"`。

- [ ] **Step 4: 确认无字面色值**

Run: `cd frontend && grep -n "crimson\|#666\|#888\|#ccc\|#eee\|#555\|#b8860b\|#a60" src/pages/GroupsPage.tsx src/pages/BackupsPage.tsx src/pages/LogsPage.tsx`
Expected: 无输出

- [ ] **Step 5: 构建与 lint**

Run: `cd frontend && npm run build && npm run lint`
Expected: 通过

- [ ] **Step 6: 提交**

```bash
git add frontend/src/pages/GroupsPage.tsx frontend/src/pages/BackupsPage.tsx frontend/src/pages/LogsPage.tsx
git commit -m "style: move Groups, Backups, and Logs onto the shared classes"
```

---

### Task 9: 迁移 TasksPage、NotificationsPage、SettingsPage、CronEditor

**Files:**
- Modify: `frontend/src/pages/TasksPage.tsx`（19 处）
- Modify: `frontend/src/pages/NotificationsPage.tsx`（10 处）
- Modify: `frontend/src/pages/SettingsPage.tsx`（9 处）
- Modify: `frontend/src/components/CronEditor.tsx`（6 处）

**Interfaces:**
- Consumes: Task 6 的 class、统一后的 `Field`
- Produces: 无

- [ ] **Step 1: 迁移 TasksPage**

- 第 142 行 → `className="page-header"`，`New Task` 加 `btn-primary`。
- 第 200 行表单容器 → `className="panel"`。
- 第 203、215、235、251、282 行的 `<label style={{ display:'block', margin:'0.5rem 0' }}>` 全部改用 `<Field label="…">`（从 `../components/modal` 导入），把 label 文字移入 `Field` 的 `label` prop。第 282 行的 Enabled 复选框同样用 `<Field label="Enabled">`，内层只留 `<input type="checkbox">`。
- 第 261 行 Check 子区块 → `className="stack"` 并保留 `style={{ paddingLeft: '1rem', borderLeft: '2px solid var(--border)' }}`。
- 第 179 行操作列的三个按钮加 `className="btn-ghost"`，`Delete` 再加 `btn-danger`。
- 第 189 行 `Last run` → `className="text-faint"`。
- 第 176 行 `<code>{t.cronExpression}</code>` 保持不变（`code` 已有样式）。
- 第 292 行 `Create`/`Save` 加 `btn-primary`。
- 表格与空态按 Task 7 对照表处理。

- [ ] **Step 2: 迁移 NotificationsPage**

- 第 68 行 `<h1>` 外补 `<div className="page-header">`。
- 第 76 行 URL 输入 `style={{ width: 380 }}` → `className="w-lg"`。
- 第 94 行 body 模板 textarea → `className="w-lg"`（`textarea` 已默认 mono）。
- 第 101 行 Content-Type 输入 → `className="w-md"`。
- 第 108 行 Proxy URL 输入 → `className="w-md"`。
- 第 117 行事件复选框 label → `className="row"` 并保留 `style={{ width: 200 }}`。
- 第 128 行按钮组 → `className="row"`，`Save` 加 `btn-primary`。
- 第 135 行 `Saved.` → `className="text-ok"`。
- 第 139 行测试结果 → `className={test.success ? 'text-ok' : 'text-danger'}`。

- [ ] **Step 3: 迁移 SettingsPage**

- 第 34 行 `<h1>` 外补 `<div className="page-header">`。
- 第 35 行说明 → `className="text-muted"`。
- 第 68 行 repack 勾选组 → `className="row"` 并保留 `style={{ flexWrap: 'wrap' }}`，去掉 `fontSize`。
- 第 102 行 retry backoff 输入 → `className="w-md mono"`，删除内联 style。
- 第 118 行按钮组 → `className="row"` 并保留 `style={{ marginTop: '1rem' }}`；`Save` 加 `btn-primary`。
- 第 120 行 `Saved.` → `className="text-ok"`。
- 第 143 行 `Rules` 的 textarea → `className="w-lg"`，删除内联 style。
- 第 138 行 `Num` 的 input → `className="w-sm"`。

- [ ] **Step 4: 迁移 CronEditor**

- 第 45 行与第 60 行的容器 → `className="row"` 并在第 60 行保留 `style={{ flexWrap: 'wrap' }}`。
- 第 49 行手输框 → `className="w-md mono"`。
- 第 87、101、115 行三个数字输入 → `className="w-sm"`（删掉 `width: 50`，32px 高的输入框配 50px 宽会挤掉 spinner）。

- [ ] **Step 5: 确认无字面色值**

Run: `cd frontend && grep -n "crimson\|#666\|#888\|#ccc\|#eee\|green'" src/pages/TasksPage.tsx src/pages/NotificationsPage.tsx src/pages/SettingsPage.tsx src/components/CronEditor.tsx`
Expected: 无输出

- [ ] **Step 6: 构建与 lint**

Run: `cd frontend && npm run build && npm run lint`
Expected: 通过

- [ ] **Step 7: 提交**

```bash
git add frontend/src/pages/TasksPage.tsx frontend/src/pages/NotificationsPage.tsx \
        frontend/src/pages/SettingsPage.tsx frontend/src/components/CronEditor.tsx
git commit -m "style: move Tasks, Notifications, Settings, and the cron editor onto the shared classes"
```

---

### Task 10: 迁移 BackupConfigsPage、RestoreDialog、PathBrowser

**Files:**
- Modify: `frontend/src/pages/BackupConfigsPage.tsx`（51 处）
- Modify: `frontend/src/components/RestoreDialog.tsx`（26 处）
- Modify: `frontend/src/components/PathBrowser.tsx`（9 处）

**Interfaces:**
- Consumes: Task 6 的 class、Task 7 的替换约定
- Produces: 无

**这是最后也是最大的一批，全部按 Task 7 的对照表机械替换。以下只列对照表未覆盖的特例。**

- [ ] **Step 1: 迁移 PathBrowser**

- 第 38-39 行 `style={overlayStyle}` / `style={panelStyle}` → `className={overlayStyle}` / `className={panelStyle}`（Task 6 Step 4 若已改则跳过）。
- 第 42 行路径显示 → `className="mono text-faint"` 并保留 `style={{ wordBreak: 'break-all' }}`。
- 第 46 行 → `className="text-danger"`。
- 第 48 行列表容器 → 保留 `style={{ maxHeight: 320, overflowY: 'auto' }}`，边框改 `border: '1px solid var(--border)'`，padding 用 `var(--sp-2)`。
- 第 51、59 行目录按钮 → `className="btn-ghost"`。
- 第 68 行文件名 span → `className="text-faint"`。
- 第 73、77 行提示 → `className="text-warn"`。
- 第 83 行按钮组 → `className="row"` 并保留 `style={{ marginTop: '1rem' }}`；`Use this folder` 加 `btn-primary`。

- [ ] **Step 2: 迁移 RestoreDialog**

先通读该文件，再按对照表替换 26 处。特例：

- 所有 `style={overlayStyle}` / `style={panelStyle}` → `className={...}`。
- 所有本地路径、blob 名、容器名的显示位置加 `className="mono"`。
- 主操作按钮（发起还原）加 `btn-primary`；取消保持默认。
- 冲突/覆盖类警示文字用 `text-warn`，失败用 `text-danger`。

- [ ] **Step 3: 迁移 BackupConfigsPage**

先通读该文件，再按对照表替换 51 处。特例：

- 第 358 行附近的检查报告区：`color: report.ok ? 'green' : 'crimson'` → `className={report.ok ? 'text-ok' : 'text-danger'}`；`color: report.orphanBlobs.length ? '#b06a00' : 'green'` → `className={report.orphanBlobs.length ? 'text-warn' : 'text-ok'}`。
- `style={{ width: 320, fontFamily:'monospace', fontSize:'0.85rem' }}` → `className="w-lg"`（textarea 已默认 mono）。
- `style={{ width:'100%', fontSize:'0.8rem', borderCollapse:'collapse' }}`（嵌套明细表）→ 删掉，改为外层加 `className="text-faint"`。
- `style={{ margin:'1rem 0', padding:'0.8rem', border:'1px solid #ccc' }}` → `className="panel"`。
- 本地路径输入框加 `className="w-lg mono"`，容器名输入框加 `className="w-md mono"`。
- 状态列改用 `badge` / `badge-ok` / `badge-warn` / `badge-danger`。
- `style={{ display:'inline-flex', alignItems:'center', gap:'0.4rem' }}` → `className="row"`。

- [ ] **Step 4: 全库确认再无字面色值**

Run: `cd frontend && grep -rn "crimson\|'#666'\|'#888'\|'#ccc'\|'#eee'\|'#555'\|'green'\|'#b45309'\|'#b91c1c'\|'#a60'\|'#b06a00'\|'#fffbeb'\|'#7c2d12'\|'#ddd'" src/`
Expected: 无输出

- [ ] **Step 5: 确认内联 style 只剩布局用途**

Run: `cd frontend && grep -rn "style={{" src/ | grep -iv "width\|height\|margin\|padding\|maxHeight\|overflow\|textAlign\|whiteSpace\|wordBreak\|flexWrap\|borderLeft\|border:"`
Expected: 无输出（若有，说明还有外观性内联样式没迁走）

- [ ] **Step 6: 构建与 lint**

Run: `cd frontend && npm run build && npm run lint`
Expected: 通过

- [ ] **Step 7: 提交**

```bash
git add frontend/src/pages/BackupConfigsPage.tsx frontend/src/components/RestoreDialog.tsx \
        frontend/src/components/PathBrowser.tsx
git commit -m "style: move the backup config page, restore dialog, and path browser onto the shared classes"
```

---

### Task 11: 全量验证与文档

**Files:**
- Modify: `README.md`（若其中有界面截图或界面描述需同步）
- Test: 全量

**Interfaces:**
- Consumes: Task 1–10 的全部产出
- Produces: 无

- [ ] **Step 1: 后端全量测试**

Run: `cd backend && dotnet test`
Expected: PASS。记录实际通过数；与本轮开始前的基线相比只应增加，不应减少。

- [ ] **Step 2: 前端构建与 lint**

Run: `cd frontend && npm run build && npm run lint`
Expected: 均通过，无 TypeScript error、无 oxlint error

- [ ] **Step 3: 逐页手工核对三态**

启动后端与前端，对 8 个页面（Accounts、Backups、Discovered、Groups、Tasks、Notifications、Logs、Settings）外加 Containers 子页、登录页、还原弹窗、目录浏览弹窗、密钥环横幅，逐一确认：

1. **浅色**：文字与背景对比充足，表格分隔线可见，主操作按钮明显。
2. **深色**（切换系统主题）：**没有任何白底黑字的孤岛**（弹窗、横幅是最容易漏的两处），输入框与按钮边框可见。
3. **窄屏**（窗口宽度 < 900px）：侧栏变顶部 tab 条且可横向滚动，表单 label 换行到输入框上方，弹窗不溢出屏幕。

发现问题就地修复并追加提交。

- [ ] **Step 4: 确认 README 与实际一致**

Run: `grep -n -i "screenshot\|界面\|UI layout" README.md`
若 README 描述了旧的顶部 tab 布局或引用了已删除的资源，同步更新；若无相关描述，跳过。

- [ ] **Step 5: 最终提交**

```bash
git add -A
git commit -m "chore: verify the UI rework across light, dark, and narrow layouts"
```

---

## 交付说明

前端没有任何自动化测试，本轮也未引入测试框架（见设计文档 §8）。因此 Task 11 Step 3 的视觉核对是**人工的**，不能自动重跑。汇报完成情况时应如实说明这一点：后端测试与前端构建/lint 是自动验证的，视觉部分是手工核对的。
