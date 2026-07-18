import { useEffect, useState, type CSSProperties, type ReactNode } from 'react'
import { accountsApi, type Account } from '../api/accounts'
import { settingsApi, type GlobalSettings } from '../api/settings'
import {
  backupConfigsApi,
  StorageTier,
  RetentionMode,
  CloudCheckLevel,
  LocalCheckLevel,
  CloudState,
  LocalState,
  BackupStatus,
  tierLabels,
  retentionModeLabels,
  backupStageLabels,
  type BackupConfig,
  type BackupConfigInput,
  type BackupRun,
  type RestoreRun,
  type CheckReport,
  type RepairRun,
  type FileVersionOption,
} from '../api/backupConfigs'

const cloudLevelLabels: Record<number, string> = {
  [CloudCheckLevel.None]: "Don't check cloud",
  [CloudCheckLevel.Metadata]: 'Metadata vs local cache',
  [CloudCheckLevel.ExistenceSize]: 'Existence + size',
  [CloudCheckLevel.Content]: 'Content (download + hash)',
}
const localLevelLabels: Record<number, string> = {
  [LocalCheckLevel.None]: "Don't check local",
  [LocalCheckLevel.Attributes]: 'Existence + size + permissions',
  [LocalCheckLevel.Content]: 'Content hash',
}
const cloudStateLabel = (s: number) =>
  s === CloudState.Ok ? 'OK' : s === CloudState.MissingOrBad ? 'MISSING/BAD' : '—'
const localStateLabel = (s: number) =>
  s === LocalState.Ok ? 'OK' : s === LocalState.Missing ? 'missing' : s === LocalState.Changed ? 'changed' : '—'

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
  volumeBytes: null,
  verboseLogging: false,
}

const delay = (ms: number) => new Promise((r) => setTimeout(r, ms))

