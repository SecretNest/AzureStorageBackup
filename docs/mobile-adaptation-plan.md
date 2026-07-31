# 手机端适配 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让整个界面在手机上完整可操作——触摸目标达标、表格不出屏、弹窗与向导的动作按钮常驻可见——同时桌面端鼠标用户看不到任何变化。

**Architecture:** 把现有的单一 900px 断点拆成两条正交的轴：`max-width` 管布局（900px 平板层保留，新增 640px 手机层），`pointer: coarse` 管命中区域。表格在手机层分两类处理（主表卡片化、次表横滚）。8 处结构高度一致的弹窗抽成一个 `Modal` 组件，把三段结构与背景滚动锁定收进组件内部一次写好。新建备份向导是内联面板而非弹窗，单独用 sticky 动作栏处理。

**Tech Stack:** React 19 + TypeScript + Vite 8、原生 CSS。**不新增任何 npm 依赖。**

**设计依据:** `docs/mobile-adaptation-design.md`

## Global Constraints

- **界面文案一律英文**。本文档与代码注释用中文，但任何用户可见字符串必须是英文。
- **`frontend/package.json` 不得新增依赖**，dependencies 与 devDependencies 均不变。特别是：不引入 CSS 框架、不引入组件库、不引入前端测试框架。
- **不改动任何业务逻辑、API 调用、数据模型或交互流程**。本轮只改布局、命中区域与弹窗结构。搬动代码时逻辑原样保留，包括其注释。
- **不得回归桌面端的任何功能。** 桌面端的外观变化仅限下列 7 处——它们是结构改造的合理副产物，已经用户确认；除此之外桌面端不应有可见变化，尤其不得因为手机层的规则泄漏而改变：
  1. 侧栏 `Log out` 移除，改到 Settings 页（Task 2）
  2. Tasks 表 `Last run` 从操作列下方独立成一列（Task 5）
  3. 弹窗标题栏与动作栏之间各多一条分隔线（Task 6 的三段结构）
  4. 弹窗标题栏右上角新增 `✕` 关闭按钮，并支持 Esc 关闭（Task 6）
  5. PathBrowser 目录列表去掉 `maxHeight: 320`，改由 `.modal-body` 统一滚动（Task 7）
  6. PathBrowser 目录项从小按钮改为整行可点、右侧带 `›`（Task 9）
  7. 通知事件多选项从固定 200px 改为 200–260px 的弹性列（Task 9）

  整分支终审又查出 3 处轻微超出，都是结构改造不可避免的副产物，一并记在这里：

  8. 还原对话框的 `Restore to` 输入框从 340px 变成 480px（`.w-lg`，Task 7）
  9. 弹窗内容的内边距从四周 24px 变成三段各自 16px 上下 + 24px 左右（三段结构的副产物，Task 6）
  10. 6 张次表在内容溢出时于容器内横滚，而不是撑宽 `.app-main`（只在本来就溢出时看得出差别，Task 3）
- **样式一律走 `src/index.css` 的全局语义化 class**，引用 CSS 变量，不写字面色值，不新增内联 `style`。这是 `docs/web-ui-modernization-design.md` 建立的既有约定。
- **本项目前端没有测试框架**（`package.json` 的 scripts 只有 `dev`/`build`/`lint`/`preview`）。因此本计划的每个任务用**构建 + 静态检查 + 针对性代码核对**作为验证闸门，而非单元测试。这是一个**已知局限**：真机效果必须由用户在手机上实测。不要因为本计划没有 `it()` 就去引入 vitest——那超出本轮范围。
- 前端检查命令（每个任务结束前必须全绿）：
  ```bash
  cd frontend && npx tsc -b && npm run build && npm run lint
  ```
- 每个任务结束时提交一次。提交信息用英文，正文说明"为什么"而非罗列"改了什么"。

## 断点速查

实现时反复要用到这三个 media query，写法必须完全一致，不要出现 641px / 900.5px 之类的变体：

```css
@media (max-width: 900px) { /* 平板层：已存在，本轮基本不动 */ }
@media (max-width: 640px) { /* 手机层：卡片、底部栏、全屏弹窗 */ }
@media (pointer: coarse)  { /* 触屏：命中区域，与宽度无关 */ }
```

---

### Task 1: 断点与触摸基础

建立手机层与触屏层的全部基础规则。后续所有任务都依赖这一层。

**Files:**
- Modify: `frontend/index.html:6`
- Modify: `frontend/src/index.css`（在 `:root` 之后新增 `pointer: coarse` 块；在文件末尾新增手机层块）

- [ ] **Step 1: viewport 加 `viewport-fit=cover`**

`frontend/index.html:6` 现在是：

```html
<meta name="viewport" content="width=device-width, initial-scale=1.0" />
```

改为：

```html
<meta name="viewport" content="width=device-width, initial-scale=1.0, viewport-fit=cover" />
```

没有 `viewport-fit=cover` 时，iOS 会把页面限制在安全区内并用背景色填充刘海两侧，`env(safe-area-inset-*)` 全部解析为 `0`——后续 Task 2 / Task 6 的安全区处理会静默失效。

- [ ] **Step 2: 触屏层——控件高度**

在 `frontend/src/index.css` 的深色模式块（`@media (prefers-color-scheme: dark)`，以 `:95` 的 `}` 结束）之后插入：

```css
/* ── Touch ──────────────────────────────────────────────────────────────────
   与屏幕宽度无关，只看指针精度：1000px 宽的触屏平板同样需要大命中区，
   而桌面用户把窗口拖窄不该让按钮突然变粗。 */
@media (pointer: coarse) {
  :root {
    --control-h: 44px;
  }
}
```

因为全部控件都引用 `--control-h`（设计系统的既有约定），改这一个变量即全局生效。

- [ ] **Step 3: 触屏层——hover 粘滞与按下反馈**

紧接 Step 2 的 `@media (pointer: coarse)` 块**内部**（在 `:root` 规则之后、闭合花括号之前）追加：

```css
  /* 触屏上 :hover 会在点击后粘住不消失，表格会留下一片高亮，看着像"选中了"。 */
  tbody tr:hover {
    background: transparent;
  }

  /* 系统默认的蓝色点击方块会盖住下面这些自定义的按下反馈。 */
  button,
  a,
  label,
  [role='button'] {
    -webkit-tap-highlight-color: transparent;
  }

  /* 没有 hover 就必须有 active，否则触屏上点下去毫无反馈。 */
  button:active:not(:disabled) {
    background: var(--bg-hover);
  }
  .btn-primary:active:not(:disabled) {
    background: var(--accent-hover);
  }
  .btn-danger:active:not(:disabled) {
    background: var(--danger-bg);
  }
  .nav-item:active:not(:disabled) {
    background: var(--bg-hover);
  }
```

- [ ] **Step 4: 触屏层——小命中区扩展**

