import { useEffect, useRef, useState } from 'react'
import { browseApi, type BrowseEntry } from '../api/browse'
import { isInScope, scopeState, withRule, type ScopeRules } from '../lib/scopeRules'

const PAGE_SIZE = 500

interface Loaded {
  entries: BrowseEntry[]
  total: number
  // 服务端在 stat 之前的全量列表上分页，不是在 entries 上分页：一页里可能有几项因读不出
  // 属性而被跳过（不进 entries，但仍消耗一个位置）。下一页的 offset 必须算上它们，
  // 否则会把被跳过的那几项重新请求一遍——重复的行、重复的 React key，Load more 也会
  // 因为「以为只吃了 entries.length 项」而多按了几次才消失。
  consumed: number
  // 累计有多少项因读不出属性而未列出（例如目录 mode 为 r--：可 readdir、不可 stat 子项）。
  // 少给了东西必须说出来（同 PathBrowser 的 skipped 提示），否则这些项从列表里凭空消失，
  // 用户会把「读不出来」误当成「本来就没有」。
  skipped: number
  loading: boolean
  error: string | null
}

/**
 * 备份范围选择树（设计 docs/backup-scope-selection-design.md §8）。
 *
 * 刻意**不复用** RestoreDialog 那棵树：那棵的数据源是云端版本索引（有限已知全集，三态靠
 * 数已加载的后代文件算），这棵是活的文件系统（无限、会变，三态靠规则集算）。两者只有外观
 * 像，内核相反，合并只会让两边都变脆。
 *
 * 状态只有三份，真相只有一份：rules 是唯一真相，children/expanded 纯展示。
 * 节点的勾选状态**永远从 rules 现算、不存** —— 因此点击只写一条规则就结束，没有
 * 「子改父 → 父改子」的传播回路，不可能死循环。
 */
export function ScopeTree({
  localRoot,
  rules,
  onChange,
  ignoreRules,
}: {
  localRoot: string
  rules: ScopeRules
  onChange: (next: ScopeRules) => void
  ignoreRules: string
}) {
  // key 是**相对 localRoot** 的路径（根为空串），与规则集同一套坐标。
  const [children, setChildren] = useState<Record<string, Loaded>>({})
  const [expanded, setExpanded] = useState<Set<string>>(new Set(['']))

  const absolute = (relative: string) =>
    relative.length === 0 ? localRoot : `${localRoot.replace(/\/+$/, '')}/${relative}`

  const load = async (relative: string, offset: number) => {
    setChildren((c) => ({
      ...c,
      [relative]: {
        entries: c[relative]?.entries ?? [],
        total: c[relative]?.total ?? 0,
        consumed: c[relative]?.consumed ?? 0,
        skipped: c[relative]?.skipped ?? 0,
        loading: true,
        error: null,
      },
    }))
    try {
      const page = await browseApi.list(absolute(relative), undefined, { offset, limit: PAGE_SIZE })
      setChildren((c) => {
        // offset === 0 是一次替换性的（首次加载或 Retry）：连累计的 consumed/skipped 也要
        // 跟着重新起算，否则 Retry 之后这两个数会把上一次失败前的旧值也算进去。
        const prevConsumed = offset === 0 ? 0 : (c[relative]?.consumed ?? 0)
        const prevSkipped = offset === 0 ? 0 : (c[relative]?.skipped ?? 0)
        return {
          ...c,
          [relative]: {
            // 追加而不是替换：Load more 不能把已经看到的项抖掉。
            entries: offset === 0 ? page.entries : [...(c[relative]?.entries ?? []), ...page.entries],
            total: page.total,
            consumed: prevConsumed + page.entries.length + page.skipped,
            skipped: prevSkipped + page.skipped,
            loading: false,
            error: null,
          },
        }
      })
    } catch (e) {
      setChildren((c) => ({
        ...c,
        [relative]: {
          entries: c[relative]?.entries ?? [],
          total: c[relative]?.total ?? 0,
          consumed: c[relative]?.consumed ?? 0,
          skipped: c[relative]?.skipped ?? 0,
          loading: false,
          error: e instanceof Error ? e.message : String(e),
        },
      }))
    }
  }

  // load 每渲染都是新引用；若直接进依赖数组，effect 会在每次 children 变化（也就是每次
  // 加载完成）后又跑一遍，与"只在 localRoot 出现时加载一次根目录"的本意不符——用 ref 拿
  // 最新版本，依赖里只留真正决定要不要重新加载的 localRoot（做法同 RestoreDialog 的 onErrorRef）。
  const loadRef = useRef(load)
  loadRef.current = load

  // 根目录一进来就展开：第一层只有一个节点，就是本地根自己。
  useEffect(() => {
    if (localRoot) void loadRef.current('', 0)
    // localRoot 创建后锁定，实际不会变；列出来是为了让依赖诚实。
  }, [localRoot])

  const toggleExpand = (relative: string) => {
    // 是否要发起加载，从本次渲染已有的 children 现算，不放进 setExpanded 的
    // 更新函数里判断——StrictMode 下更新函数会被调用两次（一次探测一次生效），
    // 若在里面调 load 会在开发环境里并发打两次同一个目录的 GET。
    const isExpanding = !expanded.has(relative)
    // 没有 children[relative]，或者上次加载失败（entry.error 非空且从未成功过），
    // 都算「还没有可用数据」，都要发起加载——否则一次失败就把这个目录锁死，
    // 用户再也点不动它（失败也会写入 children，之前的判断把它当成"已加载"）。
    const needsLoad = isExpanding && (!children[relative] || children[relative].error !== null)

    setExpanded((prev) => {
      const next = new Set(prev)
      if (next.has(relative)) {
        next.delete(relative)
      } else {
        next.add(relative)
      }
      return next
    })

    if (needsLoad) void load(relative, 0)
  }

  return (
    <div style={{ border: '1px solid var(--border)', padding: 'var(--sp-2)', maxHeight: '22rem', overflowY: 'auto' }}>
      <Row
        name={localRoot || '(local root)'}
        relative=""
        isDir
        rules={rules}
        onChange={onChange}
        expanded={expanded}
        onToggleExpand={toggleExpand}
        depth={0}
        ignored={false}
        outsideRoot={false}
        length={null}
      />
      {expanded.has('') && (
        <Level
          relative=""
          depth={1}
          children_={children}
          expanded={expanded}
          onToggleExpand={toggleExpand}
          onLoadMore={load}
          rules={rules}
          onChange={onChange}
          ignoreRules={ignoreRules}
        />
      )}
    </div>
  )
}

