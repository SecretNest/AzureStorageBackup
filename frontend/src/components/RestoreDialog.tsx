import { useEffect, useRef, useState } from 'react'
import {
  backupConfigsApi,
  RestoreConflictMode,
  restoreConflictModeLabels,
  RestoreRehydratePriority,
  type BackupConfig,
  type FileVersionOption,
  type RestoreEstimate,
  type RestoreRun,
  type TreeNode,
  type UnreadableEntry,
} from '../api/backupConfigs'
import { formatBytes } from '../constants/format'
import { Field } from './Field'
import { overlayStyle, panelStyle } from './modalStyles'
import { PathBrowser } from './PathBrowser'

const rehydratePriorityLabels: Record<number, string> = {
  [RestoreRehydratePriority.Standard]: 'Standard',
  [RestoreRehydratePriority.High]: 'High (faster, more expensive)',
}

// 还原对话框（§4.1a-d）：选版本 → 懒加载树浏览 + 勾选 → 防抖估算 → 冲突模式/活化优先级 → 目标根路径 → 开始还原。
// 同时保留原有的"不可恢复文件按版本替代"能力（§4.1，需求 unrecoverable substitution）。
export function RestoreDialog({
  config, onClose, onError, onStarted,
}: { config: BackupConfig; onClose: () => void; onError: (e: string) => void; onStarted: (s: RestoreRun) => void }) {
  const [versions, setVersions] = useState<number[]>([])
  const [version, setVersion] = useState<number | null>(null)
  const [target, setTarget] = useState(config.localRoot)
  const [browsing, setBrowsing] = useState(false)

  // 不可恢复文件的按版本替代（沿用既有能力）
  const [unrecoverable, setUnrecoverable] = useState<string[]>([])
  // 内容沿用自更早版本的文件：不需要"替代"（内容是有效的，只是旧），但还原前必须知道。
  const [stale, setStale] = useState<UnreadableEntry[]>([])
  const [options, setOptions] = useState<Record<string, FileVersionOption[]>>({})
  const [choices, setChoices] = useState<Record<string, number>>({})
  const [subsLoading, setSubsLoading] = useState(false)

  // 懒加载树浏览 + 勾选选择（§4.1a/d）
  const [treeCache, setTreeCache] = useState<Record<string, TreeNode[]>>({})
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [loadingDirs, setLoadingDirs] = useState<Set<string>>(new Set())
  const [cascading, setCascading] = useState<Set<string>>(new Set())
  const [selected, setSelected] = useState<Set<string>>(new Set())

  // 估算 + 冲突模式 + 活化优先级（§4.1b/c/d）
  const [estimate, setEstimate] = useState<RestoreEstimate | null>(null)
  const [estimating, setEstimating] = useState(false)
  const [conflict, setConflict] = useState<number>(RestoreConflictMode.OverwriteIfChanged)
  const [rehydratePriority, setRehydratePriority] = useState<number>(RestoreRehydratePriority.Standard)
  const [starting, setStarting] = useState(false)

  // onError 是父组件在渲染里现写的箭头函数，每渲染一次就换一个引用。它一旦进了下面这些
  // effect 的依赖数组，备份列表每隔几秒的例行刷新就会把 effect 重跑一遍——什么都不点，
  // 对话框也会自己闪：Loading… 冒出来又消失，替代表和沿用提示跟着重排，看着就像刚切了版本。
  // 用 ref 拿最新的回调，依赖里只留真正决定要重取什么的 config.id 与 version。
  const onErrorRef = useRef(onError)
  onErrorRef.current = onError

  useEffect(() => {
    backupConfigsApi.versions(config.id).then((vs) => setVersions(vs.map((v) => v.version))).catch(() => {})
  }, [config.id])

  useEffect(() => {
    let cancelled = false
    setSubsLoading(true)
    ;(async () => {
      try {
        const paths = await backupConfigsApi.unrecoverablePaths(config.id, version)
        if (cancelled) return
        setUnrecoverable(paths)
        setStale(await backupConfigsApi.unreadableEntries(config.id, version))
        if (cancelled) return
        const opts: Record<string, FileVersionOption[]> = {}
        const ch: Record<string, number> = {}
        for (const p of paths) {
          const cands = await backupConfigsApi.fileVersions(config.id, p)
          opts[p] = cands
          ch[p] = cands.length > 0 ? cands[0].version : 0 // 0 = skip
        }
        if (cancelled) return
        setOptions(opts)
        setChoices(ch)
      } catch (e) {
        if (!cancelled) onErrorRef.current(e instanceof Error ? e.message : String(e))
      } finally {
        if (!cancelled) setSubsLoading(false)
      }
    })()
    return () => { cancelled = true }
  }, [config.id, version])

  // 版本切换时重置树浏览状态并加载根目录（本地权威索引读，快，不触云）。
  useEffect(() => {
    setTreeCache({})
    setExpanded(new Set())
    setSelected(new Set())
    setEstimate(null)
    let cancelled = false
    setLoadingDirs((s) => new Set(s).add(''))
    ;(async () => {
      try {
        const kids = await backupConfigsApi.tree(config.id, version, null)
        if (!cancelled) setTreeCache((c) => ({ ...c, '': kids }))
      } catch (e) {
        if (!cancelled) onErrorRef.current(e instanceof Error ? e.message : String(e))
      } finally {
        if (!cancelled) {
          setLoadingDirs((s) => {
            const n = new Set(s)
            n.delete('')
            return n
          })
        }
      }
    })()
    return () => { cancelled = true }
  }, [config.id, version])

  // 勾选变化防抖 ~400ms 调 restoreEstimate；未选中任何项时视为"还原整版本"，不展示估算。
  useEffect(() => {
    if (selected.size === 0) {
      setEstimate(null)
      return
    }
    let cancelled = false
    const timer = setTimeout(() => {
      setEstimating(true)
      backupConfigsApi
        .restoreEstimate(config.id, version, Array.from(selected))
        .then((est) => {
          if (!cancelled) setEstimate(est)
        })
        .catch((e) => {
          if (!cancelled) onErrorRef.current(e instanceof Error ? e.message : String(e))
        })
        .finally(() => {
          if (!cancelled) setEstimating(false)
        })
    }, 400)
    return () => {
      cancelled = true
      clearTimeout(timer)
    }
  }, [config.id, version, selected])

  const toggleExpand = async (node: TreeNode) => {
    const path = node.path
    const willExpand = !expanded.has(path)
    setExpanded((e) => {
      const n = new Set(e)
      if (willExpand) n.add(path)
      else n.delete(path)
      return n
    })
    if (willExpand && !treeCache[path]) {
      setLoadingDirs((s) => new Set(s).add(path))
      try {
        const kids = await backupConfigsApi.tree(config.id, version, path)
        setTreeCache((c) => ({ ...c, [path]: kids }))
      } catch (e) {
        onError(e instanceof Error ? e.message : String(e))
      } finally {
        setLoadingDirs((s) => {
          const n = new Set(s)
          n.delete(path)
          return n
        })
      }
    }
  }

  // 递归拉取整棵子树的全部文件路径（本地索引读，快）。同时把沿途目录塞进 children 缓存供浏览复用。
  const fetchAllFiles = async (dirPath: string): Promise<string[]> => {
    const kids = await backupConfigsApi.tree(config.id, version, dirPath || null)
    setTreeCache((c) => ({ ...c, [dirPath]: kids }))
    const nested = await Promise.all(
      kids.map(async (k) => {
        if (k.isDir) return k.hasChildren ? fetchAllFiles(k.path) : []
        return [k.path]
      }),
    )
    return nested.flat()
  }

  // 文件夹勾选级联全选/取消整棵子树（递归抓取，非仅已加载节点，避免"漏选未展开子目录"的坑）。
  const toggleFolder = async (node: TreeNode) => {
    setCascading((s) => new Set(s).add(node.path))
    try {
      const files = node.hasChildren ? await fetchAllFiles(node.path) : []
      setSelected((sel) => {
        const next = new Set(sel)
        const allSelected = files.length > 0 && files.every((f) => next.has(f))
        for (const f of files) {
          if (allSelected) next.delete(f)
          else next.add(f)
        }
        return next
      })
    } catch (e) {
      onError(e instanceof Error ? e.message : String(e))
    } finally {
      setCascading((s) => {
        const n = new Set(s)
        n.delete(node.path)
        return n
      })
    }
  }

  const toggleFile = (path: string) => {
    setSelected((sel) => {
      const next = new Set(sel)
      if (next.has(path)) next.delete(path)
      else next.add(path)
      return next
    })
  }

  // 文件夹勾选态（checked/indeterminate/unchecked）仅基于已加载（缓存中）的后代文件——尚未展开的
  // 子目录不计入。这与 toggleFolder 的即时递归抓取不同：抓取完成后缓存已补齐，态会立即反映最新选择。
  const collectLoadedFiles = (dirPath: string): string[] => {
    const kids = treeCache[dirPath]
    if (!kids) return []
    let out: string[] = []
    for (const k of kids) {
      if (k.isDir) out = out.concat(collectLoadedFiles(k.path))
      else out.push(k.path)
    }
    return out
  }
  const folderState = (dirPath: string): 'checked' | 'indeterminate' | 'unchecked' => {
    const files = collectLoadedFiles(dirPath)
    if (files.length === 0) return 'unchecked'
    const n = files.filter((f) => selected.has(f)).length
    if (n === 0) return 'unchecked'
    return n === files.length ? 'checked' : 'indeterminate'
  }

  // 与选中集的交集：选择性还原时，替代表只对"实际会被还原"的不可恢复路径有意义
  // （后端按 SelectedPaths 过滤生效集，替代路径若未被勾选则替代是空操作）。未选中任何项 = 还原整版本，全部展示。
  const relevantUnrecoverable = selected.size === 0 ? unrecoverable : unrecoverable.filter((p) => selected.has(p))
  // 同理：选择性还原时，只提示实际会被还原的那些沿用条目。
  const relevantStale = selected.size === 0 ? stale : stale.filter((e) => selected.has(e.path))

  const setAllNearest = () => {
    const ch: Record<string, number> = {}
    for (const p of relevantUnrecoverable) ch[p] = options[p]?.length ? options[p][0].version : 0
    setChoices(ch)
  }

  const start = async () => {
    setStarting(true)
    try {
      const subs: Record<string, number> = {}
      for (const [p, v] of Object.entries(choices)) if (v > 0) subs[p] = v
      const selectedPaths = selected.size > 0 ? Array.from(selected) : undefined
      const state = await backupConfigsApi.restore(
        config.id, target || null, version, subs, selectedPaths, conflict, rehydratePriority,
      )
      onStarted(state)
    } catch (e) {
      onError(e instanceof Error ? e.message : String(e))
    } finally {
      setStarting(false)
    }
  }

  return (
    <div className={overlayStyle} onClick={onClose}>
      <div className={panelStyle} onClick={(e) => e.stopPropagation()}>
        <h3 style={{ marginTop: 0 }}>Restore — {config.name}</h3>
        <Field label="Restore to">
          <input className="mono" value={target} onChange={(e) => setTarget(e.target.value)} style={{ width: 340 }} />
          <button type="button" onClick={() => setBrowsing(true)}>
            Browse
          </button>
        </Field>
        {browsing && (
          <PathBrowser
            initialPath={target || undefined}
            onPick={(p) => {
              setTarget(p)
              setBrowsing(false)
            }}
            onClose={() => setBrowsing(false)}
          />
        )}
        <Field label="Version">
          <select value={version ?? ''} onChange={(e) => setVersion(e.target.value === '' ? null : Number(e.target.value))}>
            <option value="">Latest</option>
            {versions.map((v) => <option key={v} value={v}>{v}</option>)}
          </select>
        </Field>

        {subsLoading && <div className="text-faint">Loading…</div>}
        {/* 沿用的内容是**有效**数据，就是这个版本能给出的最好结果——所以不提供"按版本替代"，
            那会暗示存在更好的选项。但操作员必须知道：还原这个版本，这些文件拿到的是更早的内容。 */}
        {!subsLoading && relevantStale.length > 0 && (
          <div className="text-warn" style={{ margin: '0.6rem 0' }}>
            {relevantStale.length} file(s) in this version hold content from an earlier backup —
            the source could not be read since then, so restoring gives you that older content:
            <ul style={{ margin: '0.2rem 0 0 1.2rem' }}>
              {relevantStale.slice(0, 10).map((e) => (
                <li key={e.path}>
                  <span className="mono">{e.path}</span>
                  {' '}— unread since {new Date(e.unreadableAt).toLocaleString()}
                </li>
              ))}
            </ul>
            {relevantStale.length > 10 && <div>…and {relevantStale.length - 10} more</div>}
          </div>
        )}
        {!subsLoading && relevantUnrecoverable.length > 0 && (
          <div className="text-faint" style={{ margin: '0.6rem 0' }}>
            <div style={{ marginBottom: '0.3rem' }}>
              {relevantUnrecoverable.length} unrecoverable file(s) in this version — choose a version to substitute (or skip):
              {' '}<button type="button" onClick={setAllNearest}>Set all to nearest</button>
            </div>
            <div className="table-scroll" tabIndex={0}>
              <table>
                <thead><tr><th>File</th><th>Substitute from</th></tr></thead>
                <tbody>
                  {relevantUnrecoverable.map((p) => (
                    <tr key={p}>
                      <td className="mono">{p}</td>
                      <td style={{ textAlign: 'center' }}>
                        <select value={choices[p] ?? 0} onChange={(e) => setChoices((c) => ({ ...c, [p]: Number(e.target.value) }))}>
                          <option value={0}>Skip (don't restore)</option>
                          {(options[p] ?? []).map((o) => <option key={o.version} value={o.version}>Version {o.version}</option>)}
                        </select>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}

        <div style={{ margin: '0.8rem 0 0.3rem' }}>
          <strong>Select files/folders (optional)</strong>
        </div>
        <div className="text-faint" style={{ marginBottom: '0.4rem' }}>
          Leave nothing selected to restore the entire version. Checking a folder selects its whole subtree
          (fetched recursively — may take a moment for large folders).
        </div>
        <div className="mono" style={{ maxHeight: 260, overflow: 'auto', border: '1px solid var(--border)', padding: '0.4rem' }}>
          {loadingDirs.has('') ? (
            <div className="text-faint">Loading…</div>
          ) : (
            <TreeBrowser
              dirPath=""
              depth={0}
              tree={treeCache}
              expanded={expanded}
              loadingDirs={loadingDirs}
              cascading={cascading}
              selected={selected}
              folderState={folderState}
              onToggleExpand={toggleExpand}
              onToggleFolder={toggleFolder}
              onToggleFile={toggleFile}
            />
          )}
        </div>
        <div className="text-faint" style={{ margin: '0.3rem 0 0.8rem' }}>
          {selected.size} file(s) selected
          {selected.size > 0 && (
            <>{' '}<button type="button" onClick={() => setSelected(new Set())}>Clear selection</button></>
          )}
        </div>

        {selected.size > 0 && (
          <div style={{ margin: '0.4rem 0 0.8rem', padding: '0.6rem', border: '1px solid var(--border)' }}>
            {estimating && <div className="text-muted">Estimating…</div>}
            {!estimating && estimate && (
              <>
                <div>
                  Download: {formatBytes(estimate.downloadBytes)} — Uncompressed: {formatBytes(estimate.uncompressedBytes)}
                  {' '}— {estimate.fileCount} file(s)
                </div>
                {estimate.archivedObjects > 0 && (
                  <div className="text-warn" style={{ marginTop: '0.4rem' }}>
                    {estimate.archivedObjects} archived object(s) need rehydration before download
                    (typically several hours){estimate.rehydratePending > 0 && ` — ${estimate.rehydratePending} already rehydrating`}.
                  </div>
                )}
              </>
            )}
          </div>
        )}

        {selected.size > 0 && estimate && estimate.archivedObjects > 0 && (
          <Field label="Rehydrate priority">
            <select value={rehydratePriority} onChange={(e) => setRehydratePriority(Number(e.target.value))}>
              {Object.entries(rehydratePriorityLabels).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
            </select>
          </Field>
        )}

        <Field label="On conflict">
          <select value={conflict} onChange={(e) => setConflict(Number(e.target.value))}>
            {Object.entries(restoreConflictModeLabels).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
          </select>
        </Field>

        <div className="row" style={{ marginTop: '0.8rem' }}>
          <button type="button" className="btn-primary" onClick={start} disabled={subsLoading || starting}>
            {starting ? 'Starting…' : 'Start restore'}
          </button>
          <button type="button" onClick={onClose}>Cancel</button>
        </div>
      </div>
    </div>
  )
}

function TreeBrowser({
  dirPath, depth, tree, expanded, loadingDirs, cascading, selected, folderState,
  onToggleExpand, onToggleFolder, onToggleFile,
}: {
  dirPath: string
  depth: number
  tree: Record<string, TreeNode[]>
  expanded: Set<string>
  loadingDirs: Set<string>
  cascading: Set<string>
  selected: Set<string>
  folderState: (dirPath: string) => 'checked' | 'indeterminate' | 'unchecked'
  onToggleExpand: (node: TreeNode) => void
  onToggleFolder: (node: TreeNode) => void
  onToggleFile: (path: string) => void
}) {
  const nodes = tree[dirPath] ?? []
  if (nodes.length === 0) {
    return depth === 0 ? <div className="text-faint">Empty version</div> : null
  }
  return (
    <>
      {nodes.map((node) => {
        const state = node.isDir ? folderState(node.path) : undefined
        return (
          <div key={node.path}>
            <div className="row text-sm" style={{ paddingLeft: depth * 16 }}>
              {node.isDir ? (
                <>
                  <button
                    type="button"
                    className="icon-btn"
                    onClick={() => onToggleExpand(node)}
                    disabled={!node.hasChildren}
                    style={{ width: 18, cursor: node.hasChildren ? 'pointer' : 'default' }}
                  >
                    {node.hasChildren ? (expanded.has(node.path) ? '▾' : '▸') : ' '}
                  </button>
                  <input
                    type="checkbox"
                    checked={state === 'checked'}
                    ref={(el) => {
                      if (el) el.indeterminate = state === 'indeterminate'
                    }}
                    disabled={cascading.has(node.path)}
                    onChange={() => onToggleFolder(node)}
                  />
                  <span><strong>{node.name}</strong>/</span>
                  {cascading.has(node.path) && <span className="text-muted">loading…</span>}
                </>
              ) : (
                <>
                  <span style={{ width: 18, display: 'inline-block' }} />
                  <input type="checkbox" checked={selected.has(node.path)} onChange={() => onToggleFile(node.path)} />
                  <span>{node.name}</span>
                  {node.length != null && <span className="text-muted" style={{ marginLeft: 6 }}>{formatBytes(node.length)}</span>}
                  {/* 选择还原内容的这一刻，正是最需要知道"这份内容不是这个版本时刻的"的时候。 */}
                  {node.unreadableAt && (
                    <span className="text-warn" style={{ marginLeft: 6 }}>
                      older content (unread since {new Date(node.unreadableAt).toLocaleDateString()})
                    </span>
                  )}
                </>
              )}
            </div>
            {node.isDir && expanded.has(node.path) && (
              loadingDirs.has(node.path) ? (
                <div className="text-faint" style={{ paddingLeft: (depth + 1) * 16 }}>Loading…</div>
              ) : (
                <TreeBrowser
                  dirPath={node.path}
                  depth={depth + 1}
                  tree={tree}
                  expanded={expanded}
                  loadingDirs={loadingDirs}
                  cascading={cascading}
                  selected={selected}
                  folderState={folderState}
                  onToggleExpand={onToggleExpand}
                  onToggleFolder={onToggleFolder}
                  onToggleFile={onToggleFile}
                />
              )
            )}
          </div>
        )
      })}
    </>
  )
}
