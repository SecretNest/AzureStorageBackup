import { useState } from 'react'
import { backupConfigsApi } from '../api/backupConfigs'
import { ApiError } from '../api/client'
import { localRootDecision, type LocalRootPreview } from '../lib/localRootVerdict'
import { Modal } from './Modal'
import { PathBrowser } from './PathBrowser'

/**
 * 迁移本地根路径。流程刻意分两步——先 Check 看报告，再 Apply——因为这个操作改错了，
 * 下次备份会把整个备份记成全删全增。
 */
export function ChangeLocalRootDialog({
  configId,
  currentRoot,
  onDone,
  onClose,
}: {
  configId: number
  currentRoot: string
  onDone: () => void
  onClose: () => void
}) {
  const [newRoot, setNewRoot] = useState('')
  const [preview, setPreview] = useState<LocalRootPreview | null>(null)
  const [browsing, setBrowsing] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [acknowledged, setAcknowledged] = useState(false)

  const decision = localRootDecision(preview)
  const canApply = (decision.canApply || (decision.needsForce && acknowledged)) && !busy

  async function check() {
    setBusy(true)
    setError(null)
    setPreview(null)
    setAcknowledged(false)
    try {
      setPreview(await backupConfigsApi.previewLocalRoot(configId, newRoot.trim()))
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  async function apply() {
    setBusy(true)
    setError(null)
    try {
      await backupConfigsApi.changeLocalRoot(configId, newRoot.trim(), decision.needsForce)
      onDone()
    } catch (e) {
      setError(e instanceof ApiError ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <Modal
        title="Change Local Root"
        onClose={onClose}
        footer={
          <>
            <button type="button" onClick={onClose}>
              Cancel
            </button>
            <button type="button" className="btn-primary" disabled={!canApply} onClick={() => void apply()}>
              Apply
            </button>
          </>
        }
      >
        <div className="col" style={{ gap: 'var(--sp-3)' }}>
          <div>
            <div className="text-faint">Current</div>
            <div className="mono">{currentRoot || '(none)'}</div>
          </div>

          <div className="row" style={{ gap: 'var(--sp-1)' }}>
            <input
              className="w-lg mono"
              placeholder="/mnt/photos"
              value={newRoot}
              onChange={(e) => {
                setNewRoot(e.target.value)
                setPreview(null)
                setAcknowledged(false)
              }}
            />
            <button type="button" onClick={() => setBrowsing(true)}>
              Browse
            </button>
            <button type="button" disabled={!newRoot.trim() || busy} onClick={() => void check()}>
              Check
            </button>
          </div>

          {error && <div className="text-danger">{error}</div>}

          {preview && (
            <div className="col" style={{ gap: 'var(--sp-2)' }}>
              <div className={decision.tone === 'ok' ? 'text-ok' : `text-${decision.tone}`}>
                {decision.headline}
              </div>

              {preview.sampled > 0 && (
                <div className="text-faint">
                  {preview.missing} missing, {preview.sizeMismatch} with a different size
                  {preview.mtimeDiffers > 0 && (
                    <> ({preview.mtimeDiffers} also differ in modification time, which is not counted against the match)</>
                  )}
                </div>
              )}

              {preview.examples.length > 0 && (
                <div>
                  <div className="text-faint">Examples that did not match:</div>
                  <ul className="mono">
                    {preview.examples.map((p) => (
                      <li key={p}>{p}</li>
                    ))}
                  </ul>
                </div>
              )}

              {decision.needsForce && (
                <label className="row" style={{ gap: 'var(--sp-1)' }}>
                  <input
                    type="checkbox"
                    checked={acknowledged}
                    onChange={(e) => setAcknowledged(e.target.checked)}
                  />
                  <span>
                    I understand — change it anyway. The next backup will record every file that no longer
                    matches as deleted and upload the new ones. Scope rules are kept as they are and may no
                    longer match if this directory is laid out differently.
                  </span>
                </label>
              )}
            </div>
          )}
        </div>
      </Modal>

      {browsing && (
        <PathBrowser
          initialPath={newRoot || currentRoot || undefined}
          onPick={(p) => {
            setNewRoot(p)
            setPreview(null)
            setAcknowledged(false)
            setBrowsing(false)
          }}
          onClose={() => setBrowsing(false)}
        />
      )}
    </>
  )
}