继续在同一个 `@media (pointer: coarse)` 块内部追加：

```css
  /* 视觉尺寸保持不变——放大会破坏紧凑控制台的观感——只把命中区撑到 44px。
     用伪元素实现：它不参与布局，因此不会推开周围内容。 */
  .icon-btn,
  .hit-target {
    position: relative; /* 伪元素的定位基准；缺了它命中区会落到更外层的定位祖先上 */
  }
  .icon-btn::after,
  .hit-target::after {
    content: '';
    position: absolute;
    inset: 50% auto auto 50%;
    width: 44px;
    height: 44px;
    transform: translate(-50%, -50%);
  }
```

`.hit-target` 是给 Task 9 用的通用类（还原对话框的树形展开三角等），这里一并定义好。

- [ ] **Step 5: 手机层——输入框字号与固定宽度**

在 `frontend/src/index.css` **文件末尾**新增（后续任务会继续往这个块里加规则，所以留好注释标记）：

```css
/* ── Mobile (≤640px) ────────────────────────────────────────────────────────
   手机层。平板层（≤900px）已经处理了侧栏塌陷与 .field 单列，这里只加手机独有的东西。 */
@media (max-width: 640px) {
  /* 16px 是硬门槛，不是观感问题：低于 16px 时 iOS Safari 会在聚焦输入框的瞬间
     放大整个页面，并且失焦后不缩回——用户此后看到的是一个需要手动双指缩小的界面。
     只有表单控件会触发这个行为，正文其余部分保持 14px。 */
  input,
  select,
  textarea {
    font-size: 16px;
  }

  /* 长输入框占满可用宽度：480px 在任何手机上都溢出，280px 在 320px 屏上也贴边。 */
  .w-md,
  .w-lg {
    width: 100%;
  }
}
```

**`.w-sm`（160px）刻意不动。** 这一档全是数字框——代理端口、版本数、保留天数，以及计划编辑器里 `at [__] h`、`min [__]` 这些**夹在文字中间**的输入框（`CronEditor.tsx:87/101/115`）。让它们占满一行既难看，又会把 CronEditor 那两个标签挤到别处去。160px 在最窄的 320px 屏上也放得下。

- [ ] **Step 5b: 计划编辑器的高级模式允许折行**

`frontend/src/components/CronEditor.tsx:45` 的 advanced 分支是 `<div className="row">`，里面是一个 `.w-md` 输入框加一个 Simple 按钮。Step 5 让 `.w-md` 占满宽度后，按钮会被挤出去——这个 `.row` 没有 `flexWrap`（简单模式的 `:60` 有）。改为：

```tsx
      <div className="row" style={{ flexWrap: 'wrap' }}>
```

简单模式（`:60`）已经有 `flexWrap: 'wrap'`，不需要改。

- [ ] **Step 6: 验证构建与静态检查**

```bash
cd frontend && npx tsc -b && npm run build && npm run lint
```

Expected: 三条命令全部退出码 0，`npm run build` 输出 `built in ...`，oxlint 报告 `Found 0 warnings and 0 errors`。

- [ ] **Step 7: 核对桌面端零变化**

用 grep 确认新增的规则**全部**包在 media query 内，没有一条泄漏到全局：

```bash
cd frontend && awk '/@media \(pointer: coarse\)/,0' src/index.css | head -60
```

Expected: 输出的每一条规则都在 `@media (pointer: coarse)` 或 `@media (max-width: 640px)` 的花括号内。若有规则写在了块外，桌面端会被影响——必须移进块内。

- [ ] **Step 8: 提交**

```bash
cd /home/scegg/Sources/AzureStorageBackup
git add frontend/index.html frontend/src/index.css
git commit -m "feat(ui): size hit areas by pointer precision, not window width

A 1000px touchscreen tablet needs 44px targets just as much as a phone
does, and a desktop user dragging the window narrow should not watch the
buttons swell. Splitting the two axes lets each answer its own question.

Inputs go to 16px on phones because below that iOS Safari zooms the whole
page on focus and never zooms back out.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: 底部导航栏与 `Log out` 迁移

**Files:**
- Modify: `frontend/src/index.css`（手机层块内追加）
- Modify: `frontend/src/App.tsx:55-97`
- Modify: `frontend/src/pages/SettingsPage.tsx:13-24`

**Interfaces:**
- Produces: `SettingsPage` 新增可选 props `{ authRequired?: boolean; onLogout?: () => void }`。Task 之后无人再消费 `App.tsx` 的 `.sidebar-footer`。

- [ ] **Step 1: 手机层底部栏样式**

在 `frontend/src/index.css` 的 `@media (max-width: 640px)` 块内追加：

```css
  /* 底部固定 tab 栏：拇指够得着，且不随页面滚走。
     结构不变（仍是 .sidebar），只是换个位置和方向。 */
  .app-shell {
    grid-template-rows: minmax(0, 1fr) auto;
  }
  .sidebar {
    order: 2;
    position: fixed;
    inset: auto 0 0 0;
    z-index: 40; /* 低于 .modal-overlay 的 50：全屏弹窗必须盖住导航栏 */
    flex-direction: row;
    border-top: 1px solid var(--border);
    border-bottom: none;
    padding: 0;
    padding-bottom: env(safe-area-inset-bottom);
    overflow: visible;
  }
  /* 品牌名在手机上是纯装饰：页面自己有 <h1>，这条只是在抢高度。 */
  .sidebar-brand {
    display: none;
  }
  .sidebar-nav {
    flex: 1;
    gap: 0;
  }
  .nav-item {
    flex: 1;
    min-height: 52px;
    justify-content: center;
    text-align: center;
    border-bottom: none;
    border-top: 2px solid transparent;
    border-radius: 0;
  }
  .nav-item-active {
    border-radius: 0;
    border-bottom: none;
    border-top-color: var(--accent);
  }
  /* 底部栏是 fixed 的，脱离了文档流：不给内容留出等高的下边距，
     最后一行内容会被永久盖住，而且没有任何办法滚出来。 */
  .app-main {
    padding-bottom: calc(52px + var(--sp-4) + env(safe-area-inset-bottom));
  }
```

`.nav-item` 用 `min-height: 52px` 而不是依赖 `--control-h`：底部栏是导航而非表单控件，52px 是 iOS/Android 标签栏的惯用高度。

- [ ] **Step 2: 从 `App.tsx` 移除 `Log out`，把登出能力传给 SettingsPage**

`frontend/src/App.tsx` 现在的 `:72-86` 是：

```tsx
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
```

**整段删除**，并把登出逻辑提为组件内的一个函数。在 `App.tsx` 的 `refreshAuth` 定义（`:29-31`）之后新增：

```tsx
  // 无论服务端登出成功与否都清掉本地状态：失败却停在主界面，
  // 会让人以为自己已经退出了——在共用机器上这就是个安全问题。
  const logout = () => {
    const signedOut = () => setAuth({ required: true, authenticated: false })
    authApi.logout().then(signedOut, signedOut)
  }
