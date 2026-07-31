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
import { overlayStyle, panelStyle } from '../components/modalStyles'
import { Field } from '../components/Field'
import { ContainersPage } from './ContainersPage'

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

/// Accounts 现在是 Settings 里的一个区域（<see cref="SettingsPage"/>），不再是独立页面：
/// 账户是"配一次就不再碰"的东西，常驻一个顶级导航项名不副实。展开某个账户看 container 时
/// 仍然整片替换成 ContainersPage——那是一段有返回的下钻，嵌在区域里照旧成立。
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
    setBusy(true)
    setError(null)
    // 非 Global 分区仅代理下有效（PRD 1.1）：未启用代理则强制 Global。
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
        // 新账户创建后直接进入 container 列举界面（PRD 1.4）
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
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const test = async () => {
    setBusy(true)
    setTestResult(null)
    try {
      setTestResult(await accountsApi.testConnection(form))
    } catch (e) {
      setTestResult({ success: false, error: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  const set = <K extends keyof AccountInput>(k: K, v: AccountInput[K]) =>
    setForm((f) => ({ ...f, [k]: v }))

  // 密钥环丢失恢复(设计 §3.5)：重新录入账户密钥/代理密码，后端会连云验证后才落库。
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
      // 顶部横幅与备份页的顺序依赖都读同一份状态：重设成功后必须立刻刷新，
      // 否则横幅会一直挂着已经过期的告警(设计 §3.5)。
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
                    </button>{' '}
                    <button type="button" className="btn-ghost btn-danger" onClick={() => remove(a)}>
                      Delete
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
            {/* 非 Global 分区仅在启用代理时可选（PRD 1.1）。 */}
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

// 密钥环丢失恢复弹窗：重新录入账户密钥(必填)与代理密码(仅代理账户需要)。后端会用它连云验证，
// 验证通过才落库；验证失败以 400 携带 "Verification failed: ..." 返回，原样显示。
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
    <div className={overlayStyle} onClick={onClose}>
      <div className={panelStyle} onClick={(e) => e.stopPropagation()}>
        <h3>Re-enter Credentials — {account.name}</h3>
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

        <div className="row" style={{ marginTop: '1rem' }}>
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
        </div>
      </div>
    </div>
  )
}
