import { useEffect, useState } from 'react'
import {
  accountsApi,
  regionLabels,
  AzureRegion,
  ProxyMode,
  type Account,
  type AccountInput,
  type ConnectionResult,
} from '../api/accounts'
import { refreshKeyringStatus } from '../api/keyring'
import { Modal } from '../components/Modal'
import { Field } from '../components/Field'
import { ContainersPage } from './ContainersPage'

/// The template prefilled when creating. It is a **real value** rather than a placeholder: only one
/// part of this URL needs changing, and editable text is less work than grey hint text — replace
/// <endpoint> and you are done. A half-edited one is caught by isValidEndpoint (see its comment).
const endpointTemplate = 'https://<endpoint>.blob.core.windows.net/'

/**
 * A blob endpoint must be a valid http(s) URL.
 *
 * The template above is not special-cased: `<` and `>` are WHATWG forbidden host code points, so
 * appearing in the **host** makes `new URL()` throw — and the placeholder sits exactly in the host,
 * so generic URL validation catches a half-edited template for free, along with every typo.
 * (`<>` inside the path is legal and gets percent-encoded, which is precisely why "is this a valid
 * URL" is the better question than "is there a placeholder".)
 *
 * The scheme check is additional: `new URL()` accepts `ftp://` and the like just as happily.
 */
const isValidEndpoint = (s: string) => {
  try {
    const { protocol } = new URL(s.trim())
    return protocol === 'https:' || protocol === 'http:'
  } catch {
    return false
  }
}

const emptyForm: AccountInput = {
  name: '',
  description: '',
  blobEndpoint: endpointTemplate,
  region: AzureRegion.Global,
  accountKey: '',
  useProxy: false,
  proxyMode: ProxyMode.Independent,
  proxyHost: '',
  proxyPort: null,
  proxyUsername: '',
  proxyPassword: '',
}

