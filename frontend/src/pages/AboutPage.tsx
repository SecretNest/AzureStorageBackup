import { useEffect, useRef, useState } from 'react'
import { settingsApi, type ImportPlan, type SettingsDocument } from '../api/settings'
import { systemApi } from '../api/system'
import { ImportSettingsDialog } from '../components/ImportSettingsDialog'
import { exportFileName } from '../lib/settingsTransfer'

/// What this install *is*, and the one action that ends a session with it.
///
/// The version and the temp-directory map used to sit at the bottom of the Logs page, under everything the log table
/// scrolls through. They are not log data: nobody filters them, they never change while you watch, and the reason to
/// look them up — quoting a version in a bug report, or finding out which paths a docker volume has to cover — has
/// nothing to do with reading logs. Behind Logs' filter toolbar they were also the last thing on the longest page in
/// the app.
///
/// Log out shares this page rather than a fifth one of its own: it is the same category of thing — about this
/// installation and this session, not about any backup.
///
/// Settings export/import lives here too: it is about the installation as a whole, not any one settings page.
export function AboutSection({
  authRequired,
  onLogout,
  onImported,
}: {
  authRequired?: boolean
  onLogout?: () => void
  /** Called after a successful import, so the settings pages above refetch what the file changed. */
  onImported?: () => void
}) {
  const [paths, setPaths] = useState<Record<string, string>>({})
  const [version, setVersion] = useState('')

  // Failures are swallowed on purpose, as they were on the Logs page: neither figure is worth an error banner, and a
  // dead /system endpoint is already going to announce itself everywhere else.
  useEffect(() => {
    systemApi.paths().then(setPaths).catch(() => {})
    systemApi.version().then((v) => setVersion(v.version)).catch(() => {})
  }, [])

  return (
    <>
      <h2>System</h2>
      <p className="text-muted">Version: {version || '…'}</p>
      <p className="text-muted">Temp directories (map these as docker volumes):</p>
      <ul className="mono text-faint">
        {Object.entries(paths).map(([k, v]) => (
          <li key={k}>
            {k}: {v}
          </li>
        ))}
      </ul>

      <SettingsTransfer onImported={onImported} />

      {/* Log out is not in the sidebar: the phone tier's bottom bar has four slots and no room for a fifth. Desktop
          moved with it — one function in two places is what later maintenance forgets to sync. */}
      {authRequired && onLogout && (
        <>
          <h2>Session</h2>
          <button type="button" onClick={onLogout}>
            Log out
          </button>
        </>
      )}
    </>
  )
}

/// Export and import of everything under Settings as one JSON file.
///
/// The export goes through fetch and a Blob rather than an `<a href>` to the endpoint: a 409 (keyring lost, keys
/// requested) then shows up as a message next to the button, where an anchor would have navigated to a JSON error page.
function SettingsTransfer({ onImported }: { onImported?: () => void }) {
  const [includeSecrets, setIncludeSecrets] = useState(false)
  const [exporting, setExporting] = useState(false)
  const [exportError, setExportError] = useState<string | null>(null)

  const fileInput = useRef<HTMLInputElement>(null)
  const [reading, setReading] = useState(false)
  const [importError, setImportError] = useState<string | null>(null)
  const [pending, setPending] = useState<{ doc: SettingsDocument; plan: ImportPlan } | null>(null)

  const exportSettings = async () => {
    setExporting(true)
    setExportError(null)
    try {
      const doc = await settingsApi.export(includeSecrets)
      const blob = new Blob([JSON.stringify(doc, null, 2)], { type: 'application/json' })
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = exportFileName(new Date())
      a.click()
      URL.revokeObjectURL(url)
    } catch (e) {
      setExportError(e instanceof Error ? e.message : String(e))
    } finally {
      setExporting(false)
    }
  }

  const pickFile = async (file: File | undefined) => {
    // Clear the input first: a file input does not fire onChange for the same file twice, and "cancel the dialog,
    // pick the same file again" is a normal thing to do.
    if (fileInput.current) fileInput.current.value = ''
    if (!file) return
    setReading(true)
    setImportError(null)
    try {
      let doc: SettingsDocument
      try {
        doc = JSON.parse(await file.text()) as SettingsDocument
      } catch {
        throw new Error(`${file.name} is not valid JSON.`)
      }
      const plan = await settingsApi.previewImport(doc)
      setPending({ doc, plan })
    } catch (e) {
      setImportError(e instanceof Error ? e.message : String(e))
    } finally {
      setReading(false)
    }
  }

  return (
    <>
      <h2>Export / Import</h2>
      <p className="text-muted">
        Everything under Settings — accounts, backup defaults, performance, notifications — as one JSON file.
        Backups, groups and schedules are not included.
      </p>

      <div className="row" style={{ flexWrap: 'wrap' }}>
        <button type="button" onClick={exportSettings} disabled={exporting}>
          {exporting ? 'Exporting…' : 'Export settings'}
        </button>
        <label>
          <input type="checkbox" checked={includeSecrets} onChange={(e) => setIncludeSecrets(e.target.checked)} />{' '}
          Include account keys and proxy passwords
        </label>
      </div>
      {includeSecrets && (
        <p className="text-warn">
          The file will contain your account keys in plain text. Keep it where you would keep the keys themselves.
        </p>
      )}
      {exportError && <p className="text-danger">{exportError}</p>}

      <div className="row" style={{ marginTop: 'var(--sp-3)' }}>
        <input
          ref={fileInput}
          type="file"
          accept=".json,application/json"
          style={{ display: 'none' }}
          onChange={(e) => void pickFile(e.target.files?.[0])}
        />
        <button type="button" onClick={() => fileInput.current?.click()} disabled={reading}>
          {reading ? 'Reading…' : 'Import settings…'}
        </button>
      </div>
      <p className="text-muted">
        You will see what the file would change before anything is written. Accounts are matched by endpoint; a
        new account whose key is not in the file asks for it.
      </p>
      {importError && <p className="text-danger">{importError}</p>}

      {pending && (
        <ImportSettingsDialog
          doc={pending.doc}
          plan={pending.plan}
          onClose={() => setPending(null)}
          onImported={() => onImported?.()}
        />
      )}
    </>
  )
}