```

然后把 `:95` 的渲染改为传参：

```tsx
        {tab === 'settings' && <SettingsPage authRequired={auth.required} onLogout={logout} />}
```

- [ ] **Step 3: SettingsPage 接收并渲染登出区**

`frontend/src/pages/SettingsPage.tsx:13-24` 现在是：

```tsx
export function SettingsPage() {
  return (
    <section>
      <div className="page-header">
        <h1>Settings</h1>
      </div>
      <AccountsSection />
      <BackupDefaults />
      <NotificationsSection />
    </section>
  )
}
```

改为：

```tsx
export function SettingsPage({
  authRequired,
  onLogout,
}: {
  authRequired?: boolean
  onLogout?: () => void
}) {
  return (
    <section>
      <div className="page-header">
        <h1>Settings</h1>
      </div>
      <AccountsSection />
      <BackupDefaults />
      <NotificationsSection />
      {/* 登出从侧栏搬到这里：手机上底部栏只有四格，塞不下第五个入口。
          桌面端也一并搬过来——同一个功能摆两个位置，是后续最容易忘记同步的那种东西。 */}
      {authRequired && onLogout && (
        <>
          <h2>Session</h2>
          <button type="button" onClick={onLogout}>
            Log out
          </button>
        </>
      )}
    </section>
  )
}
```

- [ ] **Step 4: 清掉失去用途的 `.sidebar-footer` 样式**

`frontend/src/index.css:326-329` 与 `:405-409` 的 `.sidebar-footer` 规则在 Step 2 之后没有任何元素匹配。删除这两处，避免留下"看起来还在用"的死样式。

- [ ] **Step 5: 确认没有遗留引用**

```bash
cd frontend && grep -rn "sidebar-footer" src/
```

Expected: 无输出（退出码 1）。有输出说明 Step 2 或 Step 4 漏了。

- [ ] **Step 6: 验证构建与静态检查**

```bash
cd frontend && npx tsc -b && npm run build && npm run lint
```

Expected: 全部退出码 0。特别注意 `tsc` 会检查 Step 2 传的 props 与 Step 3 的签名是否匹配——这是本任务唯一的类型契约。

- [ ] **Step 7: 提交**

```bash
cd /home/scegg/Sources/AzureStorageBackup
git add frontend/src/App.tsx frontend/src/pages/SettingsPage.tsx frontend/src/index.css
git commit -m "feat(ui): move the nav to the bottom on phones, log out into Settings

A bottom bar is where the thumb already is, and it does not scroll away.
Four tabs fill it exactly, which leaves no room for Log out — so that
moves into Settings. It moves on desktop too: one function in two places
is the kind of thing that later drifts out of sync.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: 表格横向滚动容器

处理 6 张不做卡片化的表。Backups / Tasks 两张主表**不在本任务范围**（Task 4 / Task 5 处理）。

**Files:**
- Modify: `frontend/src/index.css`（新增 `.table-scroll`，放在 `── Tables ──` 段末尾，即 `:289` 之后）
- Modify: `frontend/src/pages/LogsPage.tsx:103`
- Modify: `frontend/src/pages/AccountsPage.tsx:177`
- Modify: `frontend/src/pages/ContainersPage.tsx:94`
- Modify: `frontend/src/pages/GroupsPage.tsx:109`
- Modify: `frontend/src/components/RestoreDialog.tsx:323`
- Modify: `frontend/src/pages/BackupConfigsPage.tsx:2100`

**Interfaces:**
- Produces: CSS 类 `.table-scroll`，用法是把 `<table>` 包一层 `<div className="table-scroll" tabIndex={0}>`。

- [ ] **Step 1: 新增 `.table-scroll`**

在 `frontend/src/index.css` 的 `tbody tr.ops-row:hover { ... }` 规则之后（`── Tables ──` 段落末尾，`── Shell ──` 注释之前）插入：

```css
/* 窄屏上装不下的表包一层它。表格本身不变形——日志这类扫读型的表，
   横滚比拆成卡片好用：卡片化后一屏看不了几条。 */
.table-scroll {
  overflow-x: auto;
  -webkit-overflow-scrolling: touch;
}
/* 容器可聚焦（用法处加 tabIndex），键盘用户才能滚动溢出的内容（WCAG 2.1.1）。
   但它不是控件，获焦时的 outline 会画出一个没有意义的大方框。 */
.table-scroll:focus {
  outline: none;
}
.table-scroll:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}
```

- [ ] **Step 2: 包装 Logs 表**

`frontend/src/pages/LogsPage.tsx:103` 的 `<table>` 与其配对的 `</table>`，外面包一层：

```tsx
      <div className="table-scroll" tabIndex={0}>
        <table>
          {/* ...原内容原样保留，缩进相应增加... */}
        </table>
      </div>
```

- [ ] **Step 3: 包装其余 5 张表**

用与 Step 2 完全相同的写法（`<div className="table-scroll" tabIndex={0}>` 包住 `<table>...</table>`）处理：

- `frontend/src/pages/AccountsPage.tsx:177`
- `frontend/src/pages/ContainersPage.tsx:94`
- `frontend/src/pages/GroupsPage.tsx:109`
- `frontend/src/components/RestoreDialog.tsx:323`
- `frontend/src/pages/BackupConfigsPage.tsx:2100`（这张是 `<table className="text-faint">`，`className` 留在 `<table>` 上不要挪到 wrapper）

- [ ] **Step 4: 确认 6 处都包到了**

```bash
cd frontend && grep -rn "table-scroll" src/ | grep -v index.css | wc -l
```

Expected: `6`

- [ ] **Step 5: 确认没有把主表误包进来**

```bash
cd frontend && sed -n '555,565p' src/pages/BackupConfigsPage.tsx && sed -n '148,158p' src/pages/TasksPage.tsx
```

Expected: 两处输出里的 `<table>` 上方**没有** `table-scroll` —— 它们由 Task 4 / Task 5 卡片化，包了横滚容器会与卡片布局打架。

- [ ] **Step 6: 验证构建与静态检查**

```bash
cd frontend && npx tsc -b && npm run build && npm run lint
```

Expected: 全部退出码 0。JSX 嵌套写错（少一个闭合标签）在这里会被 `tsc` 抓到。

- [ ] **Step 7: 提交**

