# 手机端适配：触屏与小尺寸屏幕（2026-07-31）

> 界面目前只有一个 900px 断点（`index.css:371/565/674`），做了侧栏塌成顶部 tab 条、`.field` 变单列、弹窗全宽三件事。这些是**窄屏**适配，**触屏**适配基本没做：控件高 32px（低于 44px 触摸目标）、输入框 14px（iOS Safari 聚焦时会自动放大整页）、8 张表格没有横向滚动容器、多处交互只挂了 `:hover`。
>
> 本轮目标是**手机上完整可操作**——包括新建/编辑备份配置的两步向导、还原对话框的目录树选择，而不是只保证"能看状态"。
>
> 本轮**不改任何业务逻辑与交互流程**，只改布局、命中区域与弹窗结构。

## 1. 设计决策（本轮锁定）

| # | 决策点 | 结论 |
|---|--------|------|
| 1 | 适配目标 | **完整可操作**。所有页面、所有弹窗在手机上都能正常完成操作，不留"这个功能得回电脑上做"的缺口 |
| 2 | 断点策略 | **尺寸轴与输入轴正交**。`max-width` 管布局（900px 平板层保留 + 新增 640px 手机层），`pointer: coarse` 管命中区域。桌面鼠标用户零可见变化 |
| 3 | 表格 | **主表卡片化，次表横滚**。Backups / Tasks 在手机层每行变一张卡片；Logs / Accounts / Containers / Groups / 还原版本表包一层横向滚动 |
| 4 | 弹窗 | **手机层全屏面板**。顶部固定标题栏、底部固定动作栏、中间内容区自己滚 |
| 5 | 弹窗结构 | **统一为 header/body/footer 三段**，不只在手机层打补丁。桌面端外观不变 |
| 6 | 主导航 | **手机层底部固定 tab 栏**。`Log out` 迁到 Settings 页，桌面端 sidebar 也一并去掉，两端一致 |
| 7 | 样式技术 | **沿用现有约定**：零运行时依赖、手写 CSS、全局语义化 class。`package.json` 不新增任何依赖 |
| 8 | 前端测试 | **本轮不引入前端测试框架**。理由与验证方式见 §8 |

## 2. 断点策略

现有的 900px 断点同时承担了两件不同的事：布局重排（该按**尺寸**决定）和交互尺度（该按**输入方式**决定）。混在一起会导致两个方向的错误：桌面用户缩窄窗口时按钮突然变粗大；1000px 宽的触屏平板拿不到大命中区。

拆成两条正交的轴：

```
尺寸轴（max-width）           输入轴（pointer: coarse）
├─ >900px  桌面：侧栏 + 表格   ├─ fine（鼠标）  32px 控件，保留 hover
├─ ≤900px  平板：顶部条 + 单列   └─ coarse（触屏）44px 控件，关掉 hover 粘滞
└─ ≤640px  手机：底部栏 + 卡片 + 全屏弹窗
```

640px 的取舍：iPhone 15 Pro Max 横屏（932px）会落在平板层而非手机层。这是刻意的——横屏下宽度足够，表格形态比卡片形态更有效率。

`pointer: coarse` 在所有现代浏览器可用；不支持时退化为不生效，得到当前行为，无回归风险。

## 3. 触摸与输入基础（`index.css`）

### 3.1 控件尺寸

`--control-h` 从 32px 提到 44px，**只在 `pointer: coarse` 下**：

```css
@media (pointer: coarse) {
  :root { --control-h: 44px; }
}
```

因为全部控件都引用这个变量（设计系统的既有约定），一处改动即全局生效。

### 3.2 iOS 自动缩放

手机层把 `input / select / textarea` 的 `font-size` 提到 **16px**。低于 16px 时 iOS Safari 在聚焦输入框的瞬间会放大整个页面，**且失焦后不会缩回**——用户此后看到的是一个被放大、需要手动双指缩小的界面。这是必须修的硬伤，不是观感问题。

正文其余部分保持 14px：只有获得焦点的表单控件会触发这个行为。

### 3.3 小命中区

以下元素视觉尺寸必须保持不变（放大会破坏紧凑控制台的观感），但命中区要扩到 44px。用伪元素实现——伪元素不参与布局，因此不会推开周围内容：

