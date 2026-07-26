# 网页界面视觉改版与 Container 错误处理（2026-07-26）

> 界面目前没有设计：`index.css` / `App.css` 仍是 Vite 脚手架模板的残留且**正在生效**，应用样式全是散落在 3455 行 JSX 里的内联 `style`，控件一律浏览器默认外观，深色模式下多处失效。本轮建立一套设计系统与应用外壳，把界面做成紧凑运维控制台的观感。
>
> 同时修掉一个独立缺陷：新建 container 时后端返回裸 500，界面只显示 `ApiError: Internal Server error`。
>
> 本轮**只动视觉与布局骨架**，不改任何交互流程。

## 1. 设计决策（本轮锁定）

| # | 决策点 | 结论 |
|---|--------|------|
| 1 | 改版范围 | **视觉 + 布局骨架**。建立设计系统并重做外壳；页面内部的交互流程与信息组织保持不变 |
| 2 | 样式技术 | **零运行时依赖，手写 CSS**。不引入 Tailwind，不引入组件库；`package.json` 不新增任何依赖 |
| 3 | 视觉方向 | **紧凑运维控制台**（Linear / Vercel Dashboard 那类）。细分隔线而非阴影，4–6px 圆角，14px 正文，中性灰阶 + 单一强调色 |
| 4 | 深色模式 | **跟随系统，两套 token 都做**，无手动切换开关 |
| 5 | 样式组织 | **全局语义化 class**。以元素选择器打底（`button` / `input` / `select` / `table` 无需加类即生效），辅以少量语义类。不用 CSS Module，不用 TS 样式常量对象 |
| 6 | 表单字段宽度 | **按内容长度分档**。Blob Endpoint、Account Key、代理主机、本地路径等长内容走宽档 |
| 7 | Container 500 | **逐端点捕获 `RequestFailedException`**，不引入全局异常处理器——`KeyringGuard.cs:30` 记录的"不接管全局异常"决定继续有效 |
| 8 | 前端测试 | **本轮不引入前端测试框架**。视觉回归靠手工逐页核对，这是已知局限 |

## 2. 缺陷修复：新建 container 返回 500

### 2.1 根因

三层，逐层确认：

1. **触发**：输入的 container 名不符合 Azure 命名规则，Azure 返回 `400 InvalidResourceName`。
2. **后端**：`ContainerEndpoints.cs:42` 调用 `ContainerService.CreateContainerAsync` 未捕获 `RequestFailedException`；项目刻意没有全局异常处理器（仅 `UseSecretUnavailableMapping` 处理 `SecretUnavailableException`），因此 Azure 的 400 一路冒泡，由 Kestrel 兜底成裸 500。GET 与 DELETE 两个端点同样未捕获。
3. **前端**：`ContainersPage.tsx:23` 的 `create` 对名字零校验，直接把用户输入送上云；`client.ts:34` 在响应体为空时回落到 `res.statusText`，于是 `String(e)` 得到 `ApiError: Internal Server error`——一个既不说明哪里错、也不指向如何改的字符串。

### 2.2 修法

**后端**

新增静态校验器 `Services/ContainerName.cs` 的 `ContainerName.Validate(name)`，返回违规说明或 `null`，实现 Azure container 命名规则：

- 长度 3–63 字符
- 仅允许小写字母、数字、连字符
- 首尾必须是字母或数字
- 不允许连续连字符

POST 端点在**连云之前**调用校验，不合法直接 400 并指明违反的具体规则。理由：本地校验能给出可操作的消息，而 Azure 回的 "contains invalid characters" 不告诉用户哪个字符、也不告诉规则是什么。

GET / POST / DELETE 三个端点各自捕获 `RequestFailedException`：

- `Status` 落在 400–499 → 原样透传该状态码，消息含 `ErrorCode` 与 Azure 的说明
- 其余（含 `Status == 0` 的连接失败、5xx）→ 映射为 **502**，消息为存储账户不可达

逐端点捕获而非注册全局 handler：全局 handler 会一并接管本轮范围之外的所有未处理异常，改变既有失败语义。

