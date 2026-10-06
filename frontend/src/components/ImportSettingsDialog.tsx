import { useState } from 'react'
import { settingsApi, type ImportPlan, type SettingsDocument } from '../api/settings'
import { importSummary, keyPrompts, sectionLabel, withKeys, type EnteredKeys } from '../lib/settingsTransfer'
import { Field } from './Field'
import { Modal } from './Modal'

/// The import's "look before you leap" step. The server has already said what the file would do (the plan); this
/// shows it, collects a key for every new account the file has none for, and only then applies. Keys are typed
/// here rather than on the Accounts page afterwards because an account without a key is not flagged anywhere —
/// it looks normal and fails on first use.
export function ImportSettingsDialog({
  doc,
  plan,
  onClose,
  onImported,
}: {
  doc: SettingsDocument
  plan: ImportPlan
  onClose: () => void
  onImported: () => void
}) {
  const prompts = keyPrompts(doc, plan)
  const [keys, setKeys] = useState<EnteredKeys>(() =>
    Object.fromEntries(prompts.map((p) => [p.blobEndpoint, { accountKey: '', proxyPassword: '' }])),
  )
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<ImportPlan | null>(null)

  const ready = prompts.every((p) => keys[p.blobEndpoint]?.accountKey)

  const setKey = (endpoint: string, field: 'accountKey' | 'proxyPassword', value: string) =>
    setKeys((cur) => ({ ...cur, [endpoint]: { ...cur[endpoint], [field]: value } }))

  const run = async () => {
    setBusy(true)
    setError(null)
    try {
      const applied = await settingsApi.import(withKeys(doc, keys))
      setResult(applied)
      onImported()
    } catch (e) {
      // The document and the typed keys stay: fix the one thing that failed and press Import again.
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  if (result) {
    return (
      <Modal
        title="Settings imported"
        onClose={onClose}
        footer={
          <button type="button" className="btn-primary" onClick={onClose}>
            Close
          </button>
        }
      >
        <p>{importSummary(result)}</p>
      </Modal>
    )
  }

  return (
    <Modal
      title="Import settings"
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn-primary" onClick={run} disabled={busy || !ready}>
            {busy ? 'Importing…' : 'Import'}
          </button>
          <button type="button" onClick={onClose} disabled={busy}>
            Cancel
          </button>
        </>
      }
    >
      {error && <p className="text-danger">{error}</p>}

      <h3>Accounts</h3>
      {plan.accounts.length === 0 ? (
        <p className="text-muted">None in file.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>Endpoint</th>
              <th>Action</th>
            </tr>
          </thead>
          <tbody>
            {plan.accounts.map((a) => (
              <tr key={a.blobEndpoint}>
                <td>{a.name}</td>
                <td className="mono">{a.blobEndpoint}</td>
                <td>{a.action === 'create' ? 'Create' : 'Update'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <p className="text-muted">
        Accounts are matched by endpoint. Matched accounts are updated in place, so the backups that use them
        are unaffected; accounts on this server that are not in the file are left alone.
      </p>

      <h3>Settings</h3>
      <ul>
        <li>Backup defaults: {sectionLabel(plan.backupDefaults)}</li>
        <li>Performance: {sectionLabel(plan.performance)}</li>
        <li>Notifications: {sectionLabel(plan.notifications)}</li>
      </ul>

      {prompts.length > 0 && (
        <>
          <h3>Keys needed</h3>
          <p className="text-muted">
            These accounts are new here and the file carries no key for them. Enter each key to create the
            account; a matched account keeps the key it already has.
          </p>
          {prompts.map((p) => (
            <div key={p.blobEndpoint}>
              <Field label={`${p.name} — account key`}>
                <input
                  className="w-lg"
                  type="password"
                  autoComplete="off"
                  value={keys[p.blobEndpoint]?.accountKey ?? ''}
                  onChange={(e) => setKey(p.blobEndpoint, 'accountKey', e.target.value)}
                />
              </Field>
              {p.askProxyPassword && (
                <Field label={`${p.name} — proxy password (optional)`}>
                  <input
                    className="w-lg"
                    type="password"
                    autoComplete="off"
                    value={keys[p.blobEndpoint]?.proxyPassword ?? ''}
                    onChange={(e) => setKey(p.blobEndpoint, 'proxyPassword', e.target.value)}
                  />
                </Field>
              )}
            </div>
          ))}
        </>
      )}
    </Modal>
  )
}