```bash
cd /home/scegg/Sources/AzureStorageBackup
git add frontend/src/index.css frontend/src/pages/LogsPage.tsx frontend/src/pages/AccountsPage.tsx frontend/src/pages/ContainersPage.tsx frontend/src/pages/GroupsPage.tsx frontend/src/components/RestoreDialog.tsx frontend/src/pages/BackupConfigsPage.tsx
git commit -m "fix(ui): let the narrow tables scroll sideways instead of leaving the screen

Logs and friends stay tables on purpose. They are read by scanning down a
column, and cards would fit three rows to a screen where the table fits
fifteen. The container takes tabIndex so a keyboard can reach the
overflow too.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: Backups 主表卡片化

最难的一张：6 列，外加一条跨全部列的运行状态行。

**Files:**
- Modify: `frontend/src/index.css`（手机层块内追加）
- Modify: `frontend/src/pages/BackupConfigsPage.tsx:560-682`

- [ ] **Step 1: 手机层卡片化样式**

在 `frontend/src/index.css` 的 `@media (max-width: 640px)` 块内追加：

```css
  /* 卡片化的表。只有显式标了 .cards 的表才变形——日志那类表要留在 Task 3 的横滚里。 */
  table.cards,
  table.cards tbody,
  table.cards tr,
  table.cards td {
    display: block;
    width: auto;
  }
  /* 列名改由每个 td 自己用 ::before 显示，表头就没用了。
     用 position:absolute 而不是 display:none：后者会让屏幕阅读器也读不到。 */
  table.cards thead {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip-path: inset(50%);
  }
  table.cards tr {
    border: 1px solid var(--border);
    border-radius: var(--r-lg);
    margin-bottom: var(--sp-3);
    padding: var(--sp-2) var(--sp-3);
    background: var(--bg-raised);
  }
  table.cards td {
    border-bottom: none;
    padding: var(--sp-1) 0;
    display: grid;
    grid-template-columns: 96px minmax(0, 1fr);
    gap: var(--sp-3);
    align-items: start;
  }
  table.cards td::before {
    content: attr(data-label);
    color: var(--text-muted);
    font-size: 12px;
  }
  /* 首列是这张卡片的标题——给它标签反而是噪音。 */
  table.cards td.card-title {
    display: block;
    font-weight: 600;
    font-size: 15px;
    padding-bottom: var(--sp-2);
  }
  table.cards td.card-title::before {
    content: none;
  }
  /* 操作列：按钮横排在卡片底部，上面划一条线与字段区分开。
     它的 data-label 是空的，所以不占标签列。 */
  table.cards td.card-actions {
    display: flex;
    flex-wrap: wrap;
    gap: var(--sp-2);
    margin-top: var(--sp-2);
    padding-top: var(--sp-2);
    border-top: 1px solid var(--border);
    text-align: left;
  }
  table.cards td.card-actions::before {
    content: none;
  }
  /* 空表提示不是卡片，别给它画边框。 */
  table.cards td.empty-state {
    display: block;
  }
  table.cards td.empty-state::before {
    content: none;
  }

  /* 运行状态行在桌面上是跨全列的独立一行，卡片化后要和上面那张卡合成一张。 */
  table.cards tr.has-ops {
    margin-bottom: 0;
    border-bottom: none;
    border-bottom-left-radius: 0;
    border-bottom-right-radius: 0;
  }
  table.cards tr.ops-row {
    border-top: none;
    border-top-left-radius: 0;
    border-top-right-radius: 0;
    padding-top: 0;
  }
  table.cards tr.ops-row td {
    display: block;
    padding-top: 0;
  }
  table.cards tr.ops-row td::before {
    content: none;
  }
```

- [ ] **Step 2: 给 Backups 表加 `cards` 类**

`frontend/src/pages/BackupConfigsPage.tsx:560` 现在是 `<table>`，改为：

```tsx
      <table className="cards">
```

- [ ] **Step 3: 给每个 `<td>` 加 `data-label`**

表头顺序是 Name / Account / Container / Local Root / Encrypted / Status /（操作），见 `:562-569`。对应 `:588` 起 `tbody` 里那一行的 6 个 `<td>`，按顺序改为：

```tsx
                <td className="card-title">
```
（第 1 个，Name 列——它是卡片标题，不需要标签）

```tsx
                <td data-label="Account">
                <td data-label="Local Root">
                <td data-label="Encrypted">
                <td data-label="Status">
```
（第 2–5 个，按表头文字逐一对应）

```tsx
                <td className="card-actions">
```
（第 6 个，操作列。如果它原本已有 `className` 或 `style`，保留原有内容并追加 `card-actions`——例如 `className="card-actions"` 与既有的 `style={{ textAlign: 'right' }}` 并存不冲突，手机层的 `text-align: left` 会覆盖它。）

空表那行（`:582` 的 `<td colSpan={6} className="empty-state">`）保持不变——Step 1 已经为它单独出规则。

`.ops-row` 那个 `<td>`（跨列的运行状态）**不加** `data-label`。

- [ ] **Step 4: 核对标签与表头一一对应**

```bash
cd frontend && sed -n '560,600p' src/pages/BackupConfigsPage.tsx | grep -o '<th>[^<]*</th>\|data-label="[^"]*"\|className="card-title"\|className="card-actions"'
```

Expected: 输出的 `<th>` 文字顺序与随后的 `data-label` / `card-title` / `card-actions` 顺序严格一致。对不上就是标签错位——手机上会显示"Local Root: Yes"这类张冠李戴的内容，而构建不会报错。

- [ ] **Step 5: 验证构建与静态检查**

```bash
cd frontend && npx tsc -b && npm run build && npm run lint
```

Expected: 全部退出码 0。

- [ ] **Step 6: 提交**

```bash
cd /home/scegg/Sources/AzureStorageBackup
git add frontend/src/index.css frontend/src/pages/BackupConfigsPage.tsx
git commit -m "feat(ui): fold the backup table into cards on phones

Six columns will not fit, and this is the one table where the answer is
not sideways scrolling: the buttons live in the last column, so scrolling
would put every action off screen. The running-status row merges into the
card above it, since it is talking about that same backup.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Tasks 表卡片化

**Files:**
- Modify: `frontend/src/pages/TasksPage.tsx:152-195`

复用 Task 4 建立的 `table.cards` 样式，本任务**不需要**改 CSS。

- [ ] **Step 1: 加 `cards` 类**

`frontend/src/pages/TasksPage.tsx:152` 现在是 `<table>`，改为：

```tsx
      <table className="cards">
```

- [ ] **Step 2: 给每个 `<td>` 加 `data-label`**

表头顺序是 Target / Type / Schedule / Enabled /（操作），见 `:156-160`。`tbody` 里那一行（`:170` 起）的 5 个 `<td>` 按顺序改为：

```tsx
              <tr key={t.id}>
                <td className="card-title">
                  {targetKindLabels[t.targetKind]}: {describeTarget(t)}
                </td>
                <td data-label="Type">{taskTypeLabels[t.taskType]}</td>
                <td data-label="Schedule">
                  <code>{t.cronExpression}</code>
                </td>
                <td data-label="Enabled">{t.enabled ? 'Yes' : 'No'}</td>
                <td className="card-actions" style={{ textAlign: 'right' }}>
```

