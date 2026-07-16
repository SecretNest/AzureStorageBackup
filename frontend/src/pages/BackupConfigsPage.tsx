import { useEffect, useState, type ReactNode } from 'react'
import { accountsApi, type Account } from '../api/accounts'
import {
  backupConfigsApi,
  StorageTier,
  RetentionMode,
  tierLabels,
  retentionModeLabels,
  backupStageLabels,
  type BackupConfig,
  type BackupConfigInput,
  type BackupRun,
  type RestoreRun,
  type CheckResult,
} from '../api/backupConfigs'

const MB = 1024 * 1024

const emptyForm: BackupConfigInput = {
  accountId: 0,
  containerName: '',
  name: '',
  description: '',
  localRoot: '',
  password: '',
  indexTier: StorageTier.Hot,
  dataTier: StorageTier.Hot,
  ignoreRules: '',
  dontCompressRules: '',
  dontGroupRules: '',
  includeSymlinks: false,
  maxVersions: 100,
  maxAgeDays: 180,
  retentionMode: RetentionMode.EitherTriggers,
  singleFileThresholdBytes: 5 * MB,
  groupCapBytes: 100 * MB,
}

const delay = (ms: number) => new Promise((r) => setTimeout(r, ms))

export function BackupConfigsPage() {
  const [configs, setConfigs] = useState<BackupConfig[]>([])
  const [accounts, setAccounts] = useState<Account[]>([])
  const [runs, setRuns] = useState<Record<number, BackupRun>>({})
  const [restores, setRestores] = useState<Record<number, RestoreRun>>({})
  const [checks, setChecks] = useState<Record<number, CheckResult | 'checking'>>({})
  const [showForm, setShowForm] = useState(false)
  const [editing, setEditing] = useState<BackupConfig | null>(null)
  const [step, setStep] = useState<1 | 2>(1)
  const [form, setForm] = useState<BackupConfigInput>(emptyForm)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = () => {
    backupConfigsApi.list().then(setConfigs).catch((e) => setError(String(e)))
  }
  useEffect(load, [])
  useEffect(() => {
    accountsApi.list().then(setAccounts).catch(() => {})
  }, [])

  const set = <K extends keyof BackupConfigInput>(k: K, v: BackupConfigInput[K]) =>
    setForm((f) => ({ ...f, [k]: v }))

  const startNew = () => {
    setEditing(null)
    setForm({ ...emptyForm, accountId: accounts[0]?.id ?? 0 })
    setStep(1)
    setError(null)
    setShowForm(true)
  }

  const startEdit = (c: BackupConfig) => {
    setEditing(c)
    setForm({
      accountId: c.accountId,
      containerName: c.containerName,
      name: c.name,
      description: c.description ?? '',
      localRoot: c.localRoot,
      password: '',
      indexTier: c.indexTier,
      dataTier: c.dataTier,
      ignoreRules: c.ignoreRules ?? '',
      dontCompressRules: c.dontCompressRules ?? '',
      dontGroupRules: c.dontGroupRules ?? '',
      includeSymlinks: c.includeSymlinks,
      maxVersions: c.maxVersions,
      maxAgeDays: c.maxAgeDays,
      retentionMode: c.retentionMode,
      singleFileThresholdBytes: c.singleFileThresholdBytes,
      groupCapBytes: c.groupCapBytes,
    })
    setStep(1)
    setError(null)
    setShowForm(true)
  }

  const save = async () => {
    setBusy(true)
    setError(null)
    try {
      if (editing) await backupConfigsApi.update(editing.id, form)
      else await backupConfigsApi.create(form)
      setShowForm(false)
      load()
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  const remove = async (c: BackupConfig) => {
    if (!window.confirm(`Delete backup "${c.name}"?`)) return
    try {
      await backupConfigsApi.remove(c.id)
      load()
    } catch (e) {
      setError(String(e))
    }
  }

  const run = async (c: BackupConfig) => {
    setError(null)
    try {
      let state = await backupConfigsApi.run(c.id)
      setRuns((r) => ({ ...r, [c.id]: state }))
      while (state.status === 'Running') {
        await delay(1000)
        state = await backupConfigsApi.runStatus(c.id)
        setRuns((r) => ({ ...r, [c.id]: state }))
      }
    } catch (e) {
      setError(String(e))
    }
  }

  const restore = async (c: BackupConfig) => {
    const target = window.prompt('Restore to which local path?', c.localRoot)
    if (target === null) return
    setError(null)
    try {
      let state = await backupConfigsApi.restore(c.id, target || null, null)
      setRestores((r) => ({ ...r, [c.id]: state }))
      while (state.status === 'Running') {
        await delay(1000)
        state = await backupConfigsApi.restoreStatus(c.id)
        setRestores((r) => ({ ...r, [c.id]: state }))
      }
    } catch (e) {
      setError(String(e))
    }
  }

  const check = async (c: BackupConfig) => {
    setError(null)
    setChecks((s) => ({ ...s, [c.id]: 'checking' }))
    try {
      const result = await backupConfigsApi.check(c.id)
      setChecks((s) => ({ ...s, [c.id]: result }))
    } catch (e) {
      setChecks((s) => {
        const { [c.id]: _removed, ...rest } = s
        return rest
      })
      setError(String(e))
    }
  }

  const accountName = (id: number) => accounts.find((a) => a.id === id)?.name ?? `#${id}`

  return (
    <section>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h1>Backups</h1>
        <button type="button" onClick={startNew} disabled={accounts.length === 0}>
          New Backup
        </button>
      </div>
      {accounts.length === 0 && <p style={{ color: '#666' }}>Add an account first.</p>}
      {error && <p style={{ color: 'crimson' }}>{error}</p>}

      <table style={{ width: '100%', borderCollapse: 'collapse', marginTop: '1rem' }}>
        <thead>
          <tr style={{ textAlign: 'left', borderBottom: '1px solid #ccc' }}>
            <th>Name</th>
            <th>Account / Container</th>
            <th>Local Root</th>
            <th>Encrypted</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {configs.length === 0 ? (
            <tr>
              <td colSpan={5} style={{ padding: '1rem 0', color: '#666' }}>
                No backups yet.
              </td>
            </tr>
          ) : (
            configs.map((c) => (
              <tr key={c.id} style={{ borderBottom: '1px solid #eee', verticalAlign: 'top' }}>
                <td>{c.name}</td>
                <td>
                  {accountName(c.accountId)} / {c.containerName}
                </td>
                <td style={{ fontFamily: 'monospace', fontSize: '0.85rem' }}>{c.localRoot}</td>
                <td>{c.hasPassword ? 'Yes' : 'No'}</td>
                <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>
                  <button
                    type="button"
                    onClick={() => run(c)}
                    disabled={runs[c.id]?.status === 'Running'}
                  >
                    {runs[c.id]?.status === 'Running' ? 'Running…' : 'Run'}
                  </button>{' '}
                  <button
                    type="button"
                    onClick={() => restore(c)}
                    disabled={restores[c.id]?.status === 'Running'}
                  >
                    {restores[c.id]?.status === 'Running' ? 'Restoring…' : 'Restore'}
                  </button>{' '}
                  <button
                    type="button"
                    onClick={() => check(c)}
                    disabled={checks[c.id] === 'checking'}
                  >
                    {checks[c.id] === 'checking' ? 'Checking…' : 'Check'}
                  </button>{' '}
                  <button type="button" onClick={() => startEdit(c)}>
                    Edit
                  </button>{' '}
                  <button type="button" onClick={() => remove(c)}>
                    Delete
                  </button>
                  {runs[c.id] && <RunStatus run={runs[c.id]} />}
                  {restores[c.id] && <RestoreStatus run={restores[c.id]} />}
                  {checks[c.id] && checks[c.id] !== 'checking' && (
                    <CheckStatus result={checks[c.id] as CheckResult} />
                  )}
                </td>
              </tr>
            ))
          )}
        </tbody>
      </table>

      {showForm && (
        <div style={{ marginTop: '1.5rem', padding: '1rem', border: '1px solid #ccc' }}>
          <h2>
            {editing ? `Edit: ${editing.name}` : 'New Backup'} — Step {step} of 2
          </h2>

          {step === 1 ? (
            <>
              <Field label="Account">
                <select
                  value={form.accountId}
                  disabled={!!editing}
                  onChange={(e) => set('accountId', Number(e.target.value))}
                >
                  {accounts.map((a) => (
                    <option key={a.id} value={a.id}>
                      {a.name}
                    </option>
                  ))}
                </select>
              </Field>
              <Field label="Container">
                <input
                  value={form.containerName}
                  disabled={!!editing}
                  onChange={(e) => set('containerName', e.target.value)}
                />
              </Field>
              <Field label="Local Root">
                <input
                  placeholder="/data/photos"
                  value={form.localRoot}
                  onChange={(e) => set('localRoot', e.target.value)}
                />
              </Field>
              <Field label="Name">
                <input value={form.name} onChange={(e) => set('name', e.target.value)} />
              </Field>
              <Field label="Description">
                <input
                  value={form.description ?? ''}
                  onChange={(e) => set('description', e.target.value)}
                />
              </Field>
              <Field label="Password">
                <input
                  type="password"
                  placeholder={
                    editing ? 'Leave blank to keep current' : 'Optional — set to encrypt'
                  }
                  value={form.password ?? ''}
                  onChange={(e) => set('password', e.target.value)}
                />
              </Field>
              <Field label="Index Tier">
                <TierSelect value={form.indexTier} onChange={(v) => set('indexTier', v)} archive={false} />
              </Field>
              <Field label="Data Tier">
                <TierSelect value={form.dataTier} onChange={(v) => set('dataTier', v)} archive />
              </Field>

              <div style={{ marginTop: '1rem' }}>
                <button type="button" onClick={() => setStep(2)}>
                  Next
                </button>{' '}
                <button type="button" onClick={() => setShowForm(false)}>
                  Cancel
                </button>
              </div>
            </>
          ) : (
            <>
              <Field label="Ignore rules">
                <RuleBox value={form.ignoreRules} onChange={(v) => set('ignoreRules', v)} />
              </Field>
              <Field label="Don't compress">
                <RuleBox
                  value={form.dontCompressRules}
                  onChange={(v) => set('dontCompressRules', v)}
                />
              </Field>
              <Field label="Don't group">
                <RuleBox value={form.dontGroupRules} onChange={(v) => set('dontGroupRules', v)} />
              </Field>
              <Field label="Include symlinks">
                <input
                  type="checkbox"
                  checked={form.includeSymlinks}
                  onChange={(e) => set('includeSymlinks', e.target.checked)}
                />
              </Field>
              <Field label="Max versions">
                <input
                  type="number"
                  value={form.maxVersions}
                  onChange={(e) => set('maxVersions', Number(e.target.value))}
                />
              </Field>
              <Field label="Max age (days)">
                <input
                  type="number"
                  value={form.maxAgeDays}
                  onChange={(e) => set('maxAgeDays', Number(e.target.value))}
                />
              </Field>
              <Field label="Retention mode">
                <select
                  value={form.retentionMode}
                  onChange={(e) => set('retentionMode', Number(e.target.value))}
                >
                  {Object.entries(retentionModeLabels).map(([v, label]) => (
                    <option key={v} value={v}>
                      {label}
                    </option>
                  ))}
                </select>
              </Field>
              <Field label="Single-file threshold (MB)">
                <input
                  type="number"
                  value={Math.round(form.singleFileThresholdBytes / MB)}
                  onChange={(e) => set('singleFileThresholdBytes', Number(e.target.value) * MB)}
                />
              </Field>
              <Field label="Group cap (MB)">
                <input
                  type="number"
                  value={Math.round(form.groupCapBytes / MB)}
                  onChange={(e) => set('groupCapBytes', Number(e.target.value) * MB)}
                />
              </Field>

              <div style={{ marginTop: '1rem' }}>
                <button type="button" onClick={() => setStep(1)}>
                  Back
                </button>{' '}
                <button type="button" onClick={save} disabled={busy}>
                  {editing ? 'Save' : 'Create'}
                </button>{' '}
                <button type="button" onClick={() => setShowForm(false)} disabled={busy}>
                  Cancel
                </button>
              </div>
            </>
          )}
        </div>
      )}
    </section>
  )
}

function RunStatus({ run }: { run: BackupRun }) {
  if (run.status === 'Failed')
    return <div style={{ color: 'crimson', fontSize: '0.8rem' }}>Failed: {run.error}</div>
  if (run.status === 'Completed')
    return (
      <div style={{ color: 'green', fontSize: '0.8rem' }}>
        Completed — version {run.version}
      </div>
    )
  const p = run.progress
  return (
    <div style={{ fontSize: '0.8rem', color: '#555' }}>
      {p ? `${backupStageLabels[p.stage]} ${p.percent}% (${p.changedFiles} changed)` : 'Starting…'}
    </div>
  )
}

function CheckStatus({ result }: { result: CheckResult }) {
  return result.ok ? (
    <div style={{ color: 'green', fontSize: '0.8rem' }}>
      Check OK — {result.checkedRefs} object(s), version {result.version}
    </div>
  ) : (
    <div style={{ color: 'crimson', fontSize: '0.8rem' }}>
      Check failed — {result.missingRefs.length} missing: {result.missingRefs.join(', ')}
    </div>
  )
}

function RestoreStatus({ run }: { run: RestoreRun }) {
  if (run.status === 'Failed')
    return <div style={{ color: 'crimson', fontSize: '0.8rem' }}>Restore failed: {run.error}</div>
  if (run.status === 'Completed')
    return (
      <div style={{ color: 'green', fontSize: '0.8rem' }}>
        Restored {run.restoredFiles} file(s), skipped {run.skippedFiles} — version {run.version}
      </div>
    )
  return <div style={{ fontSize: '0.8rem', color: '#555' }}>Restoring…</div>
}

function TierSelect({
  value,
  onChange,
  archive,
}: {
  value: number
  onChange: (v: number) => void
  archive: boolean
}) {
  return (
    <select value={value} onChange={(e) => onChange(Number(e.target.value))}>
      <option value={StorageTier.Hot}>{tierLabels[StorageTier.Hot]}</option>
      <option value={StorageTier.Cool}>{tierLabels[StorageTier.Cool]}</option>
      <option value={StorageTier.Cold}>{tierLabels[StorageTier.Cold]}</option>
      {archive && <option value={StorageTier.Archive}>{tierLabels[StorageTier.Archive]}</option>}
    </select>
  )
}

function RuleBox({ value, onChange }: { value: string | null; onChange: (v: string) => void }) {
  return (
    <textarea
      rows={3}
      placeholder="gitignore syntax, one per line"
      style={{ width: 320, fontFamily: 'monospace', fontSize: '0.85rem' }}
      value={value ?? ''}
      onChange={(e) => onChange(e.target.value)}
    />
  )
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label style={{ display: 'flex', gap: '0.5rem', alignItems: 'center', margin: '0.4rem 0' }}>
      <span style={{ width: 200, display: 'inline-block' }}>{label}</span>
      {children}
    </label>
  )
}
