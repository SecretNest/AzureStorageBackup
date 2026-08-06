import { useEffect, useRef, useState } from 'react'
import { browseApi, type BrowseResult } from '../api/browse'
import { ApiError } from '../api/client'
import { Modal } from './Modal'

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
  const [notice, setNotice] = useState<string | null>(null)
  const [path, setPath] = useState<string | undefined>(initialPath)
  // 只在「一次都还没列出来过」时才回落，且至多一次。用 ref 而不是 data：进过目录之后
  // 某个目录消失，正确的表现是报错并把用户留在原地，而不是把他弹回根目录。
  const listedOnce = useRef(false)

  useEffect(() => {
    // 目录快速切换时，取消上一个还没返回的请求，防止慢响应后到达
    // 覆盖了新目录的数据（乱序响应）。
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
        // 起始目录用不了时不能就停在这条错误上：调用方给的是唯一入口，用户在 NAS 上没有
        // 命令行，界面里就再也走不到任何目录了。改成不带 path 重来一次——后端会拿
        // Backup:Root 当起点——并把回落这件事说出来。
        // 404 = 目录已不存在（原根被删、挂载点没起来）；409 = 落在配置的根之外（根改过了）。
        // 两种都只是「起点选得不好」，不是「浏览不能用」；403（读不出来）不回落，那是真问题。
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

  // 用户自己点进某个目录后，那句回落说明就过期了。
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
        {/* 少给了东西就必须说出来：不可 stat 的子项被跳过时，目录看上去和空目录一模一样。 */}
        {!!data?.skipped && (
          <p className="text-warn">
            {data.skipped} item(s) could not be read and are not listed.
          </p>
        )}
      </div>
    </Modal>
  )
}