第 5 个 `<td>` 原本是 `<td style={{ textAlign: 'right' }}>`，保留 `style` 并加上 `className="card-actions"`——桌面端右对齐的行为不能变，手机层的 `text-align: left` 会在卡片里覆盖它。

- [ ] **Step 3: 处理操作列里的 "Last run" 说明**

操作列的 `<td>` 末尾有一个 `<div className="text-faint">Last run: …</div>`（`:187-189`）。Step 2 让这个 `<td>` 变成了 `display: flex` 的按钮行，这个 div 会被当成第四个按钮挤在同一行。

把它移出操作列，作为独立字段放在 Enabled 之后：

```tsx
                <td data-label="Enabled">{t.enabled ? 'Yes' : 'No'}</td>
                <td data-label="Last run" className="text-faint">
                  {t.lastRunAt ? new Date(t.lastRunAt).toLocaleString() : 'never'}
                </td>
                <td className="card-actions" style={{ textAlign: 'right' }}>
                  <button type="button" className="btn-ghost" onClick={() => runNow(t)} disabled={running === t.id}>
                    {running === t.id ? 'Running…' : 'Run now'}
                  </button>{' '}
                  <button type="button" className="btn-ghost" onClick={() => startEdit(t)}>
                    Edit
                  </button>{' '}
                  <button type="button" className="btn-ghost btn-danger" onClick={() => remove(t)}>
                    Delete
                  </button>
                </td>
```

这会多出一列，所以还要：

- 表头 `:160` 的空 `<th></th>` 之前插入一个 `<th>Last run</th>`
- 空表行 `:165` 的 `colSpan={5}` 改为 `colSpan={6}`

桌面端的变化是 "Last run" 从操作列下方挪到自己的一列——这比塞在按钮下面更符合表格的读法，且是本任务无法回避的结构调整。

- [ ] **Step 4: 核对列数一致**

```bash
cd frontend && sed -n '152,200p' src/pages/TasksPage.tsx | grep -c '<th>' && sed -n '152,200p' src/pages/TasksPage.tsx | grep -o 'colSpan={[0-9]*}'
```

Expected: 第一条输出 `6`（Target / Type / Schedule / Enabled / Last run / 空），第二条输出 `colSpan={6}`。两者必须相等，否则空表提示的横跨宽度是错的。

- [ ] **Step 5: 验证构建与静态检查**

```bash
cd frontend && npx tsc -b && npm run build && npm run lint
```

Expected: 全部退出码 0。

- [ ] **Step 6: 提交**

```bash
cd /home/scegg/Sources/AzureStorageBackup
git add frontend/src/pages/TasksPage.tsx
git commit -m "feat(ui): fold the tasks table into cards, give Last run its own column

Last run was tucked under the action buttons, which the card layout turns
into a button row — it would have been dealt a fourth seat among them.
A column of its own reads better on the desktop table too.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: `Modal` 组件与背景滚动锁定

8 处弹窗的结构完全一致（overlay + panel + `<h3>` + 内容 + 尾部 `.row` 按钮），抽成一个组件后三段结构与滚动锁定只写一份。

**Files:**
- Create: `frontend/src/components/Modal.tsx`
- Modify: `frontend/src/index.css`（`── Modal ──` 段，`:646-679`）

**Interfaces:**
- Produces:
  ```tsx
  export function Modal(props: {
    title: ReactNode
    onClose: () => void
    footer?: ReactNode
    secondary?: boolean
    children: ReactNode
  }): JSX.Element
  ```
  Task 7 的 8 处调用点全部消费这个签名。`secondary` 用于叠在另一个弹窗之上的 PathBrowser。

- [ ] **Step 1: 写 `Modal` 组件**

创建 `frontend/src/components/Modal.tsx`：

```tsx
import { useEffect, type ReactNode } from 'react'

// 开着的弹窗数量。锁定 body 滚动必须计数，不能各自为政：
// 还原对话框会在自身之上再开 PathBrowser，内层关闭时若直接恢复 overflow，
// 外层还开着，背景就又能滚了。
let openCount = 0
let restoreOverflow = ''

function useModalScrollLock() {
  useEffect(() => {
    if (openCount === 0) {
      restoreOverflow = document.body.style.overflow
      document.body.style.overflow = 'hidden'
    }
    openCount += 1
    return () => {
      openCount -= 1
      if (openCount === 0) document.body.style.overflow = restoreOverflow
    }
  }, [])
}

/**
 * 弹窗外壳。三段结构（标题栏 / 内容 / 动作栏）在手机上是全屏面板的骨架：
 * 标题栏与动作栏固定，只有中间滚动——否则长表单的"保存"会在几屏以外。
 * 桌面端外观与手写这套结构时一致。
 */
export function Modal({
  title,
  onClose,
  footer,
  secondary,
  children,
}: {
  title: ReactNode
  onClose: () => void
  footer?: ReactNode
  /** 叠在另一个弹窗之上时置位，用更高的层级。 */
  secondary?: boolean
  children: ReactNode
}) {
  useModalScrollLock()

  // Esc 关闭。手机全屏时遮罩不可见，点外面关不掉；桌面上这也是弹窗的常规行为。
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div
      className={secondary ? 'modal-overlay modal-overlay-secondary' : 'modal-overlay'}
      onClick={onClose}
    >
      <div className="modal-panel" onClick={(e) => e.stopPropagation()}>
        <div className="modal-header">
          <h3>{title}</h3>
          <button type="button" className="icon-btn modal-close" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </div>
        <div className="modal-body">{children}</div>
        {footer && <div className="modal-footer">{footer}</div>}
      </div>
    </div>
  )
}
```

- [ ] **Step 2: 三段结构的桌面样式**

在 `frontend/src/index.css` 的 `.modal-panel > h3:first-child { ... }` 规则（`:670-672`）**之后**、`@media (max-width: 900px)` 之前插入：

```css
/* 内容区自己滚，标题栏与动作栏不动。桌面上 max-height 由 .modal-panel 限制，
   手机上（见下）改成占满视口。 */
.modal-panel {
  display: grid;
  grid-template-rows: auto minmax(0, 1fr) auto;
  padding: 0;
}
.modal-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--sp-3);
  padding: var(--sp-4) var(--sp-5);
  border-bottom: 1px solid var(--border);
}
.modal-body {
  padding: var(--sp-4) var(--sp-5);
  overflow-y: auto;
}
.modal-footer {
  display: flex;
  flex-wrap: wrap;
  gap: var(--sp-2);
  padding: var(--sp-4) var(--sp-5);
  border-top: 1px solid var(--border);
}
.modal-close {
  font-size: 16px;
  line-height: 1;
}
/* 二级弹窗（叠在另一个弹窗之上的 PathBrowser）。原先两层同为 z-index 50，
   靠 DOM 顺序侥幸生效；手机上两层都是全屏，顺序不对就是"点了没反应"。 */