function Level({
  relative,
  depth,
  children_,
  expanded,
  onToggleExpand,
  onLoadMore,
  rules,
  onChange,
  ignoreRules,
}: {
  relative: string
  depth: number
  children_: Record<string, Loaded>
  expanded: Set<string>
  onToggleExpand: (relative: string) => void
  onLoadMore: (relative: string, offset: number) => void
  rules: ScopeRules
  onChange: (next: ScopeRules) => void
  ignoreRules: string
}) {
  const state = children_[relative]
  const pad = { paddingLeft: depth * 16 }

  if (!state) return null
  if (state.error) {
    return (
      <div className="text-warn text-sm" style={pad}>
        Could not be read — {state.error}{' '}
        <button type="button" className="text-sm" onClick={() => onLoadMore(relative, 0)}>
          Retry
        </button>
      </div>
    )
  }
  if (state.loading && state.entries.length === 0 && state.skipped === 0) {
    return (
      <div className="text-faint text-sm" style={pad}>
        Loading…
      </div>
    )
  }
  // total 是服务端 stat 之前的全量子项数：真空目录时它恒为 0（skipped 也必然是 0，
  // 无项可跳过）。一页恰好把能读的都跳过、下一页还有更多要拉的情形，靠这个条件
  // 与"Empty"分开——那种情况下面的分支会照常渲染 skipped 提示与 Load more。
  if (!state.loading && state.total === 0) {
    return (
      <div className="text-faint text-sm" style={pad}>
        Empty
      </div>
    )
  }

  return (
    <>
      {state.entries.map((e) => {
        const childRelative = relative.length === 0 ? e.name : `${relative}/${e.name}`
        return (
          <div key={e.fullPath}>
            <Row
              name={e.name}
              relative={childRelative}
              isDir={e.isDirectory}
              rules={rules}
              onChange={onChange}
              expanded={expanded}
              onToggleExpand={onToggleExpand}
              depth={depth}
              ignored={matchesIgnore(childRelative, e.isDirectory, ignoreRules)}
              outsideRoot={e.outsideRoot}
              length={e.length}
            />
            {e.isDirectory && expanded.has(childRelative) && (
              <Level
                relative={childRelative}
                depth={depth + 1}
                children_={children_}
                expanded={expanded}
                onToggleExpand={onToggleExpand}
                onLoadMore={onLoadMore}
                rules={rules}
                onChange={onChange}
                ignoreRules={ignoreRules}
              />
            )}
          </div>
        )
      })}
      {/* 少给了东西必须说出来（同 PathBrowser 的 skipped 提示）：这些子项因属性读不出来
          而没有出现在上面的列表里，不代表它们不存在。 */}
      {state.skipped > 0 && (
        <div className="text-warn text-sm" style={pad}>
          {state.skipped} item(s) could not be read and are not listed.
        </div>
      )}
      {state.consumed < state.total && (
        <div style={pad}>
          <button
            type="button"
            className="text-sm"
            disabled={state.loading}
            // 服务端在 stat 之前的全量列表上分页：下一页要从"这一页真正消耗掉的位置"续，
            // 那包含被跳过的项，不只是成功 stat 出来的 entries.length。
            onClick={() => onLoadMore(relative, state.consumed)}
          >
            {state.loading
              ? 'Loading…'
              : `Load more (showing ${state.entries.length.toLocaleString()} of ${state.total.toLocaleString()})`}
          </button>
        </div>
      )}
    </>
  )
}

