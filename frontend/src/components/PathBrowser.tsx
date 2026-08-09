import { useEffect, useRef, useState } from 'react'
import { browseApi, type BrowseResult } from '../api/browse'
import { ApiError } from '../api/client'
import { Modal } from './Modal'

/**
 * The local directory picker (design §7). Only directories are selectable; files are listed but not
 * selectable, so the user can confirm they picked the right place. Out-of-bounds entries (usually a
 * symlink pointing outside the root) are greyed out and unclickable.
 */
export function PathBrowser({
  initialPath,
  onPick,
  onClose,
}: {
  initialPath?: string
  onPick: (path: string) => void
  onClose: () => void
}) {
  const [data, setData] = useState<BrowseResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [path, setPath] = useState<string | undefined>(initialPath)
  // Fall back only when nothing has ever been listed, and at most once. A ref rather than state:
  // if a directory disappears after the user has navigated into it, the right behaviour is to report
  // the error and leave them where they are, not to bounce them back to the root.
  const listedOnce = useRef(false)

  useEffect(() => {
    // When directories are switched quickly, cancel the previous request so a slow response cannot
    // arrive late and overwrite the new directory's data.
    const controller = new AbortController()
    setError(null)
    browseApi
      .list(path, controller.signal)
      .then((d) => {
        listedOnce.current = true
        setData(d)
      })
      .catch((e) => {
        if (controller.signal.aborted) return
        // An unusable starting directory must not be a dead end: the caller supplies the only entry
        // point, and the user has no command line on a NAS, so the UI would become unable to reach any
        // directory at all. Retry without a path — the backend then starts from Backup:Root — and say
        // that the fallback happened.
        // 404 = the directory is gone (the old root deleted, a mount that did not come up); 409 = it
        // is outside the configured root (the root was changed). Both mean "a poor starting point",
        // not "browsing is broken". 403 (unreadable) does not fall back — that is a real problem.
        const startUnusable = e instanceof ApiError && (e.status === 404 || e.status === 409)
        if (!listedOnce.current && path !== undefined && startUnusable) {
          setNotice(`${e.message} Showing the configured root instead.`)
          setPath(undefined)
          return
        }
        setError(e instanceof Error ? e.message : String(e))
      })
    return () => controller.abort()
  }, [path])

  // Once the user navigates into a directory themselves, that fallback notice is stale.
  function go(next: string) {
    setNotice(null)
    setPath(next)
  }

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

      {notice && <p className="text-warn">{notice}</p>}
      {error && <p className="text-danger">{error}</p>}

      <div style={{ border: '1px solid var(--border)', padding: 'var(--sp-2)' }}>
        {data?.parent && (
          <div>
            <button type="button" className="browse-row" onClick={() => go(data.parent!)}>
              .. (up)
            </button>
          </div>
        )}
        {data?.entries.map((e) => (
          <div key={e.fullPath}>
            {e.isDirectory ? (
              <button
                type="button"
                className="browse-row"
                disabled={e.outsideRoot}
                title={e.outsideRoot ? 'Outside the configured root' : undefined}
                onClick={() => go(e.fullPath)}
              >
                {e.name}/
              </button>
            ) : (
              <span className="text-faint">{e.name}</span>
            )}
          </div>
        ))}
        {data?.truncated && (
          <p className="text-warn">Too many entries — this listing was truncated.</p>
        )}
        {/* Anything omitted has to be stated: with unstattable children skipped, a directory looks exactly like an empty one. */}
        {!!data?.skipped && (
          <p className="text-warn">
            {data.skipped} item(s) could not be read and are not listed.
          </p>
        )}
      </div>
    </Modal>
  )
}
