import { useEffect, useState, type ReactNode } from 'react'
import {
  accountsApi,
  regionLabels,
  AzureRegion,
  ProxyMode,
  type Account,
  type AccountInput,
  type ConnectionResult,
} from '../api/accounts'

const emptyForm: AccountInput = {
  name: '',
  description: '',
  blobEndpoint: '',
  region: AzureRegion.Global,
  accountKey: '',
  useProxy: false,
  proxyMode: ProxyMode.Independent,
  proxyHost: '',
  proxyPort: null,
  proxyUsername: '',
  proxyPassword: '',
}

export function AccountsPage() {
  const [accounts, setAccounts] = useState<Account[]>([])
  const [editing, setEditing] = useState<Account | null>(null)
  const [form, setForm] = useState<AccountInput>(emptyForm)
  const [showForm, setShowForm] = useState(false)
  const [testResult, setTestResult] = useState<ConnectionResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = () => {
    accountsApi.list().then(setAccounts).catch((e) => setError(String(e)))
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
    setBusy(true)
    setError(null)
    try {
      if (editing) await accountsApi.update(editing.id, form)
      else await accountsApi.create(form)
      setShowForm(false)
      load()
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  const remove = async (a: Account) => {
    if (!window.confirm(`Delete account "${a.name}"?`)) return
    try {
      await accountsApi.remove(a.id)
      load()
    } catch (e) {
      setError(String(e))
    }
  }

  const test = async () => {
    setBusy(true)
    setTestResult(null)
    try {
      setTestResult(await accountsApi.testConnection(form))
    } catch (e) {
      setTestResult({ success: false, error: String(e) })
    } finally {
      setBusy(false)
    }
  }

  const set = <K extends keyof AccountInput>(k: K, v: AccountInput[K]) =>
    setForm((f) => ({ ...f, [k]: v }))

  return (
    <section>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h1>Accounts</h1>
        <button type="button" onClick={startNew}>
          New Account
        </button>
      </div>

      {error && <p style={{ color: 'crimson' }}>{error}</p>}

      <table style={{ width: '100%', borderCollapse: 'collapse', marginTop: '1rem' }}>
        <thead>
          <tr style={{ textAlign: 'left', borderBottom: '1px solid #ccc' }}>
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
              <td colSpan={5} style={{ padding: '1rem 0', color: '#666' }}>
                No accounts yet.
              </td>
            </tr>
          ) : (
            accounts.map((a) => (
              <tr key={a.id} style={{ borderBottom: '1px solid #eee' }}>
                <td>{a.name}</td>
                <td>{a.blobEndpoint}</td>
                <td>{regionLabels[a.region]}</td>
                <td>{a.useProxy ? 'Yes' : 'No'}</td>
                <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>
                  <button type="button" onClick={() => startEdit(a)}>
                    Edit
                  </button>{' '}
                  <button type="button" onClick={() => remove(a)}>
                    Delete
                  </button>
                </td>
              </tr>
            ))
          )}
        </tbody>
      </table>

      {showForm && (
        <div style={{ marginTop: '1.5rem', padding: '1rem', border: '1px solid #ccc' }}>
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
              placeholder="https://account.blob.core.windows.net"
              value={form.blobEndpoint}
              onChange={(e) => set('blobEndpoint', e.target.value)}
            />
          </Field>
          <Field label="Region">
            <select
              value={form.region}
              onChange={(e) => set('region', Number(e.target.value))}
            >
              <option value={AzureRegion.Global}>Global</option>
              <option value={AzureRegion.China}>China</option>
              <option value={AzureRegion.UsGov}>US Gov</option>
            </select>
          </Field>
          <Field label="Account Key">
            <input
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
                      value={form.proxyHost ?? ''}
                      onChange={(e) => set('proxyHost', e.target.value)}
                    />
                  </Field>
                  <Field label="Proxy Port">
                    <input
                      type="number"
                      value={form.proxyPort ?? ''}
                      onChange={(e) =>
                        set('proxyPort', e.target.value ? Number(e.target.value) : null)
                      }
                    />
                  </Field>
                  <Field label="Proxy Username">
                    <input
                      value={form.proxyUsername ?? ''}
                      onChange={(e) => set('proxyUsername', e.target.value)}
                    />
                  </Field>
                  <Field label="Proxy Password">
                    <input
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

          <div style={{ marginTop: '1rem' }}>
            <button type="button" onClick={save} disabled={busy}>
              {editing ? 'Save' : 'Create'}
            </button>{' '}
            <button type="button" onClick={test} disabled={busy}>
              Test Connection
            </button>{' '}
            <button type="button" onClick={() => setShowForm(false)} disabled={busy}>
              Cancel
            </button>
          </div>

          {testResult && (
            <p style={{ color: testResult.success ? 'green' : 'crimson' }}>
              {testResult.success
                ? 'Connection succeeded.'
                : `Connection failed: ${testResult.error}`}
            </p>
          )}
        </div>
      )}
    </section>
  )
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label style={{ display: 'flex', gap: '0.5rem', alignItems: 'center', margin: '0.4rem 0' }}>
      <span style={{ width: 140, display: 'inline-block' }}>{label}</span>
      {children}
    </label>
  )
}