function Row({
  name,
  relative,
  isDir,
  rules,
  onChange,
  expanded,
  onToggleExpand,
  depth,
  ignored,
  outsideRoot,
  length,
}: {
  name: string
  relative: string
  isDir: boolean
  rules: ScopeRules
  onChange: (next: ScopeRules) => void
  expanded: Set<string>
  onToggleExpand: (relative: string) => void
  depth: number
  ignored: boolean
  outsideRoot: boolean
  length: number | null
}) {
  // 三态现算。这一行就是「不会死循环」的全部理由：读规则集，不读也不写任何兄弟/父子状态。
  const state = isDir ? scopeState(rules, relative) : isInScope(rules, relative) ? 'checked' : 'unchecked'

  return (
    <div className="row text-sm" style={{ paddingLeft: depth * 16 }}>
      {isDir ? (
        <button
          type="button"
          className="icon-btn hit-target"
          style={{ width: 18 }}
          onClick={() => onToggleExpand(relative)}
        >
          {expanded.has(relative) ? '▾' : '▸'}
        </button>
      ) : (
        <span style={{ width: 18, display: 'inline-block' }} />
      )}
      <input
        type="checkbox"
        checked={state === 'checked'}
        ref={(el) => {
          if (el) el.indeterminate = state === 'indeterminate'
        }}
        disabled={outsideRoot}
        // 点击只做一件事：写一条规则。父子状态在下一次渲染时各自现算。
        onChange={() => onChange(withRule(rules, relative, state !== 'checked'))}
      />
      <span>
        {isDir ? <strong>{name}</strong> : name}
        {isDir && '/'}
      </span>
      {length != null && (
        <span className="text-muted" style={{ marginLeft: 6 }}>
          {formatBytes(length)}
        </span>
      )}
      {ignored && (
        <span
          className="text-muted"
          style={{ marginLeft: 6 }}
          title="Matches this backup's ignore rules. It stays selectable and your choice is saved, but ignore rules are applied separately and will still leave it out of the backup."
        >
          ignored
        </span>
      )}
      {outsideRoot && (
        <span className="text-warn" style={{ marginLeft: 6 }}>
          outside root
        </span>
      )}
    </div>
  )
}

const formatBytes = (n: number): string => {
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let v = n
  let i = 0
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i++
  }
  return `${i === 0 ? v : v.toFixed(1)} ${units[i]}`
}

/**
 * 仅用于给行打 `ignored` 徽标 —— 是提示，不是判定。真正的忽略在备份时由后端的
 * IgnoreRuleSet 执行；这里只支持最常见的几种写法（目录后缀 /、`*.ext`、精确路径），
 * 不追求与后端逐字节一致。看不出 gitignore 全部语义是可以接受的，误报/漏报徽标不影响
 * 任何备份结果。
 */
function matchesIgnore(relative: string, isDir: boolean, ignoreRules: string): boolean {
  const name = relative.slice(relative.lastIndexOf('/') + 1)
  for (const raw of ignoreRules.split('\n')) {
    let p = raw.trim()
    if (p.length === 0 || p.startsWith('#') || p.startsWith('!')) continue

    let dirOnly = false
    if (p.endsWith('/')) {
      dirOnly = true
      p = p.slice(0, -1)
    }
    if (dirOnly && !isDir) continue
    p = p.replace(/^\//, '')

    if (p === relative || p === name) return true
    if (p.startsWith('*.') && name.endsWith(p.slice(1))) return true
  }
  return false
}