**前端**

- `ContainersPage` 用一份与后端等价的 TypeScript 规则实现做提交前校验（规则重复实现于两端，后端为准、前端为提前反馈），非法时禁用创建按钮，并在输入框下方常驻规则说明。
- `client.ts` 的 `request` 解析 ProblemDetails 响应体，取 `detail` 或 `title` 作为 `ApiError.message`，解析不出再回落到原文与 `statusText`。**此项对全站所有错误提示生效**，不限于 container。
- `containersApi.remove` 拼 URL 时补 `encodeURIComponent`。

### 2.3 测试

- `ContainerName.Validate` 的单元测试：合法名、过短、过长、大写、下划线、首尾连字符、连续连字符各一例。
- 端点测试：非法名返回 400 且消息含规则说明；`RequestFailedException` 的 4xx 与连接失败分别映射为透传状态码与 502。
- 现有 `ContainerEndpointsTests` 的 Azurite 集成用例必须仍绿。

## 3. 设计基础

### 3.1 清理

以下全部删除——它们是 Vite 模板残留，且 `#root` 那条正在干扰真实布局：

- `index.css` 中：`#root { width: 1126px; text-align: center; border-inline }`、`font: 18px` 基准、`.counter`、模板配色变量
- `App.css` 整个文件（`.hero`、`#center`、`#next-steps`、`#docs`、`#spacer`、`.ticks`）
- `src/assets/hero.png`、`src/assets/react.svg`、`src/assets/vite.svg`
- `public/icons.svg`——经确认为模板自带的社交图标集（bluesky 等），`index.html` 与 `src/` 均未引用

`public/favicon.svg` 目前是 Vite 的紫色闪电标志，由 `index.html:5` 引用。替换为与 `--accent` 一致的简单标记；`index.html` 的 `<title>` 已是 `Azure Storage Backup`，无需改动。

### 3.2 Token

在 `index.css` 的 `:root` 定义浅色，`@media (prefers-color-scheme: dark)` 覆盖深色。

| 组 | 变量 |
|---|---|
| 表面 | `--bg` 画布、`--bg-subtle` 侧栏与表头、`--bg-raised` 卡片与弹窗 |
| 描边 | `--border`、`--border-strong` |
| 文字 | `--text`、`--text-muted`、`--text-faint` |
| 强调 | `--accent`、`--accent-hover`、`--accent-fg`、`--accent-subtle` |
| 语义 | `--ok` / `--warn` / `--danger`，各配 `-bg` 与 `-border` 变体 |
| 排版 | `--font-sans` 系统字体栈、`--font-mono`；正文 14px/1.5，h1 20px/600，h2 16px/600，辅助文字 12px |
| 间距 | `--sp-1` … `--sp-6` = 4 / 8 / 12 / 16 / 24 / 32 |
| 圆角 | `--r-sm` 4px、`--r-md` 6px、`--r-lg` 8px |
| 阴影 | 仅 `--shadow-overlay`，供弹窗与浮层使用 |

平面区域一律不用阴影——这是"紧凑控制台"与"卡片式 SaaS"的分界点，也是本轮视觉方向的执行要点。

端点、路径、容器名、哈希值一律使用 `--font-mono`。这类内容等宽后可读性差别显著。

### 3.3 焦点与可访问性

统一 `:focus-visible` 双层 ring（内圈用 `--bg` 隔开、外圈用 `--accent`），覆盖按钮、输入、链接、表格行内操作。禁用态统一降低不透明度并设 `cursor: not-allowed`。

## 4. 应用外壳

现状是 `App.tsx` 里一排裸 `<button>` 充当 tab。改为：

