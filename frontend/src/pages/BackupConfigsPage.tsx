import { useEffect, useRef, useState } from 'react'
import { accountsApi, type Account } from '../api/accounts'
import { refreshKeyringStatus, useKeyringStatus } from '../api/keyring'
import { settingsApi, type GlobalSettings } from '../api/settings'
import { DefaultableField } from '../components/DefaultableField'
import { PathBrowser } from '../components/PathBrowser'
import { RestoreDialog } from '../components/RestoreDialog'
import { Field } from '../components/modal'
import { overlayStyle, panelStyle } from '../components/modalStyles'
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
  type BackupActivity,
  type BackupConfig,
  type BackupConfigInput,
  type BackupRun,
  type RestoreRun,
  type CheckReport,
  type RepairRun,
} from '../api/backupConfigs'
import {
  containersApi,
  validateContainerName,
  containerNameRule,
  BackupPresence,
  type ContainerInfo,
} from '../api/containers'

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
  ignoreRules: null,
  dontCompressRules: null,
  dontGroupRules: null,
  includeSymlinks: null,
  maxVersions: null,
  maxAgeDays: null,
  retentionMode: null,
  singleFileThresholdBytes: null,
  groupCapBytes: null,
  volumeBytes: null,
  verboseLogging: null,
}

const delay = (ms: number) => new Promise((r) => setTimeout(r, ms))

