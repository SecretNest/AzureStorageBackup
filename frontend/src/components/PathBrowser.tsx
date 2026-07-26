import { useEffect, useState } from 'react'
import { browseApi, type BrowseResult } from '../api/browse'
import { overlayStyle, panelStyle } from './modalStyles'

/**
 * 本地目录选择器（设计 §7）。只有目录可选；文件列出但不可选，
 * 以便确认选对了位置。越界项（通常是指向根外的软链）灰显不可点。
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
  const [path, setPath] = useState<string | undefined>(initialPath)

  useEffect(() => {
    // 目录快速切换时，取消上一个还没返回的请求，防止慢响应后到达
    // 覆盖了新目录的数据（乱序响应）。
    const controller = new AbortController()
    setError(null)
    browseApi
      .list(path, controller.signal)
      .then(setData)
      .catch((e) => {
        if (controller.signal.aborted) return
        setError(e instanceof Error ? e.message : String(e))
      })
    return () => controller.abort()
  }, [path])

  return (
    <div className={overlayStyle} onClick={onClose}>
      <div className={panelStyle} onClick={(e) => e.stopPropagation()}>
        <h3 style={{ marginTop: 0 }}>Choose a folder</h3>

        <p className="mono text-faint" style={{ wordBreak: 'break-all' }}>
          {data?.path ?? path ?? ''}
        </p>

        {error && <p className="text-danger">{error}</p>}

        <div style={{ maxHeight: 320, overflowY: 'auto', border: '1px solid var(--border)', padding: 'var(--sp-2)' }}>
          {data?.parent && (
            <div>
              <button type="button" className="btn-ghost" onClick={() => setPath(data.parent!)}>
                .. (up)
              </button>
            </div>
          )}
          {data?.entries.map((e) => (
            <div key={e.fullPath} style={{ padding: '0.15rem 0' }}>
              {e.isDirectory ? (
                <button
                  type="button"
                  className="btn-ghost"
                  disabled={e.outsideRoot}
                  title={e.outsideRoot ? 'Outside the configured root' : undefined}
                  onClick={() => setPath(e.fullPath)}
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
          {/* 少给了东西就必须说出来：不可 stat 的子项被跳过时，目录看上去和空目录一模一样。 */}
          {!!data?.skipped && (
            <p className="text-warn">
              {data.skipped} item(s) could not be read and are not listed.
            </p>
          )}
        </div>

        <div className="row" style={{ marginTop: '1rem' }}>
          <button type="button" className="btn-primary" onClick={() => data && onPick(data.path)} disabled={!data}>
            Use this folder
          </button>
          <button type="button" onClick={onClose}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  )
}