- **左侧固定侧栏，宽 220px**：顶部产品名，中部 8 个导航项（Accounts / Backups / Discovered / Groups / Tasks / Notifications / Logs / Settings），底部 `Log out`。选中态为左侧 2px `--accent` 竖条加深背景。
- **右侧内容区**：统一的 page header（h1 左、主操作按钮右），下方为页面内容。内容区最大宽度 1280px，左右 24px 内边距。
- **KeyringBanner** 置于内容区最顶、page header 之上，确保任何页面都优先可见。
- **窄屏（< 900px）**：侧栏塌缩为顶部横向滚动 tab 条。纯 CSS 媒体查询实现，React 组件结构与状态逻辑不变。
- **LoginPage** 用同一套 token 重做为居中卡片。

导航项的数据结构（`App.tsx` 中的 `tabs` 数组）与切换逻辑保持不变，只换外观。

## 5. 组件层

以元素选择器打底，绝大多数位置无需添加 `className`：

**按钮**：默认样式为次要按钮（描边 + `--bg-subtle`）。语义类 `.btn-primary`（`--accent` 实心）、`.btn-danger`、`.btn-ghost`（表格行内操作，无边框）。

**输入控件**：`input` / `select` / `textarea` 统一高度、圆角、描边、焦点 ring；placeholder 用 `--text-faint`。

**字段宽度分档**：`.w-sm` 160px、`.w-md` 280px、`.w-lg` 480px、`.w-full`。Blob Endpoint、Account Key、Proxy Host、本地路径走 `.w-lg` 或 `.w-full`。

**Field 组件去重**：当前存在**两份**实现——`components/modal.tsx`（label 宽 200）与 `pages/AccountsPage.tsx` 内的私有副本（label 宽 140）。统一为 `components/modal.tsx` 中的单一实现，布局改为 `grid-template-columns: 200px 1fr`，删除 `AccountsPage` 的副本。

**表格**：表头用 `--bg-subtle`、12px 字号并放大字距；行 `hover` 高亮；单元格内边距 8px / 12px；行分隔用 1px `--border`。统一 `.empty-state` 呈现空列表。

**弹窗**：`modalStyles.ts` 当前硬编码 `background: '#fff'`，深色模式下描边与内部控件配色全部失配。改为使用 `--bg-raised` 与 `--shadow-overlay`，遮罩加轻微模糊。

**横幅**：`.alert` 及 `.alert-warn` / `.alert-error` / `.alert-ok` 四态，替换 `KeyringBanner` 中硬编码的 `#fffbeb` / `#b45309` / `#7c2d12`。

**状态徽章**：`.badge` 配语义色，供 Tasks 与 Backups 页的状态列使用。

## 6. 实施顺序

| 阶段 | 内容 |
|---|---|
| 0 | §2 的缺陷修复。与样式无关，可独立验证与提交 |
| 1 | §3 清理与 token、全局元素样式、§4 外壳（`App.tsx`、`LoginPage`、`KeyringBanner`、`modalStyles`） |
| 2 | §5 组件层：`Field` 去重与宽度分档、`.btn-*`、`.badge`、`.empty-state`、`.alert` |
| 3 | 逐页移除内联 `style`：Accounts + Containers → Groups → Backups → Tasks → Notifications → Logs → Settings → `BackupConfigsPage`(1020 行) + `RestoreDialog`(484 行) + `PathBrowser` + `CronEditor` |

阶段 1 完成后，所有页面在不改一行页面代码的前提下即已获得大部分改善——因为控件样式走元素选择器。

大文件排在阶段 3 末尾：前面几页会把类名体系跑熟，届时大文件的改动是纯机械替换。

**明确不做**（守住范围）：不拆分 `BackupConfigsPage`、不新增仪表盘页、不改动任何交互流程、不引入前端路由、不引入前端测试框架。

## 7. 验证

- 后端 `dotnet test` 全量绿；新增 §2.3 所列测试；Azurite 可达时集成用例照常执行。
- 前端 `npm run build`（含 `tsc -b`）与 `npm run lint`（oxlint）均通过。
- 手工逐页核对浅色、深色、窄屏三态。

## 8. 已知局限

前端目前没有任何测试，本轮亦不引入测试框架，因此视觉回归无法自动化，只能靠手工核对。这一点在交付时如实说明，不以"已验证"表述掩盖。