export function BackupConfigsPage() {
  const [configs, setConfigs] = useState<BackupConfig[]>([])
  const [accounts, setAccounts] = useState<Account[]>([])
  const [runs, setRuns] = useState<Record<number, BackupRun>>({})
  const [restores, setRestores] = useState<Record<number, RestoreRun>>({})
  const [checkModal, setCheckModal] = useState<BackupConfig | null>(null)
  const [restoreModal, setRestoreModal] = useState<BackupConfig | null>(null)
  const [showForm, setShowForm] = useState(false)
  const [editing, setEditing] = useState<BackupConfig | null>(null)
  const [step, setStep] = useState<1 | 2>(1)
  const [form, setForm] = useState<BackupConfigInput>(emptyForm)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = () => {
    backupConfigsApi.list().then(setConfigs).catch((e) => setError(String(e)))
  }
  const [defaults, setDefaults] = useState<GlobalSettings | null>(null)
  useEffect(load, [])
  useEffect(() => {
    accountsApi.list().then(setAccounts).catch(() => {})
    settingsApi.get().then(setDefaults).catch(() => {})
  }, [])

  const set = <K extends keyof BackupConfigInput>(k: K, v: BackupConfigInput[K]) =>
    setForm((f) => ({ ...f, [k]: v }))

  const startNew = () => {
    setEditing(null)
    // 用全局设置的默认值预填（PRD §11「使用默认」）
    const d = defaults
    setForm({
      ...emptyForm,
      accountId: accounts[0]?.id ?? 0,
      ...(d && {
        indexTier: d.defaultIndexTier,
        dataTier: d.defaultDataTier,
        maxVersions: d.defaultMaxVersions,
        maxAgeDays: d.defaultMaxAgeDays,
        retentionMode: d.defaultRetentionMode,
        singleFileThresholdBytes: d.defaultSingleFileThresholdBytes,
        groupCapBytes: d.defaultGroupCapBytes,
        volumeBytes: d.defaultVolumeBytes,
        verboseLogging: d.defaultVerboseLogging,
        includeSymlinks: d.defaultIncludeSymlinks,
        ignoreRules: d.defaultIgnoreRules ?? '',
        dontCompressRules: d.defaultDontCompressRules ?? '',
        dontGroupRules: d.defaultDontGroupRules ?? '',
      }),
    })
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
      verboseLogging: c.verboseLogging,
      maxVersions: c.maxVersions,
      maxAgeDays: c.maxAgeDays,
      retentionMode: c.retentionMode,
      singleFileThresholdBytes: c.singleFileThresholdBytes,
      groupCapBytes: c.groupCapBytes,
      volumeBytes: c.volumeBytes,
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

  const resetStatus = async (c: BackupConfig) => {
    try {
      await backupConfigsApi.resetStatus(c.id)
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

  const pollRestore = async (id: number, state: RestoreRun) => {
    setRestores((r) => ({ ...r, [id]: state }))
    while (state.status === 'Running') {
      await delay(1000)
      state = await backupConfigsApi.restoreStatus(id)
      setRestores((r) => ({ ...r, [id]: state }))
    }
  }

  const [importing, setImporting] = useState(false)
  const [importForm, setImportForm] = useState({ accountId: 0, containerName: '', password: '' })
  const doImport = async () => {
    setError(null)
    try {
      await backupConfigsApi.import(
        importForm.accountId || accounts[0]?.id || 0,
        importForm.containerName,
        importForm.password || null,
      )
      setImporting(false)
      setImportForm({ accountId: 0, containerName: '', password: '' })
      load()
    } catch (e) {
      setError(String(e))
    }
  }

  const accountName = (id: number) => accounts.find((a) => a.id === id)?.name ?? `#${id}`

  return (
    <section>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h1>Backups</h1>
        <div>
          <button type="button" onClick={() => setImporting((v) => !v)} disabled={accounts.length === 0}>
            Import existing
          </button>{' '}
          <button type="button" onClick={startNew} disabled={accounts.length === 0}>
            New Backup
          </button>
        </div>
      </div>

      {importing && (
        <div style={{ margin: '1rem 0', padding: '0.8rem', border: '1px solid #ccc' }}>
          <strong>Import existing backup</strong> (reads the container's info file)
          <div style={{ display: 'flex', gap: '0.5rem', marginTop: '0.5rem', flexWrap: 'wrap' }}>
            <select
              value={importForm.accountId || accounts[0]?.id || 0}
              onChange={(e) => setImportForm((f) => ({ ...f, accountId: Number(e.target.value) }))}
            >
              {accounts.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.name}
                </option>
              ))}
            </select>
            <input
              placeholder="container name"
              value={importForm.containerName}
              onChange={(e) => setImportForm((f) => ({ ...f, containerName: e.target.value }))}
            />
            <input
              type="password"
              placeholder="password (if encrypted)"
              value={importForm.password}
              onChange={(e) => setImportForm((f) => ({ ...f, password: e.target.value }))}
            />
            <button type="button" onClick={doImport} disabled={!importForm.containerName}>
              Import
            </button>
          </div>
        </div>
      )}
      {accounts.length === 0 && <p style={{ color: '#666' }}>Add an account first.</p>}
      {error && <p style={{ color: 'crimson' }}>{error}</p>}

      <table style={{ width: '100%', borderCollapse: 'collapse', marginTop: '1rem' }}>
        <thead>
          <tr style={{ textAlign: 'left', borderBottom: '1px solid #ccc' }}>
            <th>Name</th>
            <th>Account / Container</th>
            <th>Local Root</th>
            <th>Encrypted</th>
            <th>Status</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {configs.length === 0 ? (
            <tr>
              <td colSpan={6} style={{ padding: '1rem 0', color: '#666' }}>
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
                <td>
                  <StatusBadge config={c} onReset={() => resetStatus(c)} />
                </td>
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
                    onClick={() => setRestoreModal(c)}
                    disabled={restores[c.id]?.status === 'Running'}
                  >
                    {restores[c.id]?.status === 'Running' ? 'Restoring…' : 'Restore…'}
                  </button>{' '}
                  <button type="button" onClick={() => setCheckModal(c)}>
                    Check / Repair…
                  </button>{' '}
                  <button type="button" onClick={() => startEdit(c)}>
                    Edit
                  </button>{' '}
                  <button type="button" onClick={() => remove(c)}>
                    Delete
                  </button>
                  {runs[c.id] && <RunStatus run={runs[c.id]} />}
                  {restores[c.id] && <RestoreStatus run={restores[c.id]} />}
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
              <Field label={editing ? 'Account (locked)' : 'Account'}>
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
              <Field label={editing ? 'Container (locked)' : 'Container'}>
                <input
                  value={form.containerName}
                  disabled={!!editing}
                  onChange={(e) => set('containerName', e.target.value)}
                />
              </Field>
              <Field label={editing ? 'Local Root (locked)' : 'Local Root'}>
                <input
                  placeholder="/data/photos"
                  value={form.localRoot}
                  disabled={!!editing}
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
              <Field label={editing ? 'Password (locked)' : 'Password'}>
                <input
                  type="password"
                  placeholder={
                    editing
                      ? editing.hasPassword
                        ? 'Encrypted — cannot be changed after creation'
                        : 'Not encrypted — cannot be changed after creation'
                      : 'Optional — set to encrypt'
                  }
                  value={form.password ?? ''}
                  disabled={!!editing}
                  onChange={(e) => set('password', e.target.value)}
                />
              </Field>
              <Field label={editing ? 'Index Tier (locked)' : 'Index Tier'}>
                <TierSelect
                  value={form.indexTier}
                  onChange={(v) => set('indexTier', v)}
                  archive={false}
                  disabled={!!editing}
                />
              </Field>
              <Field label={editing ? 'Data Tier (locked)' : 'Data Tier'}>
                <TierSelect
                  value={form.dataTier}
                  onChange={(v) => set('dataTier', v)}
                  archive
                  disabled={!!editing}
                />
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
              <Field label="Verbose (debug) logging">
                <input
                  type="checkbox"
                  checked={form.verboseLogging}
                  onChange={(e) => set('verboseLogging', e.target.checked)}
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
              <Field label="Volume size (MB, 0=off)">
                <input
                  type="number"
                  value={form.volumeBytes ? Math.round(form.volumeBytes / MB) : 0}
                  onChange={(e) =>
                    set('volumeBytes', Number(e.target.value) > 0 ? Number(e.target.value) * MB : null)
                  }
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

      {checkModal && (
        <CheckModal
          config={checkModal}
          onClose={() => setCheckModal(null)}
          onError={(e) => setError(e)}
        />
      )}
      {restoreModal && (
        <RestoreModal
          config={restoreModal}
          onClose={() => setRestoreModal(null)}
          onError={(e) => setError(e)}
          onStarted={(state) => {
            const id = restoreModal.id
            setRestoreModal(null)
            void pollRestore(id, state)
          }}
        />
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

// 状态徽标（§4.2 决策 2）：进行中（蓝，派生 activity）优先于持久 Error（红，tooltip + Reset）；否则不显示。
function StatusBadge({ config, onReset }: { config: BackupConfig; onReset: () => void }) {
  if (config.activity !== 'Idle') {
    return (
      <span style={{ color: '#1a73e8', fontSize: '0.8rem', fontWeight: 600 }}>
        {config.activity}
      </span>
    )
  }
  if (config.status === BackupStatus.Error) {
    return (
      <span style={{ display: 'inline-flex', alignItems: 'center', gap: '0.4rem' }}>
        <span
          title={config.lastError ?? 'Unknown error'}
          style={{ color: 'crimson', fontSize: '0.8rem', fontWeight: 600, cursor: 'help' }}
        >
          Error
        </span>
        <button type="button" onClick={onReset} style={{ fontSize: '0.75rem' }}>
          Reset
        </button>
      </span>
    )
  }
  return <span style={{ color: '#999', fontSize: '0.8rem' }}>—</span>
}

function RestoreStatus({ run }: { run: RestoreRun }) {
  if (run.status === 'Failed')
    return <div style={{ color: 'crimson', fontSize: '0.8rem' }}>Restore failed: {run.error}</div>
  if (run.status === 'Completed')
    return (
      <div style={{ color: run.failedFiles ? 'darkorange' : 'green', fontSize: '0.8rem' }}>
        Restored {run.restoredFiles} file(s), skipped {run.skippedFiles}
        {run.failedFiles ? `, failed ${run.failedFiles}` : ''} — version {run.version}
      </div>
    )
  return <div style={{ fontSize: '0.8rem', color: '#555' }}>{run.phase || 'Restoring…'}</div>
}

function TierSelect({
  value,
  onChange,
  archive,
  disabled,
}: {
  value: number
  onChange: (v: number) => void
  archive: boolean
  disabled?: boolean
}) {
  return (
    <select value={value} disabled={disabled} onChange={(e) => onChange(Number(e.target.value))}>
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

const overlayStyle: CSSProperties = {
  position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)',
  display: 'flex', alignItems: 'flex-start', justifyContent: 'center', paddingTop: '4vh', zIndex: 50,
}
const panelStyle: CSSProperties = {
  background: '#fff', padding: '1.5rem', borderRadius: 6, minWidth: 620, maxWidth: '90vw',
  maxHeight: '88vh', overflow: 'auto',
}

function CheckModal({
  config, onClose, onError,
}: { config: BackupConfig; onClose: () => void; onError: (e: string) => void }) {
  const [versions, setVersions] = useState<number[]>([])
  const [version, setVersion] = useState<number | null>(null)
  const [cloud, setCloud] = useState<number>(CloudCheckLevel.ExistenceSize)
  const [local, setLocal] = useState<number>(LocalCheckLevel.Content)
  const [rehydrate, setRehydrate] = useState<number | null>(null)
  const [listOrphans, setListOrphans] = useState(false)
  const [running, setRunning] = useState(false)
  const [report, setReport] = useState<CheckReport | null>(null)
  const [repairing, setRepairing] = useState(false)
  const [repairReport, setRepairReport] = useState<RepairRun | null>(null)

  useEffect(() => {
    backupConfigsApi.versions(config.id).then((vs) => setVersions(vs.map((v) => v.version))).catch(() => {})
  }, [config.id])

  const rehydrateArg = () => (cloud === CloudCheckLevel.Content ? rehydrate : null)

  const runCheck = async () => {
    setRunning(true)
    setRepairReport(null)
    try {
      setReport(await backupConfigsApi.check(config.id, cloud, local, version, rehydrateArg(), listOrphans))
    } catch (e) {
      onError(String(e))
    } finally {
      setRunning(false)
    }
  }

  const runRepair = async () => {
    setRepairing(true)
    try {
      // 修复是后台 job（持锁到完成）；轮询状态。
      let run = await backupConfigsApi.repair(config.id, cloud, version, rehydrateArg(), listOrphans)
      setRepairReport(run)
      while (run.status === 'Running') {
        await delay(1500)
        run = await backupConfigsApi.repairStatus(config.id)
        setRepairReport(run)
      }
      if (run.status === 'Completed')
        setReport(await backupConfigsApi.check(config.id, cloud, local, version, rehydrateArg(), listOrphans))
      else if (run.error) onError(run.error)
    } catch (e) {
      onError(String(e))
    } finally {
      setRepairing(false)
    }
  }

  const problems = report ? report.findings.filter((f) => f.cloud === CloudState.MissingOrBad) : []

  return (
    <div style={overlayStyle} onClick={onClose}>
      <div style={panelStyle} onClick={(e) => e.stopPropagation()}>
        <h3 style={{ marginTop: 0 }}>Check / Repair — {config.name}</h3>

        <Field label="Version">
          <select value={version ?? ''} onChange={(e) => setVersion(e.target.value === '' ? null : Number(e.target.value))}>
            <option value="">Latest</option>
            {versions.map((v) => <option key={v} value={v}>{v}</option>)}
          </select>
        </Field>
        <Field label="Cloud check">
          <select value={cloud} onChange={(e) => setCloud(Number(e.target.value))}>
            {Object.entries(cloudLevelLabels).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
          </select>
        </Field>
        <Field label="Local check">
          <select value={local} onChange={(e) => setLocal(Number(e.target.value))}>
            {Object.entries(localLevelLabels).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
          </select>
        </Field>
        {cloud === CloudCheckLevel.Content && (
          <Field label="Rehydrate Archive to">
            <select value={rehydrate ?? ''} onChange={(e) => setRehydrate(e.target.value === '' ? null : Number(e.target.value))}>
              <option value="">Don't rehydrate</option>
              <option value={StorageTier.Hot}>Hot</option>
              <option value={StorageTier.Cool}>Cool</option>
            </select>
          </Field>
        )}
        <Field label="Unreferenced blobs">
          <label style={{ fontSize: '0.85rem' }}>
            <input type="checkbox" checked={listOrphans} onChange={(e) => setListOrphans(e.target.checked)} />
            {' '}Detect unreferenced blobs (repair deletes them)
          </label>
        </Field>

        <div style={{ margin: '0.8rem 0' }}>
          <button type="button" onClick={runCheck} disabled={running || repairing}>
            {running ? 'Checking…' : 'Run check'}
          </button>{' '}
          {(problems.some((f) => f.repairable) || (report?.orphanBlobs?.length ?? 0) > 0) && (
            <button type="button" onClick={runRepair} disabled={repairing || running}>
              {repairing ? 'Repairing…' : 'Repair from local'}
            </button>
          )}{' '}
          <button type="button" onClick={onClose}>Close</button>
        </div>

        {repairReport && (
          <div style={{ fontSize: '0.85rem', marginBottom: '0.6rem' }}>
            {repairReport.status === 'Running' && 'Repairing (backup is locked until done)…'}
            {repairReport.status === 'Failed' && <span style={{ color: 'crimson' }}>Repair failed: {repairReport.error}</span>}
            {repairReport.status === 'Completed' && (
              <>
                Repaired {repairReport.repaired?.length ?? 0} file(s);{' '}
                <span style={{ color: repairReport.unrecoverable?.length ? 'crimson' : 'inherit' }}>
                  {repairReport.unrecoverable?.length ?? 0} unrecoverable
                </span>
                {(repairReport.unrecoverable?.length ?? 0) > 0 && `: ${repairReport.unrecoverable!.join(', ')}`}
                {(repairReport.deletedOrphans?.length ?? 0) > 0 &&
                  `; deleted ${repairReport.deletedOrphans!.length} unreferenced blob(s)`}
              </>
            )}
          </div>
        )}

        {report && (
          <div>
            {report.metadataIssue && (
              <div style={{ color: 'crimson', fontSize: '0.85rem' }}>Metadata drift: {report.metadataIssue}</div>
            )}
            <div style={{ fontSize: '0.85rem', margin: '0.4rem 0', color: report.ok ? 'green' : 'crimson' }}>
              {report.ok ? 'All checked objects OK' : `${problems.length} problem(s), ${report.repairablePaths.length} repairable from local`}
              {' '}(version {report.version})
            </div>
            {listOrphans && (
              <div style={{ fontSize: '0.85rem', margin: '0.4rem 0', color: report.orphanBlobs.length ? '#b06a00' : 'green' }}>
                {report.orphanBlobs.length === 0
                  ? 'No unreferenced blobs found'
                  : `${report.orphanBlobs.length} unreferenced blob(s) — repair will delete: ${report.orphanBlobs.slice(0, 20).join(', ')}${report.orphanBlobs.length > 20 ? '…' : ''}`}
              </div>
            )}
            {problems.length > 0 && (
              <table style={{ width: '100%', fontSize: '0.8rem', borderCollapse: 'collapse' }}>
                <thead><tr><th style={{ textAlign: 'left' }}>File</th><th>Cloud</th><th>Local</th><th>Repairable</th></tr></thead>
                <tbody>
                  {problems.map((f) => (
                    <tr key={f.path}>
                      <td style={{ fontFamily: 'monospace' }}>{f.path}</td>
                      <td style={{ textAlign: 'center', color: 'crimson' }}>{cloudStateLabel(f.cloud)}</td>
                      <td style={{ textAlign: 'center' }}>{localStateLabel(f.local)}</td>
                      <td style={{ textAlign: 'center' }}>{f.repairable ? 'yes' : 'no'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>
        )}
      </div>
    </div>
  )
}

function RestoreModal({
  config, onClose, onError, onStarted,
}: { config: BackupConfig; onClose: () => void; onError: (e: string) => void; onStarted: (s: RestoreRun) => void }) {
  const [versions, setVersions] = useState<number[]>([])
  const [version, setVersion] = useState<number | null>(null)
  const [target, setTarget] = useState(config.localRoot)
  const [unrecoverable, setUnrecoverable] = useState<string[]>([])
  const [options, setOptions] = useState<Record<string, FileVersionOption[]>>({})
  const [choices, setChoices] = useState<Record<string, number>>({})
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    backupConfigsApi.versions(config.id).then((vs) => setVersions(vs.map((v) => v.version))).catch(() => {})
  }, [config.id])

  useEffect(() => {
    let cancelled = false
    setLoading(true)
    ;(async () => {
      try {
        const paths = await backupConfigsApi.unrecoverablePaths(config.id, version)
        if (cancelled) return
        setUnrecoverable(paths)
        const opts: Record<string, FileVersionOption[]> = {}
        const ch: Record<string, number> = {}
        for (const p of paths) {
          const cands = await backupConfigsApi.fileVersions(config.id, p)
          opts[p] = cands
          ch[p] = cands.length > 0 ? cands[0].version : 0 // 0 = skip
        }
        if (cancelled) return
        setOptions(opts)
        setChoices(ch)
      } catch (e) {
        if (!cancelled) onError(String(e))
      } finally {
        if (!cancelled) setLoading(false)
      }
    })()
    return () => { cancelled = true }
  }, [config.id, version, onError])

  const setAllNearest = () => {
    const ch: Record<string, number> = {}
    for (const p of unrecoverable) ch[p] = options[p]?.length ? options[p][0].version : 0
    setChoices(ch)
  }

  const start = async () => {
    try {
      const subs: Record<string, number> = {}
      for (const [p, v] of Object.entries(choices)) if (v > 0) subs[p] = v
      const state = await backupConfigsApi.restore(config.id, target || null, version, subs)
      onStarted(state)
    } catch (e) {
      onError(String(e))
    }
  }

  return (
    <div style={overlayStyle} onClick={onClose}>
      <div style={panelStyle} onClick={(e) => e.stopPropagation()}>
        <h3 style={{ marginTop: 0 }}>Restore — {config.name}</h3>
        <Field label="Restore to">
          <input value={target} onChange={(e) => setTarget(e.target.value)} style={{ width: 340 }} />
        </Field>
        <Field label="Version">
          <select value={version ?? ''} onChange={(e) => setVersion(e.target.value === '' ? null : Number(e.target.value))}>
            <option value="">Latest</option>
            {versions.map((v) => <option key={v} value={v}>{v}</option>)}
          </select>
        </Field>

        {loading && <div style={{ fontSize: '0.85rem' }}>Loading…</div>}
        {!loading && unrecoverable.length > 0 && (
          <div style={{ margin: '0.6rem 0' }}>
            <div style={{ fontSize: '0.85rem', marginBottom: '0.3rem' }}>
              {unrecoverable.length} unrecoverable file(s) in this version — choose a version to substitute (or skip):
              {' '}<button type="button" onClick={setAllNearest}>Set all to nearest</button>
            </div>
            <table style={{ width: '100%', fontSize: '0.8rem', borderCollapse: 'collapse' }}>
              <thead><tr><th style={{ textAlign: 'left' }}>File</th><th>Substitute from</th></tr></thead>
              <tbody>
                {unrecoverable.map((p) => (
                  <tr key={p}>
                    <td style={{ fontFamily: 'monospace' }}>{p}</td>
                    <td style={{ textAlign: 'center' }}>
                      <select value={choices[p] ?? 0} onChange={(e) => setChoices((c) => ({ ...c, [p]: Number(e.target.value) }))}>
                        <option value={0}>Skip (don't restore)</option>
                        {(options[p] ?? []).map((o) => <option key={o.version} value={o.version}>Version {o.version}</option>)}
                      </select>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        <div style={{ marginTop: '0.8rem' }}>
          <button type="button" onClick={start} disabled={loading}>Start restore</button>{' '}
          <button type="button" onClick={onClose}>Cancel</button>
        </div>
      </div>
    </div>
  )
}