```css
@media (pointer: coarse) {
  .icon-btn { position: relative; }   /* 伪元素的定位基准 */
  .icon-btn::after {
    content: '';
    position: absolute;
    inset: 50% auto auto 50%;
    width: 44px; height: 44px;
    transform: translate(-50%, -50%);
  }
}
```

宿主元素必须是 `position: relative`，否则伪元素会相对更外层的定位祖先摊开，命中区落在错误的位置。

需要处理的：`.icon-btn`（`index.css:186`，折叠三角）、还原对话框树形展开三角（`RestoreDialog.tsx:454`，现在宽 18px）、表格行内的 `.btn-ghost` 按钮。

### 3.4 hover 与 active

触屏上 `:hover` 会在点击后**粘住不消失**，直到点别处——表格行会留下一片高亮，看着像"选中了"。

- `pointer: coarse` 下关掉 `tbody tr:hover`（`index.css:271`）的背景变化
- 所有目前只有 `:hover` 反馈的元素补 `:active`，触屏才有按下反馈
- 设 `-webkit-tap-highlight-color: transparent`，避免系统默认的蓝色方块盖住自定义的 `:active`

### 3.5 安全区

`index.html:6` 的 viewport 加 `viewport-fit=cover`，底部导航栏与全屏弹窗的底部动作栏吃 `env(safe-area-inset-bottom)`——否则 iPhone 的下巴横条会盖住按钮。

### 3.6 固定宽度

`.w-md`（280px）与 `.w-lg`（480px）在手机层变 `width: 100%`。

`.w-sm`（160px）**不动**：这一档全是数字框——代理端口、版本数、保留天数，以及计划编辑器里 `at [__] h`、`min [__]` 这些夹在文字中间的输入框（`CronEditor.tsx:87/101/115`）。让它们占满一行既难看，又会把那两个标签挤散。160px 在最窄的 320px 屏上也放得下。

三处内联固定宽度另行处理：`NotificationsPage.tsx:121`（`width: 200`）、`RestoreDialog.tsx:277`（`width: 340`）、PathBrowser 目录列表的 `maxHeight: 320`（后者在弹窗改用 `.modal-body` 滚动后会造成嵌套滚动条，一并删掉）。

## 4. 主导航：手机层底部 tab 栏

`.app-shell` 在 ≤640px 改为 `grid-template-rows: 1fr auto`，用 `order` 把 `.sidebar` 排到内容之后，`position: fixed` 钉在底部。`.sidebar-brand` 隐藏（标题在页面里已有），`.sidebar-nav` 四等分铺满。

内容区加 `padding-bottom`，值为底部栏高度 + 安全区，否则最后一行内容会被永久遮住。

### 4.1 `Log out` 的去处

底部栏只有 4 格，容不下第 5 个入口。把 `Log out` 从 `App.tsx:72-86` 的 `.sidebar-footer` 迁到 `SettingsPage`。

桌面端 sidebar 的 `Log out` **也一并去掉**，不做"手机上在 Settings、桌面上在侧栏"的分叉——同一个功能两个位置，是后续维护里最容易忘记同步的那种东西。

这需要把 `App.tsx` 里的登出逻辑（含"无论服务端成功与否都清本地状态"那条注释记录的安全考量，`App.tsx:75-77`）连同 `auth.required` 判断一起下传给 `SettingsPage`。逻辑本身原样搬，不改。

## 5. 表格

### 5.1 卡片化（Backups / Tasks）

手机层把 `table / thead / tbody / tr / td` 转成 block，隐藏 `thead`，每个 `tr` 成为一张带边框的卡片，`td` 内用 `::before { content: attr(data-label) }` 在左侧显示字段名：

```
┌──────────────────────────────┐
│ Photos                       │  ← 首列（名称）放大加粗，不显示标签
│ Account/Ctn  acct1 / photos  │
│ Local Root   /volume1/photo  │
│ Encrypted    Yes             │
│ Status       ● Idle          │
│ ──────────────────────────── │
│ [Back up] [Restore] [⋯]      │  ← 操作列，data-label 留空
└──────────────────────────────┘
```

CSS 拿不到表头文字，所以要给这两张表的 `<td>` 补 `data-label` 属性——这是本节在 JSX 侧的全部改动（`BackupConfigsPage.tsx:560` 起、`TasksPage.tsx:153` 起）。

