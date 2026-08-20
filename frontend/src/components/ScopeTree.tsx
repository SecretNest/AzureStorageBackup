import { useEffect, useRef, useState } from 'react'
import { browseApi, type BrowseEntry } from '../api/browse'
import { isInScope, scopeState, withRule, type ScopeRules } from '../lib/scopeRules'
import { formatBytes } from '../constants/format'

const PAGE_SIZE = 500

interface Loaded {
  entries: BrowseEntry[]
  total: number
  // The server pages over the full listing taken before stat, not over entries: a page can contain
  // items skipped because their attributes could not be read (absent from entries, but still
  // consuming a position). The next page's offset must account for them, or the skipped items get
  // requested again — duplicate rows, duplicate React keys, and a Load more that takes several extra
  // presses to disappear because it believes only entries.length was consumed.
  consumed: number
  // Cumulative count of children not listed because their attributes could not be read (e.g. a
  // directory with mode r--: readdir works, stat on children does not).
  // Anything omitted has to be stated (as with PathBrowser's skipped notice), or these entries vanish
  // from the list and the user reads "unreadable" as "not there in the first place".
  skipped: number
  loading: boolean
  error: string | null
}

/**
 * The backup scope selection tree (design docs/configuration.md).
 *
 * Deliberately **not** a reuse of RestoreDialog's tree: that one's source is the cloud version index
 * (a finite known set, with tri-state computed from loaded descendants), while this one's is a live
 * filesystem (unbounded, changing, with tri-state computed from the rule set). They only look alike;
 * their cores are opposites, and merging them would make both fragile.
 *
 * Three pieces of state, one source of truth: rules is the truth, children/expanded are presentation.
 * A node's tick state is **always computed from rules and never stored** — so a click writes one rule
 * and stops, there is no "child updates parent updates child" loop, and an infinite one is impossible.
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
  // The key is the path **relative to localRoot** (empty string for the root), the same coordinates the rule set uses.
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
        // offset === 0 is a replacing load (first load, or Retry), so the cumulative consumed/skipped
        // counters restart with it — otherwise Retry would fold in the stale values from before the failure.
        const prevConsumed = offset === 0 ? 0 : (c[relative]?.consumed ?? 0)
        const prevSkipped = offset === 0 ? 0 : (c[relative]?.skipped ?? 0)
        return {
          ...c,
          [relative]: {
            // Append rather than replace: Load more must not shake off the entries already on screen.
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

  // load is a new identity every render; putting it in the dependency array directly would re-run the
  // effect after every children change (that is, after every completed load), contradicting "load the
  // root once, when localRoot appears". A ref holds the latest version, leaving only localRoot — what
  // genuinely decides whether to reload — in the dependencies (same approach as RestoreDialog's onErrorRef).
  const loadRef = useRef(load)
  loadRef.current = load

  // The root expands as soon as it appears: the first level holds exactly one node, the local root itself.
  useEffect(() => {
    if (localRoot) void loadRef.current('', 0)
    // localRoot is locked after creation and cannot actually change; it is listed to keep the dependencies honest.
  }, [localRoot])

  const toggleExpand = (relative: string) => {
    // Whether to start a load is computed from the children this render already has, rather than
    // inside setExpanded's updater — under StrictMode the updater runs twice (once probing, once for
    // real), and calling load in there fires two concurrent GETs for the same directory in development.
    const isExpanding = !expanded.has(relative)
    // No children[relative], or a previous load that failed (entry.error set and never succeeded),
    // both count as "no usable data yet" and both trigger a load — otherwise one failure locks the
    // directory permanently and the user can no longer open it (a failure is written into children
    // too, and the earlier check treated that as "already loaded").
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
  // total is the child count before the server stats anything: for a genuinely empty directory it is
  // always 0 (and skipped is necessarily 0 too, since there is nothing to skip). This condition is
  // what separates that from a page where everything readable was skipped and more remains — in that
  // case the branch below still renders the skipped notice and Load more.
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
      {/* Anything omitted has to be stated (as with PathBrowser's skipped notice): these children are
          missing from the list above because their attributes could not be read, not because they do not exist. */}
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
            // The server pages over the full listing taken before stat, so the next page continues
            // from what this page actually consumed — which includes the skipped items, not just the
            // entries that were stattable.
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
  // Tri-state, computed live. This line is the entire reason there is no infinite loop: it reads the rule set and neither reads nor writes any sibling or parent state.
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
        // A click does one thing: write one rule. Parent and child states are each recomputed on the next render.
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

/**
 * Used only to put an `ignored` badge on a row — a hint, not a decision. Actual ignoring happens at
 * backup time in the backend's IgnoreRuleSet; this supports only the most common forms (a trailing
 * /, `*.ext`, an exact path) and does not aim to be byte-identical with the backend. Not
 * understanding all of gitignore's semantics is acceptable: a wrong badge affects no backup result.
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
