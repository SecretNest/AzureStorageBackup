import { useState } from 'react'
import { Modal } from './Modal'

/**
 * 停止一次备份要问清楚：正在传的那个文件（连同它所有分卷）是传完再停，还是立刻扔掉。
 * 从前这里只有一句 window.confirm，两种后果被含混成一个"停止"——而它们差得很远：
 * 一个是"这部分算数，下次接着传"，另一个是"这部分删掉，下次重来"。
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
