# 预置密码访问控制（2026-07-25）

> 工具原为单用户、无认证。本轮增加一道访问门：进入系统需输入密码，密码由环境变量预置，无用户名。这是**访问控制**，不是加密体系的一部分——不改动 Data Protection 密钥环的用途，也不把用户密码变成任何数据的加密主密钥（PRD §107 那个开放问题不在本轮范围内）。
>
> 补充 [product-requirements.md](product-requirements.md)。与 [keyring-loss-recovery-design.md](keyring-loss-recovery-design.md) 有一处必须遵守的交互，见 §5。

## 1. 设计决策（本轮锁定）

| # | 决策点 | 结论 |
|---|--------|------|
| 1 | 会话机制 | **Cookie + Data Protection 签发**。密钥环已存在，直接复用；`HttpOnly` 使 XSS 偷不走；有内建过期与滑动续期；登出即删 cookie。不用 localStorage token（XSS 可读）或 HTTP Basic（无法自定义登录页、登出困难） |
| 2 | 未设密码时 | **放行**，启动记一条 Warning。认证是可选加固，现有部署升级后行为不变。不做 UI 横幅 |
| 3 | 密码存放 | 环境变量明文 `Auth__Password`。不做 hash 预生成——那要求用户先跑一个工具算 hash，对自建单用户工具是不成比例的负担 |
| 4 | 保护范围 | 只拦 `/api/*`；静态资源不保护 |
| 5 | 豁免 | `/api/health`、`/api/health/ready`、`POST /api/auth/login`、`GET /api/auth/status` |
| 6 | 未认证响应 | **401**，不重定向。前端是 SPA + `fetch`，重定向只会让 `fetch` 拿到一份 HTML |
| 7 | 会话有效期 | 滑动过期 30 天 |
| 8 | 与密钥环恢复的关系 | 登录门在密钥环闸门**之外**。密码比对不经密钥环，故 `Lost` 时仍可登录 |

## 2. 配置

`Auth__Password`（即配置键 `Auth:Password`，遵循项目既有的 `Section__Key` 约定）。

- **未设置或为空** → 认证关闭，所有端点放行，启动时记一条 Warning：`Authentication is disabled: Auth__Password is not set.`
- **已设置** → 认证开启

镜像不为它设默认值——有默认密码比没有密码更危险。

## 3. 会话

ASP.NET Core cookie 认证，由现有 Data Protection 密钥环签发。

| 属性 | 值 | 理由 |
|---|---|---|
| `HttpOnly` | `true` | XSS 无法读取 |
| `SameSite` | `Lax` | 防 CSRF，同时不影响正常导航 |
| `SecurePolicy` | **`SameAsRequest`** | 见下 |
| 过期 | 滑动 30 天 | 日常使用无需重登；长期不用才失效 |

**`SecurePolicy` 必须是 `SameAsRequest`，不能硬编码为 `Always`。** 镜像默认监听 HTTP（`Dockerfile:33`，`ASPNETCORE_URLS=http://+:8080`）。若强制 `Secure`，浏览器在 HTTP 下根本不会回传 cookie，表现为「登录成功但立刻又被要求登录」——一个很难从症状反推到原因的故障。`SameAsRequest` 在 HTTPS 下自动加 `Secure`，在 HTTP 下不加。

## 4. 端点与中间件

### 4.1 端点

| 端点 | 请求 | 响应 |
|---|---|---|
| `POST /api/auth/login` | `{ "password": "..." }` | 正确 → **204** + `Set-Cookie`；错误 → **401** |
| `POST /api/auth/logout` | — | **204**，清除 cookie |
| `GET /api/auth/status` | — | `{ "required": bool, "authenticated": bool }` |

`status` 是前端唯一的决策依据，必须在未认证时也可访问。

### 4.2 中间件位置

管线顺序（`Program.cs` 现状加入本轮中间件后）：

```
UseCors → UseDefaultFiles → UseStaticFiles → [认证] → UseSecretUnavailableMapping → Map*Endpoints → MapFallbackToFile
```

认证置于 `UseStaticFiles` 之后（静态资源不保护）、`UseSecretUnavailableMapping` 之前（先判断能不能进门，再处理门内的业务异常），只对路径前缀 `/api/` 生效。