### 5.2 运行状态行（`.ops-row`）

Backups 表把运行中的备份/还原/修复/检查状态单独放一行、跨全部列（`BackupConfigsPage.tsx:585` 起，`index.css:275-289`）。这个设计的原因记录在案：操作列是 `nowrap` 的，几百字符的路径放进去会把表撑到出屏。

卡片化后它自然接在卡片底部。但现有的"与上一行之间不划线"规则依赖 `tr.has-ops + tr.ops-row` 的相邻关系与表格边框模型，在 block 布局下要重写：手机层让 `.has-ops` 卡片去掉下边框、`.ops-row` 去掉上边框并与前者视觉合并成一张卡。

### 5.3 横向滚动（其余 6 张表）

Logs（`LogsPage.tsx:103`）、Accounts（`AccountsPage.tsx:177`）、Containers（`ContainersPage.tsx:94`）、Groups（`GroupsPage.tsx:109`）、还原版本表（`RestoreDialog.tsx:323`）、`BackupConfigsPage.tsx:2100` 的明细表包一层：

```css
.table-scroll { overflow-x: auto; -webkit-overflow-scrolling: touch; }
```

容器加 `tabindex="0"`，否则只能靠触摸滑动，键盘用户无法滚动溢出内容（WCAG 2.1.1）。

Logs 不卡片化的理由："时间/级别/来源/消息"是扫读型日志流，卡片化后一屏看不了几条，比横滚更难用。

## 6. 弹窗：手机层全屏

### 6.1 现状

`modalStyles.ts` 只是 `.modal-overlay` / `.modal-panel` 的常量别名，**所有弹窗共用同一套 CSS**（`index.css:646-679`）。这让全屏化的 CSS 部分成本很低。

### 6.2 三段结构

给弹窗补统一结构，桌面端外观不变：

```
.modal-panel
├─ .modal-header   标题 + 关闭按钮
├─ .modal-body     内容，overflow-y: auto
└─ .modal-footer   动作按钮
```

手机层：

```css
@media (max-width: 640px) {
  .modal-overlay { padding: 0; }
  .modal-panel {
    width: 100vw; height: 100dvh;
    max-width: none; max-height: none;
    border: none; border-radius: 0; padding: 0;
    display: grid; grid-template-rows: auto 1fr auto;
  }
  .modal-footer { padding-bottom: calc(var(--sp-3) + env(safe-area-inset-bottom)); }
}
```

用 `dvh` 而非 `vh`：手机浏览器地址栏收缩会改变 `vh` 的解析值，用 `vh` 会导致底部动作栏在滚动过程中跳动或被地址栏盖住。

### 6.3 嵌套弹窗的层级

还原对话框会在自身之上再开 PathBrowser（`RestoreDialog.tsx:282`）。`.modal-overlay` 现在是固定的 `z-index: 50`（`index.css:650`），两层叠在一起时靠 DOM 顺序侥幸生效——桌面端因为两个面板尺寸不同、能看见下面那层的边缘，问题不明显；手机层两层都是全屏，一旦顺序不对就是"点了 Browse 什么也没发生"。

改为让嵌套层显式提升：PathBrowser 作为二级弹窗使用 `z-index: 60`。同时保留其 `onClick={onClose}` 的遮罩点击关闭行为——手机全屏下遮罩不可见，关闭要靠标题栏的 `✕`，这正是 §6.2 三段结构里 header 关闭按钮的必要性所在。

### 6.4 需要改造的弹窗

全部 8 处弹窗共用 `.modal-overlay` / `.modal-panel`。目前动作按钮都是散在内容末尾的 `.row`，要包进 `.modal-footer`：

| # | 弹窗 | 位置 |
|---|---|---|
| 1 | 目录选择器 PathBrowser | `PathBrowser.tsx:38` |
| 2 | 账户表单 | `AccountsPage.tsx:382` |
| 3 | 还原对话框 | `RestoreDialog.tsx:273` |
| 4 | 末次错误 ErrorModal | `BackupConfigsPage.tsx:1552` |
| 5 | 删除备份确认 | `BackupConfigsPage.tsx:1774` |
| 6 | 创建完成 PostCreateModal | `BackupConfigsPage.tsx:1812` |
| 7 | 重输密码 ResetPasswordModal | `BackupConfigsPage.tsx:1843` |
| 8 | 检查/修复 | `BackupConfigsPage.tsx:1981` |