export function BackupConfigsPage() {
  const [configs, setConfigs] = useState<BackupConfig[]>([])
  const [accounts, setAccounts] = useState<Account[]>([])
  const [runs, setRuns] = useState<Record<number, BackupRun>>({})
  const [restores, setRestores] = useState<Record<number, RestoreRun>>({})
  const [repairs, setRepairs] = useState<Record<number, RepairRun>>({})
  const [checkModal, setCheckModal] = useState<BackupConfig | null>(null)
  const [restoreModal, setRestoreModal] = useState<BackupConfig | null>(null)
  const [deleteModal, setDeleteModal] = useState<BackupConfig | null>(null)
  const [showForm, setShowForm] = useState(false)
  const [browsing, setBrowsing] = useState(false)
  const [editing, setEditing] = useState<BackupConfig | null>(null)
  const [step, setStep] = useState<1 | 2>(1)
  const [form, setForm] = useState<BackupConfigInput>(emptyForm)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [postCreate, setPostCreate] = useState<BackupConfig | null>(null)
  const [resettingPassword, setResettingPassword] = useState<BackupConfig | null>(null)
  const keyring = useKeyringStatus()

  const load = () => {
    backupConfigsApi.list().then(setConfigs).catch((e) => setError(e instanceof Error ? e.message : String(e)))
  }
  const [defaults, setDefaults] = useState<GlobalSettings | null>(null)
  // 选定账户后列举其容器（PRD 1.2 的接口，ContainersPage 已在用）。
  // 列举要连云，失败不能挡住新建备份——降级为纯输入框。
  const [containerList, setContainerList] = useState<ContainerInfo[] | null>(null)
  const [containerListError, setContainerListError] = useState<string | null>(null)
  const [newContainer, setNewContainer] = useState(false)
  useEffect(load, [])

  // 供下面几个 effect 的 tick/cleanup 读取「当下最新」的 configs/restores，而不必把它们
  // 放进依赖数组去触发 interval 重建——渲染期间直接赋值，提交后 effect 看到的必是最新值。
  const configsRef = useRef(configs)
  configsRef.current = configs
  const restoresRef = useRef(restores)
  restoresRef.current = restores

  // 列表每 5 秒刷一次：纯本地查询（配置行 + 内存中的 activity），不连云。
  // 这是无人触发的后台刷新：一次网络抖动不该弹一条错误横幅——它可能盖掉用户正在看的
  // 另一条错误，且用户对这次刷新没有可做的动作。下一拍会自然重试(Fix 7)。
  useEffect(() => {
    const t = setInterval(() => {
      backupConfigsApi.list().then(setConfigs).catch(() => {})
    }, 5000)
    return () => clearInterval(t)
  }, [])

  // 有活跃项时，只对活跃的那几份、且只拉该 activity 对应的那一个端点。
  // 全空闲时不发这些请求。取代闭包里的循环：状态来自服务端，因此刷新页面、换标签页、
  // 或备份由定时任务发起，看到的都一样。
  // 配置列表每次 load() 返回时都用新数组引用替换，导致这个 effect 每 5 秒重建一次
  // 及至更早的用户动作。改用派生的 activeKey（只包含活跃配置的 id 和 activity），
  // 仅在实际有活动的配置改变时才重建 interval。
  const activeKey = configs
    .filter((c) => c.activity !== 'Idle')
    .map((c) => `${c.id}:${c.activity}`)
    .join(',')

  useEffect(() => {
    if (!activeKey) return

    let cancelled = false
    // 从 activeKey 字符串解析出 id 和 activity，避免依赖 configs 数组引用
    const activeList = activeKey.split(',').map((item) => {
      const [id, activity] = item.split(':')
      // 断言回 BackupActivity：不断言的话 activity 是 string，下面几处与字面量的比较
      // 就不再受编译器检查，写错一个字母会静默地不轮询那一类，而不是编译失败。
      return { id: Number(id), activity: activity as BackupActivity }
    })

    // tick 本身是 async 的：一拍还没跑完（比如状态请求撞上 7z 正在吃满 CPU 那阵、比 1 秒慢）
    // 就不该再叠加下一拍——叠起来的请求会顶着浏览器同源并发上限排队，连 5 秒的列表刷新都会
    // 被一起拖住，页面恰恰在最需要更新时停摆(Fix 6)。
    let inFlight = false

    const tick = async () => {
      if (inFlight) return
      inFlight = true
      try {
        await Promise.all(
          activeList.map(async (item) => {
            try {
              const tasks: Promise<void>[] = []
              if (item.activity === 'BackingUp') {
                tasks.push(
                  backupConfigsApi.runStatus(item.id).then((s) => {
                    if (!cancelled) setRuns((r) => ({ ...r, [item.id]: s }))
                  }),
                )
                // activity 是单值：并发还原时会被 BackingUp 盖住(见 RestoreRunner.cs 顶部注释——
                // 还原不占忙碌锁，允许与备份并行)。本地若还记得这个配置的还原仍在跑，就不管
                // activity 怎么说，独立地把还原状态也拉一次，否则并发结束时看不到还原的终态(Fix 2)。
                if (restoresRef.current[item.id]?.status === 'Running') {
                  tasks.push(
                    backupConfigsApi.restoreStatus(item.id).then((s) => {
                      if (!cancelled) setRestores((r) => ({ ...r, [item.id]: s }))
                    }),
                  )
                }
              } else if (item.activity === 'Restoring') {
                tasks.push(
                  backupConfigsApi.restoreStatus(item.id).then((s) => {
                    if (!cancelled) setRestores((r) => ({ ...r, [item.id]: s }))
                  }),
                )
              } else if (item.activity === 'Repairing') {
                tasks.push(
                  backupConfigsApi.repairStatus(item.id).then((s) => {
                    if (!cancelled) setRepairs((r) => ({ ...r, [item.id]: s }))
                  }),
                )
              }
              // Checking 与 CleaningUp 没有状态端点：只显示徽章，不拉进度。
              await Promise.all(tasks)
            } catch {
              // 单次轮询失败不值得打断整页，下一拍会重试。
            }
          }),
        )
      } finally {
        inFlight = false
      }
    }

    const t = setInterval(tick, 1000)
    void tick()
    return () => {
      cancelled = true
      clearInterval(t)

      // 这个 1 秒 tick 和上面 5 秒的列表刷新相位互相独立：如果恰好是后者把某配置的 activity
      // 翻成 Idle、配置退出活跃集合，这个 cleanup 会先于下一拍跑到，状态就停在了最后一次
      // 非终态上——按钮已经可点了，这一行却还显示着"Uploading 97%"。给每个真正离场
      // （不再出现在最新 configs 里的活跃配置）的配置补一次收尾请求，而不是简单地取消了事(Fix 3)。
      // 注意这里故意不检查 cancelled：那个标志只用来防止过期的周期性 tick 写状态，
      // 收尾请求是离场处理本身，必须把结果写进去。
      const stillActiveIds = new Set(configsRef.current.filter((c) => c.activity !== 'Idle').map((c) => c.id))
      activeList
        .filter((item) => !stillActiveIds.has(item.id))
        .forEach((item) => {
          if (item.activity === 'BackingUp') {
            void backupConfigsApi
              .runStatus(item.id)
              .then((s) => setRuns((r) => ({ ...r, [item.id]: s })))
              .catch(() => {})
            if (restoresRef.current[item.id]?.status === 'Running') {
              void backupConfigsApi
                .restoreStatus(item.id)
                .then((s) => setRestores((r) => ({ ...r, [item.id]: s })))
                .catch(() => {})
            }
          } else if (item.activity === 'Restoring') {
            void backupConfigsApi
              .restoreStatus(item.id)
              .then((s) => setRestores((r) => ({ ...r, [item.id]: s })))
              .catch(() => {})
          } else if (item.activity === 'Repairing') {
            void backupConfigsApi
              .repairStatus(item.id)
              .then((s) => setRepairs((r) => ({ ...r, [item.id]: s })))
              .catch(() => {})
          }
        })
    }
  }, [activeKey])

  useEffect(() => {
    accountsApi.list().then(setAccounts).catch(() => {})
    settingsApi.get().then(setDefaults).catch(() => {})
  }, [])

  // 编辑模式下账户与容器都锁定，不必列举。
  useEffect(() => {
    if (editing || !showForm || !form.accountId) return
    let cancelled = false
    setContainerList(null)
    setContainerListError(null)
    containersApi
      .list(form.accountId)
      .then((list) => {
        if (!cancelled) setContainerList(list)
      })
      .catch((e) => {
        if (!cancelled) setContainerListError(e instanceof Error ? e.message : String(e))
      })
    return () => {
      cancelled = true
    }
  }, [form.accountId, editing, showForm])

  // 密钥环丢失恢复(设计 §3.5)：顺序依赖是真实的——验证备份密码需要连云，连云需要账户密钥先恢复。
  // 账户仍有待重设项时禁用重设按钮，避免用户在账户没修好前白试一遍备份密码。
  const accountsStillPending = (keyring?.accountsPending ?? 0) > 0

  // 恢复模式下备份/还原/检查/修复一律 409(设计 §3.3)：按钮直接禁用并说明原因，
  // 而不是让用户点了以后看到一坨原始的 409 响应体。
  const keyringLost = keyring?.status === 'Lost'
  const keyringLostHint = keyringLost
    ? 'Data protection keys were lost — re-enter credentials before running this action.'
    : undefined

  const startResetPassword = (c: BackupConfig) => {
    setResettingPassword(c)
    setError(null)
  }

  const closeResetPassword = () => {
    setResettingPassword(null)
    setError(null)
  }

  const submitResetPassword = async (password: string) => {
    if (!resettingPassword) return
    setBusy(true)
    try {
      await backupConfigsApi.resetPassword(resettingPassword.id, password)
      setResettingPassword(null)
      load()
      void refreshKeyringStatus()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const set = <K extends keyof BackupConfigInput>(k: K, v: BackupConfigInput[K]) =>
    setForm((f) => ({ ...f, [k]: v }))

  const startNew = () => {
    setEditing(null)
    // 11 个可继承字段留 null（= 使用默认，PRD §3）。tier 创建后锁定、不可继承，
    // 因此仍以全局默认预填，保存即固定。
    setForm({
      ...emptyForm,
      accountId: accounts[0]?.id ?? 0,
      ...(defaults && {
        indexTier: defaults.defaultIndexTier,
        dataTier: defaults.defaultDataTier,
      }),
    })
    setStep(1)
    setError(null)
    // 此标志位独立于 form，重置表单时不会自动清除；陈旧的 true 会导致容器选择器误开自由文本输入模式
    setNewContainer(false)
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
      ignoreRules: c.ignoreRules,
      dontCompressRules: c.dontCompressRules,
      dontGroupRules: c.dontGroupRules,
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
    setNewContainer(false)
    setShowForm(true)
  }

  const save = async () => {
    setBusy(true)
    setError(null)
    try {
      if (editing) {
        await backupConfigsApi.update(editing.id, form)
      } else {
        // §4.6: 新建成功后不直接关闭，而是提示是否立即运行首次备份。
        const created = await backupConfigsApi.create(form)
        setPostCreate(created)
      }
      setShowForm(false)
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const remove = async (c: BackupConfig, deleteContainer: boolean) => {
    try {
      await backupConfigsApi.remove(c.id, deleteContainer)
      setDeleteModal(null)
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const resetStatus = async (c: BackupConfig) => {
    try {
      await backupConfigsApi.resetStatus(c.id)
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  // 只负责发起——轮询交给上面按 activity 派发的统一机制（服务端是唯一真相源）。
  const run = async (c: BackupConfig) => {
    setError(null)
    try {
      const state = await backupConfigsApi.run(c.id)
      setRuns((r) => ({ ...r, [c.id]: state }))
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  // 同上：只写入首个状态，后续进度由统一轮询接手。
  const pollRestore = (id: number, state: RestoreRun) => {
    setRestores((r) => ({ ...r, [id]: state }))
    load()
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
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const accountName = (id: number) => accounts.find((a) => a.id === id)?.name ?? `#${id}`

  return (
    <section>
      <div className="page-header">
        <h1>Backups</h1>
        <div className="row">
          <button
            type="button"
            onClick={() => setImporting((v) => !v)}
            disabled={accounts.length === 0 || keyringLost}
            title={keyringLostHint}
          >
            Import existing
          </button>
          <button type="button" className="btn-primary" onClick={startNew} disabled={accounts.length === 0}>
            New Backup
          </button>
        </div>
      </div>

      {importing && (
        <div className="panel">
          <strong>Import existing backup</strong> (reads the container's info file)
          <div className="toolbar" style={{ marginTop: '0.5rem' }}>
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
      {accounts.length === 0 && <p className="text-muted">Add an account first.</p>}
      {error && <p className="text-danger">{error}</p>}

      <table>
        <thead>
          <tr>
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
              <td colSpan={6} className="empty-state">
                No backups yet.
              </td>
            </tr>
          ) : (
            configs.map((c) => (
              <tr key={c.id}>
                <td>
                  {c.name}
                  {c.secretsUnavailable && (
                    <span className="row-inline" style={{ marginLeft: '0.5rem' }}>
                      <span className="text-warn">Password required</span>
                      <button
                        type="button"
                        onClick={() => startResetPassword(c)}
                        disabled={accountsStillPending}
                        title={accountsStillPending ? 'Re-enter account credentials first' : undefined}
                      >
                        Re-enter
                      </button>
                    </span>
                  )}
                </td>
                <td>
                  {accountName(c.accountId)} / {c.containerName}
                </td>
                <td className="mono text-faint">{c.localRoot}</td>
                <td>{c.hasPassword ? 'Yes' : 'No'}</td>
                <td>
                  <StatusBadge config={c} onReset={() => resetStatus(c)} />
                </td>
                <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>
                  <button
                    type="button"
                    className="btn-ghost"
                    onClick={() => run(c)}
                    disabled={keyringLost || c.activity !== 'Idle'}
                    title={keyringLostHint}
                  >
                    {c.activity === 'BackingUp' ? 'Backing up…' : 'Backup'}
                  </button>{' '}
                  <button
                    type="button"
                    className="btn-ghost"
                    onClick={() => setRestoreModal(c)}
                    // 还原不因为其它 activity 被禁用：后端刻意允许还原与备份/检查/修复并发执行且
                    // 不占忙碌锁（见 RestoreRunner.cs 顶部注释），只有真的已经有一个还原在跑
                    // （Restoring）时这个按钮才该变灰——否则一整晚的计划备份期间它会灰一整夜。
                    disabled={keyringLost || c.activity === 'Restoring'}
                    title={keyringLostHint}
                  >
                    {c.activity === 'Restoring' ? 'Restoring…' : 'Restore…'}
                  </button>{' '}
                  <button
                    type="button"
                    className="btn-ghost"
                    onClick={() => setCheckModal(c)}
                    disabled={keyringLost}
                    title={keyringLostHint}
                  >
                    Check / Repair…
                  </button>{' '}
                  <button type="button" className="btn-ghost" onClick={() => startEdit(c)}>
                    Edit
                  </button>{' '}
                  <button type="button" className="btn-ghost btn-danger" onClick={() => setDeleteModal(c)}>
                    Delete
                  </button>
                  {runs[c.id] && <RunStatus run={runs[c.id]} />}
                  {restores[c.id] && <RestoreStatus run={restores[c.id]} />}
                  {repairs[c.id] && <RepairStatus run={repairs[c.id]} />}
                </td>
              </tr>
            ))
          )}
        </tbody>
      </table>

      {showForm && (
        <div className="panel">
          <h2>
            {editing ? `Edit: ${editing.name}` : 'New Backup'} — Step {step} of 2
          </h2>

          {step === 1 ? (
            <>
              <Field label={editing ? 'Account (locked)' : 'Account'}>
                <select
                  value={form.accountId}
                  disabled={!!editing}
                  onChange={(e) => {
                    setForm((f) => ({ ...f, accountId: Number(e.target.value), containerName: '' }))
                    setNewContainer(false)
                  }}
                >
                  {accounts.map((a) => (
                    <option key={a.id} value={a.id}>
                      {a.name}
                    </option>
                  ))}
                </select>
              </Field>
              <Field label={editing ? 'Container (locked)' : 'Container'}>
                {editing || containerListError || containerList === null ? (
                  <>
                    <input
                      className="w-md mono"
                      value={form.containerName}
                      disabled={!!editing}
                      onChange={(e) => set('containerName', e.target.value)}
                    />
                    {!editing && containerListError && (
                      <div className="text-warn">
                        Could not list containers ({containerListError}). Type the name instead.
                      </div>
                    )}
                    {!editing && !containerListError && containerList === null && (
                      <div className="text-faint">Loading containers…</div>
                    )}
                  </>
                ) : (
                  <>
                    <select
                      className="w-md"
                      value={newContainer ? ' new' : form.containerName}
                      onChange={(e) => {
                        if (e.target.value === ' new') {
                          setNewContainer(true)
                          set('containerName', '')
                        } else {
                          setNewContainer(false)
                          set('containerName', e.target.value)
                        }
                      }}
                    >
                      <option value="">— select —</option>
                      {containerList.map((c) => (
                        <option key={c.name} value={c.name}>
                          {c.name}
                          {c.backup !== BackupPresence.None ? '  ● has backup' : ''}
                        </option>
                      ))}
                      <option value={' new'}>+ New container…</option>
                    </select>
                    {newContainer && (
                      <>
                        <input
                          className="w-md mono"
                          placeholder="new-container-name"
                          value={form.containerName}
                          onChange={(e) => set('containerName', e.target.value)}
                        />
                        <div
                          className={
                            form.containerName && validateContainerName(form.containerName)
                              ? 'text-danger'
                              : 'text-faint'
                          }
                        >
                          {(form.containerName && validateContainerName(form.containerName)) ||
                            containerNameRule}
                        </div>
                      </>
                    )}
                  </>
                )}
              </Field>
              <Field label={editing ? 'Local Root (locked)' : 'Local Root'}>
                <input
                  className="w-lg mono"
                  placeholder="/data/photos"
                  value={form.localRoot}
                  disabled={!!editing}
                  onChange={(e) => set('localRoot', e.target.value)}
                />
                <button type="button" onClick={() => setBrowsing(true)} disabled={!!editing}>
                  Browse
                </button>
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

              <div className="row" style={{ marginTop: '1rem' }}>
                <button
                  type="button"
                  onClick={() => setStep(2)}
                  disabled={
                    !form.accountId ||
                    !form.containerName.trim() ||
                    !form.localRoot.trim() ||
                    (newContainer && !!validateContainerName(form.containerName))
                  }
                >
                  Next
                </button>
                <button type="button" onClick={() => setShowForm(false)}>
                  Cancel
                </button>
              </div>
            </>
          ) : (
            <>
              <DefaultableField
                label="Ignore rules"
                useDefault={form.ignoreRules === null}
                onToggle={(useDefault) =>
                  set('ignoreRules', useDefault ? null : (editing?.effective.ignoreRules ?? defaults?.defaultIgnoreRules ?? ''))
                }
                effectiveText={(editing?.effective.ignoreRules ?? defaults?.defaultIgnoreRules) || '(none)'}
              >
                <RuleBox value={form.ignoreRules} onChange={(v) => set('ignoreRules', v)} />
              </DefaultableField>
              <DefaultableField
                label="Don't compress"
                useDefault={form.dontCompressRules === null}
                onToggle={(useDefault) =>
                  set(
                    'dontCompressRules',
                    useDefault ? null : (editing?.effective.dontCompressRules ?? defaults?.defaultDontCompressRules ?? ''),
                  )
                }
                effectiveText={(editing?.effective.dontCompressRules ?? defaults?.defaultDontCompressRules) || '(none)'}
              >
                <RuleBox
                  value={form.dontCompressRules}
                  onChange={(v) => set('dontCompressRules', v)}
                />
              </DefaultableField>
              <DefaultableField
                label="Don't group"
                useDefault={form.dontGroupRules === null}
                onToggle={(useDefault) =>
                  set(
                    'dontGroupRules',
                    useDefault ? null : (editing?.effective.dontGroupRules ?? defaults?.defaultDontGroupRules ?? ''),
                  )
                }
                effectiveText={(editing?.effective.dontGroupRules ?? defaults?.defaultDontGroupRules) || '(none)'}
              >
                <RuleBox value={form.dontGroupRules} onChange={(v) => set('dontGroupRules', v)} />
              </DefaultableField>
              <DefaultableField
                label="Include symlinks"
                useDefault={form.includeSymlinks === null}
                onToggle={(useDefault) =>
                  set(
                    'includeSymlinks',
                    useDefault ? null : (editing?.effective.includeSymlinks ?? defaults?.defaultIncludeSymlinks ?? false),
                  )
                }
                effectiveText={String(editing?.effective.includeSymlinks ?? defaults?.defaultIncludeSymlinks ?? false)}
              >
                <input
                  type="checkbox"
                  checked={form.includeSymlinks ?? false}
                  onChange={(e) => set('includeSymlinks', e.target.checked)}
                />
              </DefaultableField>
              <DefaultableField
                label="Verbose (debug) logging"
                useDefault={form.verboseLogging === null}
                onToggle={(useDefault) =>
                  set(
                    'verboseLogging',
                    useDefault ? null : (editing?.effective.verboseLogging ?? defaults?.defaultVerboseLogging ?? false),
                  )
                }
                effectiveText={String(editing?.effective.verboseLogging ?? defaults?.defaultVerboseLogging ?? false)}
              >
                <input
                  type="checkbox"
                  checked={form.verboseLogging ?? false}
                  onChange={(e) => set('verboseLogging', e.target.checked)}
                />
              </DefaultableField>
              <DefaultableField
                label="Max versions"
                useDefault={form.maxVersions === null}
                onToggle={(useDefault) =>
                  set('maxVersions', useDefault ? null : (editing?.effective.maxVersions ?? defaults?.defaultMaxVersions ?? 100))
                }
                effectiveText={String(editing?.effective.maxVersions ?? defaults?.defaultMaxVersions ?? 100)}
              >
                <input
                  className="w-sm"
                  type="number"
                  value={form.maxVersions ?? 0}
                  onChange={(e) => set('maxVersions', Number(e.target.value))}
                />
              </DefaultableField>
              <DefaultableField
                label="Max age (days)"
                useDefault={form.maxAgeDays === null}
                onToggle={(useDefault) =>
                  set('maxAgeDays', useDefault ? null : (editing?.effective.maxAgeDays ?? defaults?.defaultMaxAgeDays ?? 180))
                }
                effectiveText={String(editing?.effective.maxAgeDays ?? defaults?.defaultMaxAgeDays ?? 180)}
              >
                <input
                  className="w-sm"
                  type="number"
                  value={form.maxAgeDays ?? 0}
                  onChange={(e) => set('maxAgeDays', Number(e.target.value))}
                />
              </DefaultableField>
              <DefaultableField
                label="Retention mode"
                useDefault={form.retentionMode === null}
                onToggle={(useDefault) =>
                  set(
                    'retentionMode',
                    useDefault ? null : (editing?.effective.retentionMode ?? defaults?.defaultRetentionMode ?? RetentionMode.EitherTriggers),
                  )
                }
                effectiveText={
                  retentionModeLabels[
                    editing?.effective.retentionMode ?? defaults?.defaultRetentionMode ?? RetentionMode.EitherTriggers
                  ]
                }
              >
                <select
                  value={form.retentionMode ?? RetentionMode.EitherTriggers}
                  onChange={(e) => set('retentionMode', Number(e.target.value))}
                >
                  {Object.entries(retentionModeLabels).map(([v, label]) => (
                    <option key={v} value={v}>
                      {label}
                    </option>
                  ))}
                </select>
              </DefaultableField>
              <DefaultableField
                label="Single-file threshold (MB)"
                useDefault={form.singleFileThresholdBytes === null}
                onToggle={(useDefault) =>
                  set(
                    'singleFileThresholdBytes',
                    useDefault
                      ? null
                      : (editing?.effective.singleFileThresholdBytes ?? defaults?.defaultSingleFileThresholdBytes ?? 5 * MB),
                  )
                }
                effectiveText={`${Math.round(
                  (editing?.effective.singleFileThresholdBytes ?? defaults?.defaultSingleFileThresholdBytes ?? 5 * MB) / MB,
                )} MB`}
              >
                <input
                  className="w-sm"
                  type="number"
                  value={Math.round((form.singleFileThresholdBytes ?? 0) / MB)}
                  onChange={(e) => set('singleFileThresholdBytes', Number(e.target.value) * MB)}
                />
              </DefaultableField>
              <DefaultableField
                label="Group cap (MB)"
                useDefault={form.groupCapBytes === null}
                onToggle={(useDefault) =>
                  set(
                    'groupCapBytes',
                    useDefault ? null : (editing?.effective.groupCapBytes ?? defaults?.defaultGroupCapBytes ?? 100 * MB),
                  )
                }
                effectiveText={`${Math.round(
                  (editing?.effective.groupCapBytes ?? defaults?.defaultGroupCapBytes ?? 100 * MB) / MB,
                )} MB`}
              >
                <input
                  className="w-sm"
                  type="number"
                  value={Math.round((form.groupCapBytes ?? 0) / MB)}
                  onChange={(e) => set('groupCapBytes', Number(e.target.value) * MB)}
                />
              </DefaultableField>
              <DefaultableField
                label="Volume size (MB, 0 = off)"
                useDefault={form.volumeBytes === null}
                onToggle={(useDefault) =>
                  set('volumeBytes', useDefault ? null : (editing?.effective.volumeBytes ?? defaults?.defaultVolumeBytes ?? 0))
                }
                effectiveText={(() => {
                  const bytes = editing?.effective.volumeBytes ?? defaults?.defaultVolumeBytes ?? 0
                  // 0 = 关闭分卷，是这轮工作特意引入的表示法；这里应该显示 "off" 而不是 "0 MB"。
                  return bytes > 0 ? `${Math.round(bytes / MB)} MB` : 'off'
                })()}
              >
                <input
                  className="w-sm"
                  type="number"
                  value={Math.round((form.volumeBytes ?? 0) / MB)}
                  onChange={(e) => set('volumeBytes', Number(e.target.value) * MB)}
                />
              </DefaultableField>

              <div className="row" style={{ marginTop: '1rem' }}>
                <button type="button" onClick={() => setStep(1)}>
                  Back
                </button>
                <button type="button" className="btn-primary" onClick={save} disabled={busy || !form.name.trim()}>
                  {editing ? 'Save' : 'Create'}
                </button>
                <button type="button" onClick={() => setShowForm(false)} disabled={busy}>
                  Cancel
                </button>
              </div>
            </>
          )}
        </div>
      )}

      {browsing && (
        <PathBrowser
          initialPath={form.localRoot || undefined}
          onPick={(p) => {
            set('localRoot', p)
            setBrowsing(false)
          }}
          onClose={() => setBrowsing(false)}
        />
      )}
      {checkModal && (
        <CheckModal
          config={checkModal}
          onClose={() => setCheckModal(null)}
          onError={(e) => setError(e)}
        />
      )}
      {restoreModal && (
        <RestoreDialog
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
      {deleteModal && (
        <DeleteModal
          config={deleteModal}
          onClose={() => setDeleteModal(null)}
          onConfirm={(deleteContainer) => remove(deleteModal, deleteContainer)}
        />
      )}
      {postCreate && (
        <PostCreateModal
          config={postCreate}
          onRunNow={() => {
            setPostCreate(null)
            void run(postCreate)
          }}
          onNotNow={() => setPostCreate(null)}
        />
      )}
      {resettingPassword && (
        <ResetPasswordModal
          config={resettingPassword}
          busy={busy}
          error={error}
          onSubmit={submitResetPassword}
          onClose={closeResetPassword}
        />
      )}
    </section>
  )
}

function RunStatus({ run }: { run: BackupRun }) {
  if (run.status === 'Failed')
    return <div className="text-danger">Failed: {run.error}</div>
  if (run.status === 'Completed')
    return (
      <div className="text-ok">
        Completed — version {run.version}
        {/* 一次"成功"的备份可能跳过了文件——不写在这里，操作员只能靠可能被淹没的通知发现。 */}
        {!!run.unreadableFiles && (
          <span className="text-danger">
            {' '}— {run.unreadableFiles} file(s) could not be read; earlier content kept
          </span>
        )}
      </div>
    )
  const p = run.progress
  return (
    <div className="text-faint">
      {p ? `${backupStageLabels[p.stage]} ${p.percent}% (${p.changedFiles} changed)` : 'Starting…'}
    </div>
  )
}

// 状态徽标（§4.2 决策 2）：进行中（蓝，派生 activity）优先于持久 Error（红，tooltip + Reset）；否则不显示。
function StatusBadge({ config, onReset }: { config: BackupConfig; onReset: () => void }) {
  if (config.activity !== 'Idle') {
    return <span className="badge badge-info">{config.activity}</span>
  }
  if (config.status === BackupStatus.Error) {
    return (
      <span className="row-inline">
        <span title={config.lastError ?? 'Unknown error'} className="badge badge-danger">
          Error
        </span>
        <button type="button" className="btn-ghost" onClick={onReset}>
          Reset
        </button>
      </span>
    )
  }
  return <span className="text-faint">—</span>
}

// 与 RunStatus 同形的三态展示；RepairRun 没有 version/progress 字段（见 api/backupConfigs.ts），故没有对应显示。
function RepairStatus({ run }: { run: RepairRun }) {
  if (run.status === 'Failed')
    return <div className="text-danger">Repair failed: {run.error}</div>
  if (run.status === 'Completed')
    return <div className="text-ok">Repair completed</div>
  return <div className="text-faint">Repairing…</div>
}

function RestoreStatus({ run }: { run: RestoreRun }) {
  if (run.status === 'Failed')
    return <div className="text-danger">Restore failed: {run.error}</div>
  if (run.status === 'Completed')
    return (
      <div className={run.failedFiles ? 'text-warn' : 'text-ok'}>
        Restored {run.restoredFiles} file(s), skipped {run.skippedFiles}
        {run.failedFiles ? `, failed ${run.failedFiles}` : ''} — version {run.version}
      </div>
    )
  return <div className="text-faint">{run.phase || 'Restoring…'}</div>
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
      rows={2}
      placeholder="gitignore syntax, one per line"
      className="w-lg"
      value={value ?? ''}
      onChange={(e) => onChange(e.target.value)}
    />
  )
}

// 删除确认（§4.3）：默认只删本地配置/缓存/日志，云端 container 保留。勾选 deleteContainer 时二次
// window.confirm 强调不可逆，避免误删整个 container。
function DeleteModal({
  config, onClose, onConfirm,
}: { config: BackupConfig; onClose: () => void; onConfirm: (deleteContainer: boolean) => void }) {
  const [deleteContainer, setDeleteContainer] = useState(false)

  const confirm = () => {
    if (deleteContainer) {
      const sure = window.confirm(
        `This will PERMANENTLY delete the Azure container "${config.containerName}" and ALL backup data in it. ` +
          'This cannot be undone. Are you absolutely sure?',
      )
      if (!sure) return
    }
    onConfirm(deleteContainer)
  }

  return (
    <div className={overlayStyle} onClick={onClose}>
      <div className={panelStyle} onClick={(e) => e.stopPropagation()}>
        <h3 style={{ marginTop: 0 }}>Delete Backup — {config.name}</h3>
        <p>This removes the local backup configuration, cached index, and logs.</p>
        <label style={{ display: 'flex', gap: '0.5rem', alignItems: 'flex-start', margin: '0.8rem 0' }}>
          <input
            type="checkbox"
            checked={deleteContainer}
            onChange={(e) => setDeleteContainer(e.target.checked)}
          />
          <span className={deleteContainer ? 'text-danger' : undefined}>
            Also delete cloud container (irreversible — erases all backup data)
          </span>
        </label>
        <div className="row" style={{ marginTop: '1rem' }}>
          <button type="button" className="btn-danger" onClick={confirm}>
            Delete
          </button>
          <button type="button" onClick={onClose}>
            Cancel
          </button>
        </div>
      </div>
    </div>
  )
}

// §4.6：新建配置成功后，提示是否立即运行首次备份。"Run now" 复用表格行同款 run+poll 逻辑
// （进度显示在该配置所在行，无独立进度页）。
function PostCreateModal({
  config, onRunNow, onNotNow,
}: { config: BackupConfig; onRunNow: () => void; onNotNow: () => void }) {
  return (
    <div className={overlayStyle} onClick={onNotNow}>
      <div className={panelStyle} onClick={(e) => e.stopPropagation()}>
        <h3 style={{ marginTop: 0 }}>Backup Created — {config.name}</h3>
        <p>Run the first backup now?</p>
        <div className="row" style={{ marginTop: '1rem' }}>
          <button type="button" className="btn-primary" onClick={onRunNow}>
            Run first backup now
          </button>
          <button type="button" onClick={onNotNow}>
            Not now
          </button>
        </div>
      </div>
    </div>
  )
}

// 密钥环丢失恢复弹窗：重新录入原始备份密码。密码本身不提供更改功能——只能核对，核对通过
// (解密云端 info 文件成功)才落库；错误以 400 携带 "Verification failed: ..." 返回，原样显示。
function ResetPasswordModal({
  config, busy, error, onSubmit, onClose,
}: {
  config: BackupConfig
  busy: boolean
  error: string | null
  onSubmit: (password: string) => void
  onClose: () => void
}) {
  const [password, setPassword] = useState('')

  return (
    <div className={overlayStyle} onClick={onClose}>
      <div className={panelStyle} onClick={(e) => e.stopPropagation()}>
        <h3 style={{ marginTop: 0 }}>Re-enter Password — {config.name}</h3>
        <p>
          Enter the original password used to encrypt this backup. It cannot be changed — a
          different password will fail verification.
        </p>

        <Field label="Password">
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </Field>

        {error && <p className="text-danger">{error}</p>}

        <div className="row" style={{ marginTop: '1rem' }}>
          <button type="button" className="btn-primary" onClick={() => onSubmit(password)} disabled={busy || !password}>
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
      onError(e instanceof Error ? e.message : String(e))
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
      onError(e instanceof Error ? e.message : String(e))
    } finally {
      setRepairing(false)
    }
  }

  const problems = report ? report.findings.filter((f) => f.cloud === CloudState.MissingOrBad) : []
  // 内容沿用自更早版本的条目：云端 blob 本身通常没问题，所以不在 problems 里，但同样要报出来。
  const stale = report ? report.findings.filter((f) => f.unreadableAt) : []

  return (
    <div className={overlayStyle} onClick={onClose}>
      <div className={panelStyle} onClick={(e) => e.stopPropagation()}>
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
          <label>
            <input type="checkbox" checked={listOrphans} onChange={(e) => setListOrphans(e.target.checked)} />
            {' '}Detect unreferenced blobs (repair deletes them)
          </label>
        </Field>

        <div className="row" style={{ margin: '0.8rem 0' }}>
          <button type="button" className="btn-primary" onClick={runCheck} disabled={running || repairing}>
            {running ? 'Checking…' : 'Run check'}
          </button>
          {(problems.some((f) => f.repairable) || (report?.orphanBlobs?.length ?? 0) > 0) && (
            <button type="button" onClick={runRepair} disabled={repairing || running}>
              {repairing ? 'Repairing…' : 'Repair from local'}
            </button>
          )}
          <button type="button" onClick={onClose}>Close</button>
        </div>

        {repairReport && (
          <div style={{ marginBottom: '0.6rem' }}>
            {repairReport.status === 'Running' && 'Repairing (backup is locked until done)…'}
            {repairReport.status === 'Failed' && <span className="text-danger">Repair failed: {repairReport.error}</span>}
            {repairReport.status === 'Completed' && (
              <>
                Repaired {repairReport.repaired?.length ?? 0} file(s);{' '}
                <span className={repairReport.unrecoverable?.length ? 'text-danger' : undefined}>
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
              <div className="text-danger">Metadata drift: {report.metadataIssue}</div>
            )}
            <div className={report.ok ? 'text-ok' : 'text-danger'} style={{ margin: '0.4rem 0' }}>
              {report.ok ? 'All checked objects OK' : `${problems.length} problem(s), ${report.repairablePaths.length} repairable from local`}
              {' '}(version {report.version})
            </div>
            {listOrphans && (
              <div className={report.orphanBlobs.length ? 'text-warn' : 'text-ok'} style={{ margin: '0.4rem 0' }}>
                {report.orphanBlobs.length === 0
                  ? 'No unreferenced blobs found'
                  : `${report.orphanBlobs.length} unreferenced blob(s) — repair will delete: ${report.orphanBlobs.slice(0, 20).join(', ')}${report.orphanBlobs.length > 20 ? '…' : ''}`}
              </div>
            )}
            {/* 沿用条目的云端 blob 通常是好的（cloud=Ok），所以它们不会出现在下面的问题表里——
                但操作员必须知道这个版本里有内容是旧的，尤其因为它会让本地比对显示为 Changed。 */}
            {stale.length > 0 && (
              <div className="text-warn" style={{ margin: '0.4rem 0' }}>
                {stale.length} file(s) hold content carried forward from an earlier backup — the
                source could not be read since then, so a local comparison shows them as changed:
                <ul style={{ margin: '0.2rem 0 0 1.2rem' }}>
                  {stale.slice(0, 20).map((f) => (
                    <li key={f.path}>
                      <span className="mono">{f.path}</span>
                      {' '}— unread since {new Date(f.unreadableAt!).toLocaleString()}
                    </li>
                  ))}
                </ul>
                {stale.length > 20 && <div>…and {stale.length - 20} more</div>}
              </div>
            )}
            {problems.length > 0 && (
              <table className="text-faint">
                <thead><tr><th>File</th><th>Cloud</th><th>Local</th><th>Repairable</th></tr></thead>
                <tbody>
                  {problems.map((f) => (
                    <tr key={f.path}>
                      <td className="mono">
                        {f.path}
                        {f.unreadableAt && <span className="text-warn"> (carried forward)</span>}
                      </td>
                      <td className="text-danger" style={{ textAlign: 'center' }}>{cloudStateLabel(f.cloud)}</td>
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
