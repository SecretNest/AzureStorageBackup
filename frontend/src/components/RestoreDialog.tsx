import { useEffect, useRef, useState } from 'react'
import {
  backupConfigsApi,
  RestoreConflictMode,
  restoreConflictModeLabels,
  RestoreRehydratePriority,
  type BackupConfig,
  type BackupVersionInfo,
  type FileVersionOption,
  type RestoreEstimate,
  type RestoreRun,
  type TreeNode,
  type UnreadableEntry,
} from '../api/backupConfigs'
import { formatBytes, formatVersionSpan } from '../constants/format'
import { Field } from './Field'
import { Modal } from './Modal'
import { PathBrowser } from './PathBrowser'

const rehydratePriorityLabels: Record<number, string> = {
  [RestoreRehydratePriority.Standard]: 'Standard',
  [RestoreRehydratePriority.High]: 'High (faster, more expensive)',
}

// The restore dialog (§4.1a-d): pick a version → browse the lazy tree and tick → debounced estimate
// → conflict mode and rehydrate priority → target root → start. It also keeps the existing
// "substitute an unrecoverable file from another version" capability (§4.1).
export function RestoreDialog({
  config, onClose, onError, onStarted,
}: { config: BackupConfig; onClose: () => void; onError: (e: string) => void; onStarted: (s: RestoreRun) => void }) {
  // A number alone cannot pick out "the one from last Thursday" — the start and end times are how an operator recognises a version, so both are kept.
  const [versions, setVersions] = useState<BackupVersionInfo[]>([])
  const [version, setVersion] = useState<number | null>(null)
  const [target, setTarget] = useState(config.localRoot)
  const [browsing, setBrowsing] = useState(false)

  // Per-version substitution for unrecoverable files (existing capability)
  const [unrecoverable, setUnrecoverable] = useState<string[]>([])
  // Files whose content was carried over from an earlier version: no substitution needed (the content is valid, just old), but it must be known before restoring.
  const [stale, setStale] = useState<UnreadableEntry[]>([])
  const [options, setOptions] = useState<Record<string, FileVersionOption[]>>({})
  const [choices, setChoices] = useState<Record<string, number>>({})
  const [subsLoading, setSubsLoading] = useState(false)

  // Lazily loaded tree browsing and selection (§4.1a/d)
  const [treeCache, setTreeCache] = useState<Record<string, TreeNode[]>>({})
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [loadingDirs, setLoadingDirs] = useState<Set<string>>(new Set())
  const [cascading, setCascading] = useState<Set<string>>(new Set())
  const [selected, setSelected] = useState<Set<string>>(new Set())

  // Estimate, conflict mode and rehydrate priority (§4.1b/c/d)
  const [estimate, setEstimate] = useState<RestoreEstimate | null>(null)
  const [estimating, setEstimating] = useState(false)
  const [conflict, setConflict] = useState<number>(RestoreConflictMode.OverwriteIfChanged)
  const [rehydratePriority, setRehydratePriority] = useState<number>(RestoreRehydratePriority.Standard)
  const [starting, setStarting] = useState(false)

  // onError is an arrow function the parent writes inline in render, so its identity changes every
  // render. Once it is in the dependency arrays below, the backup list's routine refresh every few
  // seconds re-runs the effects — the dialog flickers on its own with nothing clicked: Loading…
  // appears and vanishes, the substitution table and carried-over notice re-lay out, and it looks
  // like the version was just switched. A ref holds the latest callback, leaving only config.id and
  // version — what genuinely decides what to refetch — in the dependencies.
  const onErrorRef = useRef(onError)
  onErrorRef.current = onError

  useEffect(() => {
    backupConfigsApi.versions(config.id).then(setVersions).catch(() => {})
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

  // Switching version resets the browse state and loads the root (a read of the locally authoritative index: fast, no cloud).
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

  // Selection changes debounce ~400 ms before calling restoreEstimate; with nothing selected it means "restore the whole version" and no estimate is shown.
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

  // Fetch every file path in a subtree recursively (a local index read, fast). Directories passed through on the way go into the children cache for browsing to reuse.
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

  // Ticking a folder cascades over the whole subtree (fetched recursively, not just the loaded nodes, which avoids the "unexpanded subdirectory silently unselected" trap).
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

  // A folder's tick state (checked/indeterminate/unchecked) is based only on descendants already
  // loaded into the cache — unexpanded subdirectories do not count. This differs from toggleFolder,
  // which fetches recursively straight away: once that completes the cache is filled in and the state
  // reflects the latest selection immediately.
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

  // Intersected with the selection: during a selective restore, substitutions only matter for the
  // unrecoverable paths that will actually be restored (the backend filters the effective set by
  // SelectedPaths, so substituting an unselected path is a no-op). Nothing selected = restore the
  // whole version, so everything is shown.
  const relevantUnrecoverable = selected.size === 0 ? unrecoverable : unrecoverable.filter((p) => selected.has(p))
  // Likewise: during a selective restore, only flag the carried-over entries that will be restored.
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
    <Modal
      title={`Restore — ${config.name}`}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn-primary" onClick={start} disabled={subsLoading || starting}>
            {starting ? 'Starting…' : 'Start restore'}
          </button>
          <button type="button" onClick={onClose}>Cancel</button>
        </>
      }
    >
      <Field label="Restore to">
        <input className="mono w-lg" value={target} onChange={(e) => setTarget(e.target.value)} />
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
          {versions.map((v) => (
            <option key={v.version} value={v.version}>
              Version {v.version} — {formatVersionSpan(v.startedAt, v.createdAt)}
            </option>
          ))}
        </select>
      </Field>

      {subsLoading && <div className="text-faint">Loading…</div>}
      {/* Carried-over content is **valid** data and is the best this version can give, so no
          "substitute by version" is offered — that would imply a better option exists. But the
          operator must know: restoring this version gives older content for these files. */}
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
      {/* Monospace belongs to the names inside the tree (TreeBrowser sets it per node), not to the box: on
          the box it also caught "Loading…", "Empty version", the byte sizes and the "older content (unread
          since …)" warning. The tree's indentation is padding in pixels, not columns of characters, so
          nothing here was relying on the container's advance width either. */}
      <div style={{ maxHeight: 260, overflow: 'auto', border: '1px solid var(--border)', padding: '0.4rem' }}>
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

    </Modal>
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
                    className="icon-btn hit-target"
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
                  {/* mono-inherit, not mono: the row is .text-sm already — see index.css. */}
                  <span className="mono-inherit"><strong>{node.name}</strong>/</span>
                  {cascading.has(node.path) && <span className="text-muted">loading…</span>}
                </>
              ) : (
                <>
                  <span style={{ width: 18, display: 'inline-block' }} />
                  <input type="checkbox" checked={selected.has(node.path)} onChange={() => onToggleFile(node.path)} />
                  <span className="mono-inherit">{node.name}</span>
                  {node.length != null && <span className="text-muted" style={{ marginLeft: 6 }}>{formatBytes(node.length)}</span>}
                  {/* The moment of choosing what to restore is exactly when it matters most to know that this content is not from this version's timestamp. */}
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