.modal-overlay-secondary {
  z-index: 60;
}
```

`.modal-panel` 原有的 `padding: var(--sp-5)` 被这里的 `padding: 0` 取代（内边距移到三段各自身上）。检查 `:659-669` 的原规则，把 `padding: var(--sp-5);` 那一行删掉，不要留两条冲突的声明。

同时删除 `.modal-panel > h3:first-child { margin-bottom: var(--sp-3); }`——标题现在由 `.modal-header` 定位，这条规则已无匹配对象。

- [ ] **Step 3: 手机层全屏面板**

在 `frontend/src/index.css` 的 `@media (max-width: 640px)` 块内追加：

```css
  /* 全屏面板：长表单的动作按钮必须常驻可见，这是"手机上完整可操作"的关键一处。 */
  .modal-overlay {
    padding: 0;
  }
  .modal-panel {
    width: 100vw;
    height: 100dvh; /* 不用 vh：地址栏收缩会改变它的解析值，动作栏会跟着跳 */
    min-width: 0;
    max-width: none;
    max-height: none;
    border: none;
    border-radius: 0;
    box-shadow: none;
  }
  .modal-header,
  .modal-body,
  .modal-footer {
    padding-left: var(--sp-4);
    padding-right: var(--sp-4);
  }
  .modal-footer {
    padding-bottom: calc(var(--sp-4) + env(safe-area-inset-bottom));
  }
  /* 动作按钮在手机上占满一行更好点，主按钮排在最前面。 */
  .modal-footer > button {
    flex: 1 1 auto;
    min-width: 120px;
  }
```

`@media (max-width: 900px)` 里原有的 `.modal-panel { min-width: 0; width: 100%; }`（`:674-679`）保留不动——那是平板层的规则，手机层的声明因为写在后面且更具体而胜出。

- [ ] **Step 4: 验证构建与静态检查**

```bash
cd frontend && npx tsc -b && npm run build && npm run lint
```

Expected: 全部退出码 0。此时 `Modal` 还没有任何调用者，`tsc` 不会报未使用——它是导出的。

- [ ] **Step 5: 确认没有重复的 padding 声明**

```bash
cd frontend && awk '/^\.modal-panel \{/,/^\}/' src/index.css
```

Expected: 输出的 `.modal-panel` 规则块里 `padding` 只出现一次且值为 `0`。出现两次说明 Step 2 漏删了原来那行。

- [ ] **Step 6: 提交**

```bash
cd /home/scegg/Sources/AzureStorageBackup
git add frontend/src/components/Modal.tsx frontend/src/index.css
git commit -m "feat(ui): give modals a header/body/footer shell that survives a phone screen

Eight dialogs were each hand-rolling the same overlay/panel/title/buttons
shape. One component instead, so the fixed header and footer — the part
that keeps Save reachable on a full-screen phone panel — get written once.

The scroll lock counts open dialogs rather than toggling a flag: the
restore dialog opens the folder picker on top of itself, and the inner
one closing must not unlock the page under the outer one.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: 8 处弹窗接入 `Modal`

**Files:**
- Modify: `frontend/src/components/PathBrowser.tsx:37-93`
- Modify: `frontend/src/pages/AccountsPage.tsx:382-427`
- Modify: `frontend/src/components/RestoreDialog.tsx:273-...`（含 `:409` 的动作行）
- Modify: `frontend/src/pages/BackupConfigsPage.tsx`（5 处：`:1552` / `:1774` / `:1812` / `:1843` / `:1981`）
- Modify: `frontend/src/components/modalStyles.ts`（删除）

**Interfaces:**
- Consumes: Task 6 的 `Modal({ title, onClose, footer?, secondary?, children })`

**改造模式**（8 处一致，逐处套用）：

原结构
```tsx
    <div className={overlayStyle} onClick={onClose}>
      <div className={panelStyle} onClick={(e) => e.stopPropagation()}>
        <h3 style={{ marginTop: 0 }}>标题</h3>
        …内容…
        <div className="row" style={{ marginTop: '1rem' }}>
          …按钮…
        </div>
      </div>
    </div>
```
改为
```tsx
    <Modal title={标题} onClose={onClose} footer={<>…按钮…</>}>
      …内容…
    </Modal>
```

- [ ] **Step 1: PathBrowser**

`frontend/src/components/PathBrowser.tsx`：把 `:3` 的 `import { overlayStyle, panelStyle } from './modalStyles'` 换成 `import { Modal } from './Modal'`。`:37-93` 的返回值改为：

```tsx
  return (
    <Modal
      title="Choose a folder"
      onClose={onClose}
      secondary
      footer={
        <>
          <button type="button" className="btn-primary" onClick={() => data && onPick(data.path)} disabled={!data}>
            Use this folder
          </button>
          <button type="button" onClick={onClose}>
            Cancel
          </button>
        </>
      }
    >
      <p className="mono text-faint" style={{ wordBreak: 'break-all' }}>
        {data?.path ?? path ?? ''}
      </p>
      {/* …原 :44-80 的错误提示与目录列表原样保留… */}
    </Modal>
  )
```

`secondary` 必须置位：还原对话框会在自身之上打开它。原来的 `<h3>` 与末尾 `.row` 按钮块删除（内容已进 `title` / `footer`）。

目录列表容器 `:50` 的 `style={{ maxHeight: 320, ... }}` 中的 `maxHeight: 320` **删掉**——`.modal-body` 现在负责滚动，内层再限高会出现两个嵌套滚动条，手机上尤其难用。边框与内边距保留。

- [ ] **Step 2: AccountsPage 的重输凭据弹窗**

`frontend/src/pages/AccountsPage.tsx`：`:12` 的 import 换成 `import { Modal } from '../components/Modal'`。`:382-427` 改为：

```tsx
    <Modal
      title={`Re-enter Credentials — ${account.name}`}
      onClose={onClose}
      footer={
        <>
          <button
            type="button"
            className="btn-primary"
            onClick={() => onSubmit(accountKey, proxyPassword)}
            disabled={busy || !accountKey}
          >
            Submit
          </button>
          <button type="button" onClick={onClose} disabled={busy}>
            Cancel
          </button>
        </>
      }
    >
      {/* …原 :385-411 的说明段落、两个 Field、错误提示原样保留… */}
    </Modal>
```

- [ ] **Step 3: RestoreDialog**

`frontend/src/components/RestoreDialog.tsx`：`:16` 的 import 换成 `import { Modal } from './Modal'`。`:273` 起的两层 div 换成 `<Modal>`，`title` 用 `` {`Restore — ${config.name}`} ``，`:409` 的 `<div className="row" style={{ marginTop: '0.8rem' }}>` 整块内容移进 `footer={<>…</>}`。

`:277` 的 `<input className="mono" ... style={{ width: 340 }} />` 把 `style` 删掉、改为 `className="mono w-lg"`——Task 1 已让 `.w-lg` 在手机层占满宽度，固定 340px 会在小屏上溢出。