/// Accounts is now a section inside Settings rather than a page of its own: accounts are configured
/// once and never touched again, so a permanent top-level nav entry misrepresents them. Expanding one
/// to see its containers still replaces the whole area with ContainersPage — that is a drill-down with
/// a way back, which works just as well nested in a section.
export function AccountsSection() {
  const [accounts, setAccounts] = useState<Account[]>([])
  const [editing, setEditing] = useState<Account | null>(null)
  const [form, setForm] = useState<AccountInput>(emptyForm)
  const [showForm, setShowForm] = useState(false)
  const [testResult, setTestResult] = useState<ConnectionResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [viewing, setViewing] = useState<Account | null>(null)
  const [resetting, setResetting] = useState<Account | null>(null)

  const load = () => {
    accountsApi.list().then(setAccounts).catch((e) => setError(e instanceof Error ? e.message : String(e)))
  }
  useEffect(load, [])

  const startNew = () => {
    setEditing(null)
    setForm(emptyForm)
    setTestResult(null)
    setError(null)
    setShowForm(true)
  }

  const startEdit = (a: Account) => {
    setEditing(a)
    setForm({
      name: a.name,
      description: a.description ?? '',
      blobEndpoint: a.blobEndpoint,
      region: a.region,
      accountKey: '',
      useProxy: a.useProxy,
      proxyMode: a.proxyMode,
      proxyHost: a.proxyHost ?? '',
      proxyPort: a.proxyPort,
      proxyUsername: a.proxyUsername ?? '',
      proxyPassword: '',
    })
    setTestResult(null)
    setError(null)
    setShowForm(true)
  }

  const save = async () => {
    // Reject an invalid endpoint before entering the busy state: the backend only checks that the Key
    // is non-empty when creating an account and never looks at the URL, so letting it through means it
    // surfaces at the first cloud call as an unrecognisable error.
    if (!isValidEndpoint(form.blobEndpoint)) {
      setError('Blob Endpoint must be a valid http(s) URL.')
      return
    }
    setBusy(true)
    setError(null)
    // Non-Global regions only work behind a proxy (PRD 1.1): without one, force Global.
    const payload = form.useProxy ? form : { ...form, region: AzureRegion.Global }
    try {
      if (editing) {
        await accountsApi.update(editing.id, payload)
        setShowForm(false)
        load()
      } else {
        const created = await accountsApi.create(payload)
        setShowForm(false)
        load()
        // A newly created account goes straight to container listing (PRD 1.4)
        setViewing(created)
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const remove = async (a: Account) => {
    if (!window.confirm(`Delete account "${a.name}"?`)) return
    try {
      await accountsApi.remove(a.id)
      setShowForm(false)
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const test = async () => {
    setBusy(true)
    setTestResult(null)
    try {
    // Editing uses the endpoint that takes an id: the Key box is empty here ("Leave blank to keep
    // current"), and the id-less endpoint returns 400 for an empty Key — while "I changed the endpoint
    // or the proxy and want to check the existing key still connects" is exactly what editing is for.
      setTestResult(
        editing
          ? await accountsApi.testConnectionFor(editing.id, form)
          : await accountsApi.testConnection(form),
      )
    } catch (e) {
      setTestResult({ success: false, error: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  const set = <K extends keyof AccountInput>(k: K, v: AccountInput[K]) =>
    setForm((f) => ({ ...f, [k]: v }))

  // Keyring-loss recovery (design §3.5): re-enter the account key and proxy password; the backend verifies against the cloud before persisting.
  const startReset = (a: Account) => {
    setResetting(a)
    setError(null)
  }

  const closeReset = () => {
    setResetting(null)
    setError(null)
  }

  const submitReset = async (accountKey: string, proxyPassword: string) => {
    if (!resetting) return
    setBusy(true)
    try {
      await accountsApi.resetSecrets(resetting.id, accountKey, proxyPassword || null)
      setResetting(null)
      load()
      // The top banner and the backups page's ordering dependency read the same state: it has to be
      // refreshed immediately after a successful reset, or the banner keeps showing a stale warning (design §3.5).
      void refreshKeyringStatus()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  if (viewing) {
    return (
      <ContainersPage
        account={viewing}
        onBack={() => {
          setViewing(null)
          load()
        }}
      />
    )
  }

  return (
    <section>
      <div className="page-header">
        <h2 style={{ margin: 0 }}>Accounts</h2>
        <button type="button" className="btn-primary" onClick={startNew}>
          New Account
        </button>
      </div>

      {error && <p className="text-danger">{error}</p>}

      <div className="table-scroll" tabIndex={0}>
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>Endpoint</th>
              <th>Region</th>
              <th>Proxy</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {accounts.length === 0 ? (
              <tr>
                <td colSpan={5} className="empty-state">
                  No accounts yet.
                </td>
              </tr>
            ) : (
              accounts.map((a) => (
                <tr key={a.id}>
                  <td>
                    {a.name}
                    {a.secretsUnavailable && (
                      <span className="row-inline" style={{ marginLeft: '0.5rem' }}>
                        <span className="badge badge-warn">Credential required</span>
                        <button type="button" className="btn-ghost" onClick={() => startReset(a)}>
                          Re-enter
                        </button>
                      </span>
                    )}
                  </td>
                  <td>{a.blobEndpoint}</td>
                  <td>{regionLabels[a.region]}</td>
                  <td>{a.useProxy ? 'Yes' : 'No'}</td>
                  <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>
                    <button type="button" className="btn-ghost" onClick={() => setViewing(a)}>
                      Containers
                    </button>{' '}
                    <button type="button" className="btn-ghost" onClick={() => startEdit(a)}>
                      Edit
                    </button>
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      {showForm && (
        <div className="panel">
          <h2>{editing ? `Edit: ${editing.name}` : 'New Account'}</h2>

          <Field label="Name">
            <input value={form.name} onChange={(e) => set('name', e.target.value)} />
          </Field>
          <Field label="Description">
            <input
              value={form.description ?? ''}
              onChange={(e) => set('description', e.target.value)}
            />
          </Field>
          <Field label="Blob Endpoint">
            <input
              className="w-lg mono"
              placeholder="https://account.blob.core.windows.net"
              value={form.blobEndpoint}
              onChange={(e) => set('blobEndpoint', e.target.value)}
            />
          </Field>
          <Field label="Region">
            {/* Non-Global regions are selectable only with a proxy enabled (PRD 1.1). */}
            <select
              value={form.useProxy ? form.region : AzureRegion.Global}
              disabled={!form.useProxy}
              onChange={(e) => set('region', Number(e.target.value))}
            >
              <option value={AzureRegion.Global}>Global</option>
              <option value={AzureRegion.China}>China</option>
              <option value={AzureRegion.UsGov}>US Gov</option>
            </select>
            {!form.useProxy && <span className="text-faint" style={{ marginLeft: '0.4rem' }}>enable proxy for other regions</span>}
          </Field>
          <Field label="Account Key">
            <input
              className="w-lg mono"
              type="password"
              placeholder={editing ? 'Leave blank to keep current' : ''}
              value={form.accountKey ?? ''}
              onChange={(e) => set('accountKey', e.target.value)}
            />
          </Field>

          <Field label="Use Proxy">
            <input
              type="checkbox"
              checked={form.useProxy}
              onChange={(e) => set('useProxy', e.target.checked)}
            />
          </Field>

          {form.useProxy && (
            <>
              <Field label="Proxy Mode">
                <select
                  value={form.proxyMode}
                  onChange={(e) => set('proxyMode', Number(e.target.value))}
                >
                  <option value={ProxyMode.Independent}>Independent</option>
                  <option value={ProxyMode.DockerEnv}>From docker environment</option>
                </select>
              </Field>

              {form.proxyMode === ProxyMode.Independent && (
                <>
                  <Field label="Proxy Host">
                    <input
                      className="w-md"
                      value={form.proxyHost ?? ''}
                      onChange={(e) => set('proxyHost', e.target.value)}
                    />
                  </Field>
                  <Field label="Proxy Port">
                    <input
                      className="w-sm"
                      type="number"
                      value={form.proxyPort ?? ''}
                      onChange={(e) =>
                        set('proxyPort', e.target.value ? Number(e.target.value) : null)
                      }
                    />
                  </Field>
                  <Field label="Proxy Username">
                    <input
                      className="w-md"
                      value={form.proxyUsername ?? ''}
                      onChange={(e) => set('proxyUsername', e.target.value)}
                    />
                  </Field>
                  <Field label="Proxy Password">
                    <input
                      className="w-md"
                      type="password"
                      placeholder={editing ? 'Leave blank to keep current' : ''}
                      value={form.proxyPassword ?? ''}
                      onChange={(e) => set('proxyPassword', e.target.value)}
                    />
                  </Field>
                </>
              )}
            </>
          )}

          <div className="row" style={{ marginTop: '1rem' }}>
            <button type="button" className="btn-primary" onClick={save} disabled={busy}>
              {editing ? 'Save' : 'Create'}
            </button>
            <button type="button" onClick={test} disabled={busy}>
              Test Connection
            </button>
            <button type="button" onClick={() => setShowForm(false)} disabled={busy}>
              Cancel
            </button>

            {/* Delete lives inside the edit form rather than on every list row: it is the only
                irreversible action on this page, and putting it at the end of a row makes it as close
                to hand as Containers and Edit, which are both clicked casually.
                The title sits on the outer span rather than the button — a disabled button receives no
                pointer events in most browsers, which would kill the tooltip exactly when the reason
                for being disabled most needs explaining. */}
            {editing && (
              <span
                style={{ marginLeft: 'auto' }}
                title={
                  editing.usedByBackups.length > 0
                    ? `In use by ${editing.usedByBackups.length} backup${
                        editing.usedByBackups.length > 1 ? 's' : ''
                      }: ${editing.usedByBackups.join(', ')}`
                    : undefined
                }
              >
                <button
                  type="button"
                  className="btn-ghost btn-danger"
                  onClick={() => remove(editing)}
                  disabled={busy || editing.usedByBackups.length > 0}
                >
                  Delete
                </button>
              </span>
            )}
          </div>

          {testResult && (
            <p className={testResult.success ? 'text-ok' : 'text-danger'}>
              {testResult.success
                ? 'Connection succeeded.'
                : `Connection failed: ${testResult.error}`}
            </p>
          )}
        </div>
      )}

      {resetting && (
        <ResetSecretsModal
          account={resetting}
          busy={busy}
          error={error}
          onSubmit={submitReset}
          onClose={closeReset}
        />
      )}
    </section>
  )
}

// The keyring-loss recovery dialog: re-enter the account key (required) and the proxy password (only
// for proxied accounts). The backend verifies them against the cloud and persists only on success;
// a failure returns 400 carrying "Verification failed: …", shown as-is.
function ResetSecretsModal({
  account, busy, error, onSubmit, onClose,
}: {
  account: Account
  busy: boolean
  error: string | null
  onSubmit: (accountKey: string, proxyPassword: string) => void
  onClose: () => void
}) {
  const [accountKey, setAccountKey] = useState('')
  const [proxyPassword, setProxyPassword] = useState('')

  return (
    <Modal
      title={`Re-enter Credentials — ${account.name}`}
      onClose={onClose}
      footer={
        <>
          <button
            type="button"
            className="btn-primary"
            onClick={() => onSubmit(accountKey, proxyPassword)}
            disabled={busy || !accountKey}
          >
            Submit
          </button>
          <button type="button" onClick={onClose} disabled={busy}>
            Cancel
          </button>
        </>
      }
    >
      <p>
        The data protection keys used to store this account's credentials were lost.
        Re-enter the account key{account.useProxy ? ' and proxy password' : ''} to restore access;
        it will be verified against the live storage account before being saved.
      </p>

      <Field label="Account Key">
        <input
          className="w-lg"
          type="password"
          value={accountKey}
          onChange={(e) => setAccountKey(e.target.value)}
        />
      </Field>
      {account.useProxy && (
        <Field label="Proxy Password">
          <input
            className="w-lg"
            type="password"
            value={proxyPassword}
            onChange={(e) => setProxyPassword(e.target.value)}
          />
        </Field>
      )}

      {error && <p className="text-danger">{error}</p>}
    </Modal>
  )
}