静态资源（HTML/JS/CSS）**不保护**：它们不含任何敏感数据，所有数据都经 API 获取；保护它们只会把登录页自身也挡在门外。

健康探针必须豁免。否则 `docker healthcheck` 与编排层探针一律得到 401，容器被判定不健康并反复重启——一个由「加强安全」直接导致的可用性故障。

### 4.3 安全细节

- 密码比对用 `CryptographicOperations.FixedTimeEquals`（对 UTF-8 字节比较）。防时序侧信道，零成本
- 登录失败后固定延迟约 1 秒，使在线爆破不划算。**不做账户锁定**——单用户工具锁定等于把自己关在门外
- 密码永不写入日志、永不出现在错误响应里

### 4.4 CORS（仅影响本地开发）

现有策略为 `WithOrigins(...).AllowAnyHeader().AllowAnyMethod()`（`Program.cs`），缺 `AllowCredentials()`。跨域请求默认不携带 cookie，因此本地跑 Vite dev server（`localhost:5173` → 后端 `localhost:8080`）会登录不上。需补 `AllowCredentials()`。

生产是同源单镜像部署，不受此影响。

## 5. 与密钥环恢复的交互（必须遵守）

登录门必须在密钥环闸门**之外**，且登录路径不得依赖 Data Protection 解密任何东西。

- 密码比对读的是环境变量明文，不经密钥环 → 密钥环 `Lost` 时**仍可登录**
- cookie 由密钥环签发 → `/keys` 丢失会使已有会话失效，需重新登录一次

正确顺序：密钥环丢失 → 重新登录（密码来自 env，不受影响）→ 进入系统 → 见到恢复横幅 → 逐项重设凭据。

若把登录门错误地置于 `KeyringGuard` 之后，或让登录依赖密钥环解密，就会形成死锁：**要恢复得先登录，要登录得先恢复**。

## 6. 前端

`App.tsx` 挂载时请求 `GET /api/auth/status`，据结果三选一：

| 状态 | 渲染 |
|---|---|
| `required: false` | 主界面，与当前行为完全一致 |
| `required: true, authenticated: false` | **仅**登录页 |
| `required: true, authenticated: true` | 主界面 + 导航栏 `Log out` |

未认证时**不挂载**任何主界面组件，而不是加遮罩层——遮罩之下的组件仍会发起请求，制造一片 401 噪音。

`api/client.ts` 增加 401 处理：任何 API 响应 401 即把认证状态打回未登录，App 自动切回登录页。这覆盖「cookie 过期后继续操作界面」的情形。

`fetch` 的 `credentials` 默认为 `same-origin`，同源部署下 cookie 自动携带，现有请求代码无需改动。

界面文案一律英文。登录页只含一个密码输入框与一个按钮，沿用现有页面的内联样式风格。

## 7. 测试

- 未设密码：所有端点放行；`/api/auth/status` 报 `required: false`
- 设了密码：未认证请求得 401
- **设了密码时健康探针仍返回 200**——这条最易在后续重构中被破坏，且破坏后果是容器反复重启
- 正确密码 → 204 + cookie；携带该 cookie 的后续请求通过
- 错误密码 → 401，且不下发 cookie
- 登出后原 cookie 失效
- 登录端点自身不被拦截（否则永远登不进去）
- `status` 端点在未认证时可访问

## 8. 文档

`README.md` 环境变量表增加 `Auth__Password` 一行，并在表下补一段说明：

- 不设即无认证
- cookie 由 `/keys` 的密钥环签发，故密钥环丢失需重新登录
- **生产环境应配 HTTPS 反代**。明文 HTTP 下密码与 cookie 均在网络上明文传输，这道门只挡住「不知道密码的人」，挡不住能嗅探流量的人

## 9. 明确不做

- 不做用户名、多用户、角色
- 不做密码修改界面（改环境变量并重启即可）
- 不做账户锁定（单用户工具会把自己锁死）
- 不把用户密码用作任何数据的加密主密钥（PRD §107 的开放问题不在本轮范围）
- 不保护静态资源
