import { useState } from 'react'
import { backupConfigsApi } from '../api/backupConfigs'
import { ApiError } from '../api/client'
import { localRootDecision, type LocalRootPreview } from '../lib/localRootVerdict'
import { Modal } from './Modal'
import { PathBrowser } from './PathBrowser'

/**
 * Migrating the local root. Deliberately two steps — Check for a report, then Apply — because
 * getting this wrong makes the next backup record the entire backup as fully deleted and re-added.
 */
export function ChangeLocalRootDialog({
  configId,
  currentRoot,
  onDone,
  onClose,
}: {
  configId: number
  currentRoot: string
  /** newRoot is the path that just took effect — the caller needs it to update its own config snapshot, not just reload the list. */
  onDone: (newRoot: string) => void
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
      const applied = newRoot.trim()
      await backupConfigsApi.changeLocalRoot(configId, applied, decision.needsForce)
      onDone(applied)
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
            <button
              type="button"
              className="btn-primary"
              disabled={!canApply}
              title={canApply ? undefined : decision.needsForce ? 'Tick the confirmation above first.' : decision.headline}
              onClick={() => void apply()}
            >
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

          {/* Keep .row's default 8px gap rather than tightening to 4px: a focused input's outline
              extends 4px (2px ring + 2px offset), and at 4px the adjacent Browse button covers it. */}
          <div className="row">
            <input
              className="w-lg mono"
              // The placeholder is the current value: a root change is usually "the same files moved",
              // so the old path is the most useful draft. An invented /mnt/photos would read as the
              // current setting.
              placeholder={currentRoot || '/mnt/photos'}
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

          {/* Apply stays greyed out until a Check has run. Without saying so, the user changes the
              path and sees a dead button, unable to tell a missed step from a broken UI. */}
          {!preview && <div className="text-info">{decision.headline}</div>}

          {preview && (
            <div className="col" style={{ gap: 'var(--sp-2)' }}>
              <div className={`text-${decision.tone}`}>
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
                  <span>{decision.confirmBody}</span>
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