- [ ] **Step 4: BackupConfigsPage 的 5 处**

`frontend/src/pages/BackupConfigsPage.tsx`：`:10` 的 import 换成 `import { Modal } from '../components/Modal'`，然后逐处套用改造模式：

| 位置 | title | footer 内容（原动作行） |
|---|---|---|
| `:1552` ErrorModal | `` {`Last error — ${config.name}`} `` | `:1570-1576` 的 Copy / Close |
| `:1774` 删除确认 | `` {`Delete Backup — ${config.name}`} `` | `:1793-1799` 的 Delete / Cancel |
| `:1812` PostCreateModal | `` {`Backup Created — ${config.name}`} `` | `:1816-1822` 的 Run now / Not now（注意这个弹窗的 overlay 用的是 `onNotNow` 而非 `onClose`，`Modal` 的 `onClose` 传 `onNotNow`） |
| `:1843` ResetPasswordModal | `` {`Re-enter Password — ${config.name}`} `` | `:1862-1868` 的 Submit / Cancel |
| `:1981` Check / Repair | `` {`Check / Repair — ${config.name}`} `` | `:2019-2032` 的 Run check / Stop / Repair / Close |

Check / Repair 那处要注意：它的动作行在**内容中间**（`:2019`），后面还有报告与状态说明。把整个 `.row` 块移到 `footer`，其后的内容留在 body 里——这些按钮全都是动作，归属 footer 是对的，而报告是要滚动阅读的内容。

- [ ] **Step 5: 删除 `modalStyles.ts`**

```bash
cd frontend && grep -rn "modalStyles\|overlayStyle\|panelStyle" src/
```

Expected: 无输出（退出码 1）。确认无引用后：

```bash
cd frontend && git rm src/components/modalStyles.ts
```

- [ ] **Step 6: 确认 8 处都改完**

```bash
cd frontend && grep -rc "<Modal" src/components/PathBrowser.tsx src/components/RestoreDialog.tsx src/pages/AccountsPage.tsx src/pages/BackupConfigsPage.tsx
```

Expected: PathBrowser `1`、RestoreDialog `1`、AccountsPage `1`、BackupConfigsPage `5`。合计 8。

- [ ] **Step 7: 确认没有遗留的手写弹窗结构**

```bash
cd frontend && grep -rn 'marginTop: 0' src/ | grep h3
```

Expected: 无输出。有输出说明某处的 `<h3 style={{ marginTop: 0 }}>` 没有搬进 `Modal` 的 `title`。

- [ ] **Step 8: 验证构建与静态检查**

```bash
cd frontend && npx tsc -b && npm run build && npm run lint
```

Expected: 全部退出码 0。这一步是本任务的主要闸门——8 处 JSX 大改，闭合标签或 props 名写错都会在这里暴露。

- [ ] **Step 9: 提交**

```bash
cd /home/scegg/Sources/AzureStorageBackup
git add -A frontend/src
git commit -m "refactor(ui): move all eight dialogs onto the Modal shell

Each one keeps its own content and buttons; what they stop doing is
hand-rolling the overlay, the panel, the title and the button row. On a
phone they now open as full-screen panels whose footer stays put.

The folder picker asks for the secondary layer because the restore dialog
opens it on top of itself — two overlays at the same z-index were only
ever working by DOM order.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 8: 新建备份向导的 sticky 动作栏

向导是内联面板 `<div className="panel">`（`BackupConfigsPage.tsx:684`），不是弹窗，拿不到 Task 6 的全屏三段结构。它的表单很长，手机上要滚过整屏才能摸到"下一步"。

**Files:**
- Modify: `frontend/src/index.css`（手机层块内追加）
- Modify: `frontend/src/pages/BackupConfigsPage.tsx:865`（Step 1 的 Next）与 `:1098`（Step 2 的 Back / Save）

- [ ] **Step 1: sticky 动作栏样式**

在 `frontend/src/index.css` 的 `@media (max-width: 640px)` 块内追加：

```css
  /* 内联表单（新建备份向导）的动作栏。它不是弹窗，拿不到 .modal-footer 的固定位置，
     用 sticky 达到同样效果：按钮始终贴在视口底部，而不是躺在表单末尾等人滚过去。 */
  .form-actions {
    position: sticky;
    /* 让开底部导航栏，否则两条栏叠在一起。52px 与 .nav-item 的 min-height 一致。 */
    bottom: calc(52px + env(safe-area-inset-bottom));
    z-index: 30; /* 低于 .sidebar 的 40 */
    margin: var(--sp-4) calc(-1 * var(--sp-4)) 0;
    padding: var(--sp-3) var(--sp-4);
    /* 背景与上边框不能省：sticky 元素下面的表单内容会从它背后透上来。 */
    background: var(--bg-raised);
    border-top: 1px solid var(--border);
  }
  .form-actions > button {
    flex: 1 1 auto;
    min-width: 120px;
  }
```

`.form-actions` 在桌面层不出现任何规则——它在那里只是一个普通的 `.row`，外观不变。

- [ ] **Step 2: Step 1 的按钮行加类**

`frontend/src/pages/BackupConfigsPage.tsx:865` 附近包着"Next"按钮的那个 `<div className="row" …>`，把 class 改成两个：

```tsx
                <div className="row form-actions" ...>
```

保留它原有的 `style`（如果有）。

- [ ] **Step 3: Step 2 的按钮行加类**

同样处理 `:1098` 附近包着 Back / Save 按钮的 `<div className="row" …>`：

```tsx
                <div className="row form-actions" ...>
```

- [ ] **Step 4: 确认两处都加上了**

```bash
cd frontend && grep -c 'row form-actions' src/pages/BackupConfigsPage.tsx
```

Expected: `2`

- [ ] **Step 5: 验证构建与静态检查**

```bash
cd frontend && npx tsc -b && npm run build && npm run lint
```

Expected: 全部退出码 0。

- [ ] **Step 6: 提交**

```bash
cd /home/scegg/Sources/AzureStorageBackup
git add frontend/src/index.css frontend/src/pages/BackupConfigsPage.tsx
git commit -m "feat(ui): pin the wizard's Next and Save to the bottom on phones

The new-backup form is an inline panel, not a dialog, so it cannot borrow
the modal footer. Sticky gets to the same place: the buttons ride the
bottom of the viewport instead of lying at the end of a form you have to
scroll a full screen to reach. It sits above the nav bar, not on it.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 9: 逐页触屏细节

收尾：把剩下几个小命中区与固定宽度处理掉。

**Files:**
- Modify: `frontend/src/components/PathBrowser.tsx`（目录项）
- Modify: `frontend/src/components/RestoreDialog.tsx:454`（树形展开三角）
- Modify: `frontend/src/pages/NotificationsPage.tsx:121`
- Modify: `frontend/src/index.css`