`components/modal.tsx` **不是**弹窗组件——它导出的是表单字段行 `Field`，不在本节范围内。

### 6.4.1 新建/编辑备份向导：不是弹窗

向导是内联面板 `<div className="panel">`（`BackupConfigsPage.tsx:684`），展开在表格下方，随页面一起滚动。它拿不到 §6.2 的全屏三段结构。

但它恰恰是"完整可操作"目标里最关键的一处：Step 1 的表单很长，手机上要滚过整屏才能摸到底部的"下一步"（`:865`）和"上一步 / 保存"（`:1098`）。

处理方式：把这两组按钮包进 `.form-actions`，手机层 `position: sticky; bottom: 0` 钉在视口底部，加背景色与上边框以免内容透上来。`bottom` 值要让开底部导航栏的高度（§4），否则两条栏会叠在一起。

选 sticky 而不是把向导改成弹窗：后者要动状态管理与 `showForm` 的渲染位置，风险远大于收益，而按钮常驻可见的效果是一样的。

### 6.5 背景滚动锁定

弹窗打开时锁住 body 滚动。手机上"滚透"（滚动弹窗内容到底后继续滑动会带动背后页面）很常见，会让人以为弹窗关掉了。

实现：在 `modalStyles.ts` 旁新增一个 `useModalScrollLock()` hook——挂载时给 `body` 加 `overflow: hidden` 并记录当前滚动位置，卸载时恢复两者。每个弹窗组件调用一次这个 hook，锁定逻辑只写一份。

**hook 必须带引用计数**：还原对话框会在自身之上再开 PathBrowser（`RestoreDialog.tsx:282`）。没有计数的话，内层 PathBrowser 关闭时会把 `body` 的 `overflow` 恢复掉，而外层还原对话框还开着，背景又能滚了。用模块级计数器：归零时才恢复。

## 7. 逐页触屏细节

**PathBrowser**（`PathBrowser.tsx:56-70`）：目录项现在是 `btn-ghost`，高 32px、左右内边距 8px，手机上很难点中。改成整行可点、最小高 44px、右侧带 `›` 指示。文件项（不可点）保持现状。越界项的灰显与 `title` 提示不变——`title` 在触屏上不显示，但它本来就只是补充信息，可点性已由 `disabled` 表达。

**RestoreDialog**：树形展开三角（`:454`）命中区扩到 44px；`:277` 的 `width: 340` 输入框改响应式；版本表包横滚。

**CronEditor**（`CronEditor.tsx`）：不需要纵向堆叠。简单模式的容器（`:60`）本来就带 `flexWrap: 'wrap'`，三个数字框保持 160px 后会自然折行；只有高级模式的容器（`:45`）缺 `flexWrap`——那里的 `.w-md` 输入框占满宽度后会把 Simple 按钮挤出去，补上折行即可。

**NotificationsPage**（`:121`）：`width: 200` 的事件多选行改为响应式栅格，手机层单列。

**`.field`**：已在 900px 变单列（`index.css:565`），无需改动。

## 8. 验证

前端**没有任何测试设施**——`package.json` 的 scripts 只有 `dev` / `build` / `lint` / `preview`，无 vitest、无 playwright。

本轮验证方式：

1. `npx tsc -b` 与 `npm run build` 必须通过
2. `npm run lint`（oxlint）必须通过
3. 逐页代码审查，对照本文档的清单核对
4. **真机效果由用户在手机上实测**

不引入 Playwright 的理由：一套视口截图回归测试需要持续维护基线图片，对一个单用户工具的收益不足以抵消成本。这是**已知局限**，与 `web-ui-modernization-design.md` §1 第 8 条的判断一致。

## 9. 不做的事

- 不改任何业务逻辑、API、数据模型
- 不引入 CSS 框架或组件库
- 不做手势交互（滑动删除、下拉刷新）——它们需要额外的可发现性设计，且都有等价的显式按钮
- 不做 PWA / 离线支持
- 不做手动的浅色/深色切换开关（沿用跟随系统）
