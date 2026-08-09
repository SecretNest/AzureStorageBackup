import { useState } from 'react'
import { Modal } from './Modal'

/**
 * Stopping a backup has to ask one thing about the file currently uploading (and all its
 * volumes): finish it, or drop it. This used to be a single window.confirm, which blurred two
 * very different outcomes into one "stop" — one means "this part counts, continue next time",
 * the other means "this part is deleted, start over".
 */
export function StopBackupDialog({
  name,
  onStop,
  onClose,
}: {
  name: string
  onStop: (finishCurrentFiles: boolean) => Promise<void>
  onClose: () => void
}) {
  const [busy, setBusy] = useState<'finish' | 'now' | null>(null)

  const stop = async (finish: boolean) => {
    setBusy(finish ? 'finish' : 'now')
    try {
      await onStop(finish)
      onClose()
    } finally {
      setBusy(null)
    }
  }

  return (
    <Modal
      title={`Stop Backup — ${name}`}
      onClose={onClose}
      footer={
        <button type="button" onClick={onClose} disabled={busy !== null}>
          Keep running
        </button>
      }
    >
      <p>
        Files already uploaded are kept either way. The difference is what happens to the file being
        uploaded right now.
      </p>
      <div className="stacked-actions">
        <button type="button" className="btn-primary" onClick={() => void stop(true)} disabled={busy !== null}>
          {busy === 'finish' ? 'Finishing…' : 'Finish current files, then stop'}
        </button>
        <p className="text-faint">
          The file being uploaded — including every one of its volumes — is finished first. It counts,
          so the next run picks up from there. This can take a few minutes for a large file.
        </p>
        <button type="button" className="btn-danger" onClick={() => void stop(false)} disabled={busy !== null}>
          {busy === 'now' ? 'Stopping…' : 'Stop now'}
        </button>
        <p className="text-faint">
          Stops immediately. Volumes already uploaded for the unfinished file are deleted, so nothing
          unusable is left behind in the container.
        </p>
      </div>
    </Modal>
  )
}