- [ ] **Step 1: PathBrowser 目录项改成整行可点**

在 `frontend/src/index.css` 的 `── Blocks ──` 段末尾（`.stack` 规则之后）新增：

```css
/* 目录选择器的一行。原先是个 .btn-ghost，高 32px、左右各 8px 内边距，
   手机上要瞄准才点得中；整行可点就没有瞄准这回事了。 */
.browse-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--sp-2);
  width: 100%;
  min-height: var(--control-h);
  height: auto;
  padding: var(--sp-2) var(--sp-3);
  border: none;
  border-radius: var(--r-md);
  background: transparent;
  color: var(--text);
  text-align: left;
  white-space: normal;
  word-break: break-all;
}
.browse-row:hover:not(:disabled) {
  background: var(--bg-hover);
}
.browse-row::after {
  content: '›';
  color: var(--text-muted);
  flex: none;
}
.browse-row:disabled::after {
  content: none;
}
```

`min-height: var(--control-h)` 让它在触屏下自动拿到 44px（Task 1 已把该变量在 `pointer: coarse` 下提到 44px）。

- [ ] **Step 2: PathBrowser 用上新样式**

`frontend/src/components/PathBrowser.tsx` 里目录项的按钮（Task 7 改造后位于目录列表内），把 `className="btn-ghost"` 改为 `className="browse-row"`：

```tsx
                <button
                  type="button"
                  className="browse-row"
                  disabled={e.outsideRoot}
                  title={e.outsideRoot ? 'Outside the configured root' : undefined}
                  onClick={() => setPath(e.fullPath)}
                >
                  {e.name}/
                </button>
```

`.. (up)` 那个按钮同样改为 `className="browse-row"`。

包着每个条目的 `<div key={e.fullPath} style={{ padding: '0.15rem 0' }}>` 的 `style` 删掉——`.browse-row` 自己有内边距，再叠一层会让行高不一致。

文件项（`<span className="text-faint">`）不动：它本来就不可点。

- [ ] **Step 3: RestoreDialog 展开三角扩命中区**

`frontend/src/components/RestoreDialog.tsx:454` 的元素带 `style={{ width: 18, cursor: ... }}`。给它加上 Task 1 定义的 `hit-target` 类：

```tsx
                    className="hit-target"
                    style={{ width: 18, cursor: node.hasChildren ? 'pointer' : 'default' }}
```

如果该元素已有 `className`，追加而不是替换。`:472` 的占位 `<span style={{ width: 18, display: 'inline-block' }} />` 不动——它不可点，不需要命中区。

- [ ] **Step 4: NotificationsPage 事件多选改响应式**

`frontend/src/pages/NotificationsPage.tsx:121` 现在是：

```tsx
            <label key={e.bit} className="row" style={{ width: 200 }}>
```

固定 200px 在小屏上会横向溢出。改为：

```tsx
            <label key={e.bit} className="row notify-event">
```

并在 `frontend/src/index.css` 的 `── Blocks ──` 段末尾新增：

```css
/* 事件多选项：桌面上排成 200px 的多列，窄屏上自然掉成单列。 */
.notify-event {
  flex: 1 1 200px;
  min-width: 0;
  max-width: 260px;
}
```

- [ ] **Step 5: 核对 CronEditor 已无需额外处理**

CronEditor 的两个布局容器在前面的任务里已经处理完：简单模式（`CronEditor.tsx:60`）本来就带 `flexWrap: 'wrap'`，窄屏会自然折行；高级模式（`:45`）由 Task 1 Step 5b 补上了 `flexWrap`。三个 `.w-sm` 数字框保持 160px，`at [__] h` 这类行内结构不会散架。

跑一遍确认两个容器都能折行：

```bash
cd frontend && grep -c "flexWrap: 'wrap'" src/components/CronEditor.tsx
```

Expected: `2`。若是 `1`，说明 Task 1 Step 5b 没做——回去补上，否则高级模式的 Simple 按钮会被占满宽度的输入框挤出可视区。

- [ ] **Step 6: 确认三处固定宽度都清掉了**

```bash
cd frontend && grep -rn "width: 200\|width: 340\|maxHeight: 320" src/
```

Expected: 无输出（退出码 1）。`width: 340` 与 `maxHeight: 320` 由 Task 7 处理，`width: 200` 由本任务 Step 4 处理——若此时还有残留，说明前面漏了。

- [ ] **Step 7: 验证构建与静态检查**

```bash
cd frontend && npx tsc -b && npm run build && npm run lint
```

Expected: 全部退出码 0。

- [ ] **Step 8: 全量核对设计文档的清单**

对照 `docs/mobile-adaptation-design.md` §7 逐项确认已处理。然后跑一次完整检查：

```bash
cd frontend && npx tsc -b && npm run build && npm run lint && cd .. && git status --short
```

Expected: 三条命令退出码 0；`git status` 只列出本任务修改的文件，没有意外的改动或未跟踪文件。

- [ ] **Step 9: 提交**

```bash
cd /home/scegg/Sources/AzureStorageBackup
git add frontend/src
git commit -m "feat(ui): make the last small targets big enough to hit with a thumb

The folder picker's rows were 32px buttons with 8px of padding — you had
to aim. Whole-row targets remove the aiming. The restore tree's 18px
disclosure triangle keeps its size and borrows an invisible 44px hit box.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

## 完成后

全部 9 个任务提交后：

1. 按 `docs/CLAUDE.md` 与项目记忆的约定，**合并到 main 并删除分支**（仓库只留 main 一条线）。
2. 告诉用户：真机验证需要他在手机上过一遍，重点是四处——底部导航是否被系统手势条遮挡、Backups 卡片里的按钮是否好点、新建备份向导的"下一步"是否常驻可见、还原对话框里打开目录选择器后能否正常返回。这是本轮**唯一**无法由构建验证的部分（见 Global Constraints 关于测试设施的说明）。

## 任务依赖

```
Task 1（基础）
 ├─→ Task 2（底部栏，用到 --control-h 与安全区）
 ├─→ Task 3（横滚，独立）
 ├─→ Task 4（Backups 卡片化）─→ Task 5（Tasks 卡片化，复用 Task 4 的 CSS）
 ├─→ Task 6（Modal 组件）─→ Task 7（8 处接入）─→ Task 9 的 Step 1-3（PathBrowser 依赖 Task 7 改造后的结构）
 └─→ Task 8（向导 sticky，bottom 值依赖 Task 2 的 52px 导航栏高度）
```

Task 3 与 Task 6 之间无依赖，可以并行。Task 5 必须在 Task 4 之后（复用 `table.cards`）。Task 8 必须在 Task 2 之后（sticky 的 `bottom` 要让开导航栏）。Task 9 必须在 Task 7 之后（PathBrowser 的结构已被 Task 7 改过）。
