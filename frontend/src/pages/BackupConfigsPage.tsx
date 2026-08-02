import { Fragment, useEffect, useRef, useState } from 'react'
import { accountsApi, type Account } from '../api/accounts'
import { refreshKeyringStatus, useKeyringStatus } from '../api/keyring'
import { settingsApi, type GlobalSettings } from '../api/settings'
import { DefaultableField } from '../components/DefaultableField'
import { PathBrowser } from '../components/PathBrowser'
import { RestoreDialog } from '../components/RestoreDialog'
import { formatBytes, formatVersionSpan } from '../constants/format'
import { Field } from '../components/Field'
import { Modal } from '../components/Modal'
import {
  activityBadgeLabels,
  activityLabels,
  backupConfigsApi,
  StorageTier,
  RetentionMode,
  CloudCheckLevel,
  LocalCheckLevel,
  CloudState,
  LocalState,
  BackupStatus,
  BackupStage,
  tierLabels,
  retentionModeLabels,
  backupStageLabels,
  type BackupActivity,
  type BackupConfig,
  type BackupConfigInput,
  type BackupRun,
  type StageProgress,
  type RestoreRun,
  type CheckRun,
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
  crossDirGroupRules: null,
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

// 从「配置 id → 运行状态」的表里摘掉一条。没有这条时原样返回，免得白白重渲一次。
function without<T>(map: Record<number, T>, id: number): Record<number, T> {
  if (!(id in map)) return map
  const next = { ...map }
  delete next[id]
  return next
}

export function BackupConfigsPage() {
  const [configs, setConfigs] = useState<BackupConfig[]>([])
  const [accounts, setAccounts] = useState<Account[]>([])
  const [runs, setRuns] = useState<Record<number, BackupRun>>({})
  const [restores, setRestores] = useState<Record<number, RestoreRun>>({})
  const [repairs, setRepairs] = useState<Record<number, RepairRun>>({})
  const [checks, setChecks] = useState<Record<number, CheckRun>>({})
  const [checkModal, setCheckModal] = useState<BackupConfig | null>(null)
  const [restoreModal, setRestoreModal] = useState<BackupConfig | null>(null)
  const [deleteModal, setDeleteModal] = useState<BackupConfig | null>(null)
  const [errorModal, setErrorModal] = useState<BackupConfig | null>(null)
  const [showForm, setShowForm] = useState(false)
  const [browsing, setBrowsing] = useState(false)
  const [editing, setEditing] = useState<BackupConfig | null>(null)
  const [step, setStep] = useState<1 | 2>(1)
  const [form, setForm] = useState<BackupConfigInput>(emptyForm)
  // 独立于 form：它只用来比对，绝不能跟着 form 一起被 POST 出去。
  const [passwordConfirm, setPasswordConfirm] = useState('')
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
    const refresh = () => backupConfigsApi.list().then(setConfigs).catch(() => {})
    const t = setInterval(refresh, 5000)
    // 浏览器把后台标签页的定时器节流到分钟级，切回来那一瞬看到的还是上一拍的旧快照，
    // 要再等一个周期才更新——长任务跑着时，那正是"看起来卡住了"的一半原因。
    const onVisible = () => {
      if (document.visibilityState === 'visible') void refresh()
    }
    document.addEventListener('visibilitychange', onVisible)
    return () => {
      clearInterval(t)
      document.removeEventListener('visibilitychange', onVisible)
    }
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
              } else if (item.activity === 'Checking') {
                tasks.push(
                  backupConfigsApi.checkStatus(item.id).then((s) => {
                    if (!cancelled && s) setChecks((r) => ({ ...r, [item.id]: s }))
                  }),
                )
              }
              // CleaningUp 没有状态端点：只显示徽章，不拉进度。
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
    // 同上：切回前台立刻补一拍，别让用户盯着一分钟前的快照。
    const onVisible = () => {
      if (document.visibilityState === 'visible') void tick()
    }
    document.addEventListener('visibilitychange', onVisible)
    return () => {
      cancelled = true
      clearInterval(t)
      document.removeEventListener('visibilitychange', onVisible)

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
          } else if (item.activity === 'Checking') {
            void backupConfigsApi
              .checkStatus(item.id)
              .then((s) => { if (s) setChecks((r) => ({ ...r, [item.id]: s })) })
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
    // 12 个可继承字段留 null（= 使用默认，PRD §3）。tier 创建后锁定、不可继承，
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
    setPasswordConfirm('')
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
      crossDirGroupRules: c.crossDirGroupRules,
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
    setPasswordConfirm('')
    setError(null)
    setNewContainer(false)
    setShowForm(true)
  }

  // 编辑时密码字段是锁死的，不参与比对。空密码（不加密）要求确认框同样为空，
  // 这样「本想设密码却只填了一个框」也会被拦下。
  const passwordMismatch = !editing && (form.password ?? '') !== passwordConfirm

  const save = async () => {
    if (passwordMismatch) return
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

  // 失败**不**在这里吞掉：错误要显示在删除弹窗内部（见 DeleteModal）。写进页面那条全局错误的话，
  // 弹窗正盖在它上面，用户看到的就是"点了 Delete，什么都没发生"——后端拒绝删除正在运行的配置时
  // （409）每次都是这个现象。
  const remove = async (c: BackupConfig, deleteContainer: boolean) => {
    await backupConfigsApi.remove(c.id, deleteContainer)
    setDeleteModal(null)
    // 删除是从这份配置的编辑表单里发起的：留着表单开着，用户面对的是一份已经不存在的配置，
    // 再点一次 Save 只会撞上 404。
    if (editing?.id === c.id) {
      setShowForm(false)
      setEditing(null)
    }
    load()
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
      // 上一次检查/修复的结论说的是「云端此刻的内容」，这次备份一上传就作废了：那行绿字
      // 留在原地只会让人以为它还成立。以前得切到别的页面再回来（组件重挂载）才清得掉。
      setChecks((m) => without(m, c.id))
      setRepairs((m) => without(m, c.id))
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  // 停止一个正在跑的操作。在此之前，一次跑了几小时的备份唯一的停法是重启容器——而用户跑在
  // NAS 上，那会连带停掉别的服务；「正忙时不许删配置」又把删除这条退路堵上了。
  // 逐操作停而不是一键停光：备份与还原可以并发，误停另一条同样是几小时的损失。
  const stopOp = async (c: BackupConfig, what: 'backup' | 'restore' | 'repair' | 'check', label: string) => {
    if (!window.confirm(`Stop the running ${label} for "${c.name}"? Work done so far is kept, but the operation will not finish.`))
      return
    setError(null)
    try {
      await backupConfigsApi.cancel(c.id, what)
      // 取消是异步的：信号发出后要等到下一个取消检查点才真的收尾，所以这里不动状态，
      // 让统一轮询把真实的终态（Canceled）拉回来。
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

  // editing 是点 Edit 那一刻的快照，而表单可能开着好几分钟——activity 得从每 5 秒刷新的
  // configs 里取当下的值，否则删除按钮的禁用状态停在打开表单时的那一拍。
  const editingLive = editing ? (configs.find((c) => c.id === editing.id) ?? editing) : null

  // 两个步骤的操作栏共用一个：删除跟当前停在第几步无关，不该逼用户先 Back 回第 1 步。
  // 后端本就拒绝删除正在运行的配置（409，见 BackupConfigEndpoints 的 DeriveActivity）；
  // 这里跟着灰只是把按钮与那条既有护栏对齐——真正的保证仍在后端，因为 activity 每 5 秒才刷
  // 一次，刚开跑的那几秒里按钮还是亮的（那时错误会显示在弹窗里，见 DeleteModal）。
  const deleteButton = editingLive && (
    <button
      type="button"
      className="btn-danger"
      // 推到操作栏另一端：不可逆的操作不该紧挨着 Save/Cancel 让人误点。
      style={{ marginLeft: 'auto' }}
      onClick={() => setDeleteModal(editingLive)}
      disabled={busy || editingLive.activity !== 'Idle'}
      title={
        editingLive.activity === 'Idle'
          ? undefined
          : `Currently ${activityLabels[editingLive.activity]} — stop it or wait for it to finish before deleting.`
      }
    >
      Delete…
    </button>
  )

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
      {/* 表单开着的时候这条挪到表单里去（见下）：表单在表格下方，而这里是表格上方，
          被服务端拒掉的保存会把原因显示在离按钮几屏远的地方，看着就是"点了没反应"。 */}
      {!showForm && error && <p className="text-danger">{error}</p>}

      <table className="cards">
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
            configs.map((c) => {
              // 运行状态挪出操作列、单独占一行（见 index.css .ops-row）：操作列是 nowrap 的，
              // 而正在处理的那个路径动辄几百字符，放在那里会把整张表撑到出屏。
              const ops = [
                runs[c.id] && <RunStatus key="run" run={runs[c.id]} onStop={() => stopOp(c, 'backup', 'backup')} />,
                restores[c.id] && <RestoreStatus key="restore" run={restores[c.id]} onStop={() => stopOp(c, 'restore', 'restore')} />,
                repairs[c.id] && <RepairStatus key="repair" run={repairs[c.id]} onStop={() => stopOp(c, 'repair', 'repair')} />,
                checks[c.id] && <CheckStatus key="check" run={checks[c.id]} onStop={() => stopOp(c, 'check', 'check')} />,
              ].filter(Boolean)
              return (
              <Fragment key={c.id}>
              <tr className={ops.length > 0 ? 'has-ops' : undefined}>
                <td className="card-title">
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
                <td data-label="Account / Container">
                  {accountName(c.accountId)} / {c.containerName}
                </td>
                <td className="mono text-faint" data-label="Local Root">{c.localRoot}</td>
                <td data-label="Encrypted">{c.hasPassword ? 'Yes' : 'No'}</td>
                <td data-label="Status">
                  <StatusBadge
                    config={c}
                    onReset={() => resetStatus(c)}
                    onShowError={() => setErrorModal(c)}
                  />
                </td>
                <td className="card-actions" style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>
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
                  {/* Delete 不在这一行：它藏在 Edit 里（见下方表单的 form-actions）。
                      一行五个按钮时，不可逆的那个正好紧挨着最常用的 Backup/Restore，窄屏换行后
                      位置还会漂；走一趟编辑表单既拉开了距离，也保证删之前先看清删的是哪一份。 */}
                  <button type="button" className="btn-ghost" onClick={() => startEdit(c)}>
                    Edit
                  </button>
                </td>
              </tr>
              {ops.length > 0 && (
                <tr className="ops-row">
                  <td colSpan={6}>{ops}</td>
                </tr>
              )}
              </Fragment>
              )
            })
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
              <Field label={editing ? 'Container (locked)' : 'Container'} multi>
                {editing || containerListError || containerList === null ? (
                  <>
                    <input
                      className="w-lg mono"
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
                      className="w-lg"
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
                        // 已经被一条备份占着的 container 不给选：选了也会被服务端 409 挡下，
                        // 而两条备份写同一个 container 会互相覆盖版本历史，各自的数据在对方的
                        // 保留清理里被当成孤儿删掉。占用来自本地配置，备份跑到一半时同样成立
                        // ——云端那个信息文件要等最后一步才写出来。
                        <option key={c.name} value={c.name} disabled={!!c.inUseBy}>
                          {c.name}
                          {c.inUseBy
                            ? `  ● in use by "${c.inUseBy}"`
                            : c.backup !== BackupPresence.None
                              ? '  ● has backup'
                              : ''}
                        </option>
                      ))}
                      <option value={' new'}>+ New container…</option>
                    </select>
                    {newContainer && (
                      <>
                        <input
                          className="w-lg mono"
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
                <input className="w-lg" value={form.name} onChange={(e) => set('name', e.target.value)} />
              </Field>
              <Field label="Description">
                <input
                  className="w-lg"
                  value={form.description ?? ''}
                  onChange={(e) => set('description', e.target.value)}
                />
              </Field>
              <Field label={editing ? 'Password (locked)' : 'Password'}>
                <input
                  type="password"
                  className="w-lg"
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
              {/* 只有新建才要二次输入。导入与密钥环恢复时输入的密码会被**验证**（错了当场失败），
                  唯独这里是「设定」——没有任何东西能判断它对不对，而它此后不可更改、丢失无解。
                  一个看不见的字符打错，要等到真正需要还原的那天才会发现。 */}
              {!editing && (
                <Field label="Confirm password">
                  <input
                    type="password"
                    className="w-lg"
                    placeholder={form.password ? 'Re-enter the same password' : 'Leave empty for no encryption'}
                    value={passwordConfirm}
                    onChange={(e) => setPasswordConfirm(e.target.value)}
                  />
                </Field>
              )}
              {passwordMismatch && (
                <div className="text-danger" style={{ marginBottom: '0.4rem' }}>
                  Passwords do not match.
                </div>
              )}
              {!editing && !!form.password && !passwordMismatch && (
                <div className="text-warn" style={{ marginBottom: '0.4rem' }}>
                  This password cannot be changed or recovered after the backup is created. If it is
                  lost, nothing encrypted with it can be restored — the only way out is to delete the
                  configuration and start over.
                </div>
              )}
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

              <div className="row form-actions" style={{ marginTop: '1rem' }}>
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
                {deleteButton}
              </div>
            </>
          ) : (
            <>
              {/* 路径基准必须写在这里。规则匹配的是**相对于 Local Root** 的路径，不含 Local Root
                  本身；不说的话很自然会照着自己看到的完整路径去写，写出来的规则一条都不命中，
                  而且不命中是静默的——用户只能从"包数没变少"这种间接现象去猜。 */}
              <p className="text-muted" style={{ margin: '0 0 var(--sp-2)' }}>
                All rule lists below use gitignore syntax and match paths <strong>relative to the local
                root</strong>{form.localRoot.trim() && <> (<span className="mono">{form.localRoot}</span>)</>} —
                write <span className="mono">/Backup/</span> to mean{' '}
                <span className="mono">{(form.localRoot.trim() || '<local root>').replace(/\/+$/, '')}/Backup</span>, not the
                full path. A trailing <span className="mono">/</span> means "this directory and everything under it".
              </p>
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
                label="Pack across directories"
                useDefault={form.crossDirGroupRules === null}
                onToggle={(useDefault) =>
                  set(
                    'crossDirGroupRules',
                    useDefault ? null : (editing?.effective.crossDirGroupRules ?? defaults?.defaultCrossDirGroupRules ?? ''),
                  )
                }
                effectiveText={(editing?.effective.crossDirGroupRules ?? defaults?.defaultCrossDirGroupRules) || '(none)'}
              >
                <RuleBox value={form.crossDirGroupRules} onChange={(v) => set('crossDirGroupRules', v)} />
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

              {error && <p className="text-danger">{error}</p>}
              <div className="row form-actions" style={{ marginTop: '1rem' }}>
                <button type="button" onClick={() => setStep(1)}>
                  Back
                </button>
                <button type="button" className="btn-primary" onClick={save} disabled={busy || !form.name.trim() || passwordMismatch}>
                  {editing ? 'Save' : 'Create'}
                </button>
                <button type="button" onClick={() => setShowForm(false)} disabled={busy}>
                  Cancel
                </button>
                {deleteButton}
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
          onError={setError}
        />
      )}
      {restoreModal && (
        <RestoreDialog
          config={restoreModal}
          onClose={() => setRestoreModal(null)}
          onError={setError}
          onStarted={(state) => {
            const id = restoreModal.id
            setRestoreModal(null)
            void pollRestore(id, state)
          }}
        />
      )}
      {errorModal && <ErrorModal config={errorModal} onClose={() => setErrorModal(null)} />}
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

// 停止按钮：只在运行中出现。停止是异步的（信号发出后要等到下一个取消检查点），所以点完
// 这一行不会立刻变——文案里不作"已停止"的承诺。
function StopButton({ onStop }: { onStop: () => void }) {
  return (
    <>
      {' '}
      <button type="button" className="btn-ghost btn-danger" style={{ padding: '0 0.3rem' }} onClick={onStop}>
        Stop
      </button>
    </>
  )
}

function RunStatus({ run, onStop }: { run: BackupRun; onStop: () => void }) {
  // 展开状态留在组件内：轮询每秒都在换 props，但 React 保留同一个实例，所以展开不会被刷掉。
  const [showDetail, setShowDetail] = useState(false)

  if (run.status === 'Failed')
    return <div className="text-danger">Failed: {run.error}</div>
  // 停止既不是成功也不是失败：后端不会把它写成该备份的 Error 状态，这里也不用红色。
  if (run.status === 'Canceled')
    return <div className="text-warn">Backup stopped — nothing was recorded for this run</div>
  if (run.status === 'Completed')
    return (
      <div className="text-ok">
        Completed — version {run.version}
        {/* 起止时刻取自版本记录，与还原对话框读的是同一组数字。老后端不发这两个字段 → 只显示编号。 */}
        {run.completedAt && ` (${formatVersionSpan(run.startedAt, run.completedAt)})`}
        {/* 一次"成功"的备份可能跳过了文件——不写在这里，操作员只能靠可能被淹没的通知发现。 */}
        {!!run.unreadableFiles && (
          <span className="text-danger">
            {' '}— {run.unreadableFiles} file(s) could not be read; earlier content kept
          </span>
        )}
      </div>
    )
  const p = run.progress
  if (!p) return <div className="text-faint">Starting…<StopButton onStop={onStop} /></div>

  // 流水线化之后 Diffing 与 Uploading 会同时在跑，明细因此可能有两条。老后端只发 detail，
  // 所以两边都要认。
  const details = p.details?.length ? p.details : p.detail ? [p.detail] : []
  const diffing = details.find((d) => d.stage === 'Diffing')
  const uploading = details.find((d) => d.stage === 'Uploading')

  // 顶行的进度。单条阶段用它自己的百分比：run.percent 衡量的是上传项完成比例，在扫描/差分阶段
  // 恒为 0——照搬会出现顶上写 0%、下面细节写 3% 的自相矛盾。
  //
  // 两条并行时**不能只给一个数**。百分比只有 Diffing 拿得出（它的分母是扫描到的条目数，一开始
  // 就定了）；上传的分母还随 diff 边判边入队在涨，给不出可靠的百分比。原先顶行直接摆 Diffing
  // 那个数，理由写的是"它才是决定整轮还要多久的那条"——而这恰恰在最要紧的时候反过来：diff 判得
  // 比压缩上传快几个数量级，被有界队列挡住（waiting for upload to catch up）之后它就停在原地，
  // 而上传其实一直在推进，顶行却看着像卡死了。
  //
  // 所以标明那个百分比是 diff 的，再并上上传**已完成的绝对量**——没有可靠分母时，绝对量比一个
  // 会回落的假百分比诚实。
  // 完成度按**源端字节**算，不按件数。上传阶段件数百分比几乎没有意义：一件活可能是一个 6.8 GB
  // 的单文件，也可能是一箱几百个 5 KB 的小文件，按件数等于把它们当成一样重——实测件数报 75% 时
  // 源字节才 31%，顶行一路虚高，最后几件大的再把它按在 99% 上很久。"还剩多少件"由明细行那个
  // 分数（2,003 / 2,661 objects）直说，比折成百分比清楚。
  // 拿不到时（扫描/差分不申报字节工作量，上传阶段 diff 判完前分母还在长）才退回件数。
  const singlePercent =
    (details[0]?.workPercent ?? details[0]?.percent) ??
    (p.stage >= BackupStage.Uploading ? p.percent : null)
  const headline =
    diffing && uploading
      ? [
          diffing.percent != null && `${diffing.percent}% diffed`,
          uploading.workDone > 0 && `${formatBytes(uploading.workDone)} uploaded`,
        ]
          .filter(Boolean)
          .join(' · ')
      : singlePercent != null
        ? `${singlePercent}%`
        : ''
  // 变更数要等 diff 跑完才算得出来，在那之前写 "(0 changed)" 是在陈述一个还不成立的事实。
  const changed = p.stage >= BackupStage.Uploading ? ` (${p.changedFiles} changed)` : ''

  // 流水线化之后 diff 与上传是**同时**在跑的，而后端的 stage 要等 diff 收工才切到 Uploading。
  // 照搬它，顶行就会在整轮里绝大部分时间写着 "Diffing"——首次备份的 diff 要把每个文件读完算
  // hash，可以跑几小时，期间上传其实一直在传，界面上却看不出来。两条明细同时在动时照实说。
  const label = details.length > 1 ? 'Diffing + Uploading' : backupStageLabels[p.stage]

  return (
    <div className="text-faint">
      {label}
      {headline && ` ${headline}`}
      {changed}
      <StopButton onStop={onStop} />
      {/* 细节收进展开区：正在处理的路径可以很长，摊在列表行里会把表格挤变形。
          默认只留一行总进度，需要看的时候再展开。 */}
      {details.length > 0 && (
        <>
          {' '}
          <button
            type="button"
            className="btn-ghost"
            style={{ padding: '0 0.3rem' }}
            onClick={() => setShowDetail((v) => !v)}
          >
            {showDetail ? '▾ Details' : '▸ Details'}
          </button>
          {/* 两条并行的明细上下叠放，不横着摊开——用户提过 details 会把表格撑宽，
              多一条更要小心。 */}
          {showDetail && details.map((d) => <StageDetail key={d.stage} detail={d} />)}
        </>
      )}
    </div>
  )
}

/// 阶段细节。在此之前，扫描和 diff 各自只在进入时上报一次——首次备份的 diff 要把每个文件
/// 完整读一遍算 hash，可以跑几小时，界面上却只有一个一动不动的 0%，分不清是在干活还是挂死。
// 每个阶段数的是**不同的东西**，光一个 "4,995 / 46,624" 会让人以为打包没生效：
// 差分数的是文件（打包与否都是这个数），上传/还原数的才是包与单文件 blob。
const STAGE_UNITS: Record<string, string> = {
  Scanning: 'entries',
  Diffing: 'files',
  Uploading: 'objects',
  Restoring: 'objects',
  // 检查的各阶段。Cloud 数的是存储对象（一个 pack 一次 HEAD），Verifying 数的是下载解压
  // 重算 hash 的包，Local 数的才是索引里的文件条目——三者数量差着数量级，不能共用一个词。
  Cloud: 'objects',
  Verifying: 'objects',
  Local: 'files',
  Orphans: 'blobs',
}

function StageDetail({ detail }: { detail: StageProgress }) {
  const unit = STAGE_UNITS[detail.stage] ?? 'items'
  // 用 "of" 而不是 "/"：斜杠是分数记号，摆在那儿就是在邀人约一个百分比出来，而件数百分比在
  // 上传阶段恰恰没什么意义（一件可能是 6.8 GB 的单文件，也可能是一箱几百个 5 KB 的小文件）。
  // 真正的完成度按字节算，在下一行那个 "60.6 GB / 191.0 GB original (31%)" 上——那里用斜杠，
  // 因为它的百分比就跟在后面，约得出来也约得对。
  const counts =
    detail.total > 0
      ? `${detail.processed.toLocaleString()} of ${detail.total.toLocaleString()} ${unit}`
      : `${detail.processed.toLocaleString()} ${unit} so far` // 扫描时总数未知——它正是扫描要算出来的
  // 在途分解。光一个"处理了 N 件"看不出是在干活还是卡住了：备份的上传阶段里，一件活要先过
  // 7z（一箱 100 MB 可以压几十秒）才轮到推字节，那段时间 uploading 是 0 而 preparing 不是；
  // 还原/校验阶段同理，下载一结束就退出在途窗口，随后的解压/算 hash 那段本地 CPU 工作同样要
  // 占着 preparing 才不会从界面上消失。三个数各自有值才出现——扫描、差分这些阶段没有队列，
  // 全是 0，这一段自然整体消失。
  //
  // preparing 现在是**阶段相关**的，不只是数值范围不同、连数的是什么都不同：上传阶段数的是
  // "拿到全局压缩锁、正在产出卷文件"的活，压缩锁只有一把，永远是 0 或 1，排在它后面等锁的
  // 活算进 queued；还原/校验阶段数的是"下载完、正在解压/算 hash"的组，那里没有全局锁，
  // 最多可以有 DownloadConcurrency 个组同时在解，不是 0/1。标签跟着这层含义走。
  //
  // 一个字节都没在传、手上却有活在准备，这种时候**不能**只让 "N uploading" 悄悄消失：
  // 那一刻速度是 0、进度条不动、在途路径一行都没有，界面上看跟卡死一模一样。把原因写出来。
  // 措辞不提"压缩"：选了不压缩的备份一样要过一遍 7z（加密、打包、分卷），说 compressing 是错的；
  // 还原/校验阶段则确实是在解压/算 hash，直说就行。
  const preparingLabel = detail.stage === 'Uploading' ? 'preparing' : 'extracting'
  // 压完了、字节却还没上网线的件卡在哪一段。三段的处置完全不同，所以分开说：
  // · peer  —— 同一份内容正由别人上传，只能等它整件传完（几分钟起步）
  // · slot  —— 全局上传闸门排满了（这一项数的是**卷**，闸门按卷排队）
  // · cloud —— 在等云端应答（存在性/元数据 HEAD），网络一慢就是几十秒
  // 不分开的话，屏幕上就只剩"什么都没在传"这一句，而这正是查不下去的地方。
  //
  // 件数口径：peer 与 cloud 数的是**件**，slot 数的是**卷**。所以只有前两项参与件数加法，
  // slot 单独说，措辞里点明单位。
  //
  // 剩下的那些（uploading 减掉 peer 与 cloud）是已进上传段、但字节还没上路的件——正在做压完到
  // 开传之间那段本地活（pack 逐成员重新 Stat、单文件查去重映射）。只在**一条流都没在传**时才报：
  // 有流在传时上面已经有一句 "N uploading" 了，再加一档只会让人分不清哪个数是哪个。
  const stalled = Math.max(0, detail.uploading - detail.waitingOnPeer - detail.waitingOnCloud)
  const idleOnStaging = detail.activeItems.length === 0 && detail.preparing > 0
  const inFlightVerb = detail.stage === 'Uploading' ? 'uploading' : 'downloading'
  // 在途那个数的单位**两侧不一样**，所以只有上传侧要点明：
  // · 上传：VolumeBlobIO 每一卷各登记一条，一件大活自己就能占满全部并发额度（默认 5）；
  // · 下载：RestoreOrchestrator / BackupChecker 按整个对象（或整组）登记一条，多卷共用它。
  // 不点明的后果是实打实的：同一行里 processed 与 queued 数的是**件**，把卷数加进去就超过
  // 总数——实测 5,346 + 5 + 1,031 = 6,382 > 6,378，多出的 4 正是「5 卷 − 1 件」。
  // 下载侧本来就与件数同口径，加"objects"反而是多余的噪音，维持原样。
  const inFlightUnit =
    detail.stage === 'Uploading' ? (detail.activeItems.length === 1 ? 'volume ' : 'volumes ') : ''
  // "5 volumes uploading" / "1 volume uploading" / "3 downloading"
  const inFlightPhrase = `${detail.activeItems.length} ${inFlightUnit}${inFlightVerb}`
  // 排列按**逆时间轴**：越接近"字节已经上了网线"的排越前，越早的阶段排越后，末尾落到 queued。
  // 一件活的正序是 queued → preparing（占锁压）→ starting upload（压完到开传之间的本地活）→
  // waiting on peer/cloud/slot（等资源）→ uploading（在传），这一行倒着念就是它。
  // preparing 从前排在 waiting 与 starting upload 的**前面**，读起来像"先压、再准备、再等"，
  // 而它其实比那两段都早——屏幕上最常见的组合恰好是 waiting 三档全 0，于是错位就直接暴露成
  // "preparing · starting upload"这种倒着的相邻两项。
  // buffered to disk 与 nothing on the wire 不在这条时间轴上：前者是 diff 领先量，后者是一句
  // 总述（解释为什么没有 "N volumes uploading"），都留在队首。
  const inFlight = [
    // 差分判得比压缩上传快几个数量级，跑到前面去是常态；多出来的活攒在磁盘上等下游消化。
    // 这一行取代了从前那句 "waiting for upload to catch up"——写侧不再阻塞了，所以要说的
    // 不再是"卡住了"，而是"领先了多少"。措辞里点明 buffered，别让人以为这是失败重试。
    detail.spilledItems > 0 && `${detail.spilledItems.toLocaleString()} buffered to disk`,
    detail.activeItems.length > 0 && inFlightPhrase,
    // "right now" 而不是 "yet"：这一条说的是**这一瞬**没有流在传（手上那件正占着压缩锁），
    // 不是"还没开始过"。跑到一半时下面那行已经有几个 GB 的累计量了，说 "yet" 是错的——
    // 用户正是拿这两句对着问的。
    idleOnStaging && 'nothing on the wire right now',
    // 压完了、字节却还没上路的件卡在哪一段。这几档存在的理由就是那个"几分钟纹丝不动"：
    // 从前这些件不属于任何一栏，屏幕上 processed + preparing + queued 比总数少，而少掉的
    // 恰恰是卡住的那件，只能靠把几屏截图排在一起做减法才发现得了。
    detail.waitingOnPeer > 0 && `${detail.waitingOnPeer} waiting on the same content elsewhere`,
    detail.waitingOnCloud > 0 && `${detail.waitingOnCloud} waiting on the cloud`,
    // 单位是**卷**不是件（闸门按卷排队），所以措辞里必须点明，否则又是一个加不平的数。
    detail.waitingOnSlot > 0 && `${detail.waitingOnSlot} volumes waiting for an upload slot`,
    detail.activeItems.length === 0 && stalled > 0 && `${stalled} starting upload`,
    detail.preparing > 0 && `${detail.preparing} ${preparingLabel}`,
    detail.queued > 0 && `${detail.queued.toLocaleString()} queued`,
  ]
    .filter(Boolean)
    .join(' · ')
  // 门开在"有没有在途流"上，不开在数值上：卡住的流会被心跳一路摁到 0（见 StageTracker.Tick），
  // 这正是要显示出来的信号——真卡住时应该看到 "0 B/s"，而不是这一行整段消失、让人分不清
  // 是没在传还是卡死了。Uploading/Restoring/Verifying 三个会登记在途项的阶段都是边传边报字节
  // （下载同样挂了逐卷 progress，见 VolumeBlobIO.DownloadAsync），此前只有 Uploading 是这样，
  // 现在三者对称，不必再单独把 Uploading 摘出来。其余七个阶段从不调 BeginItem，
  // activeItems 恒为空，条件退化回原来的 bytesPerSecond > 0，行为不变。
  const speed =
    detail.bytesPerSecond > 0 || detail.activeItems.length > 0
      ? ` · ${formatBytes(detail.bytesPerSecond)}/s`
      : ''
  // .NET 把 TimeSpan 序列化成 "hh:mm:ss.fffffff"；截到秒即可。
  const eta = detail.estimatedRemaining ? ` · ~${detail.estimatedRemaining.split('.')[0]} left` : ''

  // 字节明细另起一行：件数那行已经够长了，再塞进去在窄屏上会折得没法读。
  // 各段刻意互不重叠，加起来才是全貌；为零的整段省略，所以扫描/差分这些阶段这一行自然消失。
  //
  // 上传与下载是两个方向，措辞不能共用：上传是「压缩 → 送走」，下载是「拉下来 → 写出去」，
  // 而且下载侧的总量事先就知道（索引里记着各卷尺寸），上传侧压完才知道，只能报已完成的。
  const downloading = detail.stage === 'Restoring' || detail.stage === 'Verifying'
  const bytesLine = (
    downloading
      ? [
          // 已下载 / 总下载：分母来自索引；老索引缺卷尺寸时后端报 0，这里就只显示分子。
          detail.transferredBytes > 0 &&
            (detail.transferTotal > 0
              ? `${formatBytes(detail.transferredBytes)} / ${formatBytes(detail.transferTotal)} downloaded`
              : `${formatBytes(detail.transferredBytes)} downloaded`),
          // 已恢复 / 待恢复：解压后写出去的源字节。
          detail.workDone > 0 && `${formatBytes(detail.workDone)} restored`,
          detail.workRemaining > 0 && `${formatBytes(detail.workRemaining)} to go`,
        ]
      : [
          // 已完成 / 总量，两边都是**源端**字节（压缩前）。分数只有同口径才有意义——拿实传
          // 字节当分子是不行的：分母（压缩后的总量）在开始传之前根本不存在，压完才知道，
          // 而且压缩率随文件类型大幅摆动，跨口径的比例读不出任何东西。
          //
          // 措辞用 original / compressed 这一对，跟压缩工具里 Original Size / Compressed Size
          // 的惯例一致。原先叫 "uploaded" 是错的：它让人以为这是传上去的量，而这两个数说的是
          // **原始文件有多大**——压不动的内容两个数几乎相等，那个词就把口径彻底藏起来了。
          //
          // 完成度百分比就跟在这个分数后面——它算的正是这两个数，摆在一起谁都不会认错。
          // 从件数那行挪过来的：搁在 "2,003 of 2,661 objects" 旁边，读起来是那个分数的完成度，
          // 而件数 75% 时字节可能才 31%。
          detail.workTotal > 0 &&
            `${formatBytes(detail.workDone)} / ${formatBytes(detail.workTotal)} original${
              detail.workPercent != null ? ` (${detail.workPercent}%)` : ''
            }`,
          // 这一轮真正传出去的字节，后面跟上它占原始尺寸的比例。
          //
          // 措辞换了四轮，每一轮都指出同一件事——这个数的**口径**看不出来：
          // · 叫 "stored" 被读成"云上一共存了多少"（它和整条明细一样只说本次运行）；
          // · 与前面那个分数"有什么不同"同样看不出来，两个数都以 GB 结尾，口径却一个是原始尺寸
          //   一个是实传，而压不动的内容（媒体、已压缩文件）两者几乎相等，光摆着像重复了一遍；
          // · 叫 "on the wire" 更糟——上面那句 "nothing on the wire right now" 正用着同一个词，
          //   一处说没有、一处说 2 GB；
          // · 叫 "compressed" 会自相矛盾：不压缩(store-only)又加了密时，7z 封装加 AES 让产物
          //   **比原文件大**，小文件的归档头开销同样如此，于是出现 "compressed (105%)"。
          //
          // 落回 "uploaded"——分数改叫 original 之后这个词就空出来了，而它本来就是最准的：
          // 传出去多少就是多少，不预设变大还是变小。超过 100% 照样读得通（上传了原始尺寸的
          // 105%），而且那正是要告诉用户的事：这样配下来云上反而更占地方。
          detail.transferredBytes > 0 &&
            `${formatBytes(detail.transferredBytes)} uploaded${
              detail.workDone > 0
                // 括号里必须带上"of original"。光一个 (95%) 会被读成"上传进度 95%"——
                // 而上一行**就有**一个真正的进度百分比，两个挨着，混读几乎是必然的。
                // 重复一次 original 换来零歧义，值得。
                ? ` (${Math.round((100 * detail.transferredBytes) / detail.workDone)}% of original)`
                : ''
            }`,
          // 已经落在云上、但所属那件活还没完成的字节。左边那个 uploaded 按**件**记（才对得上
          // 同样按件销账的原始字节），所以一件大活分卷上传的几十分钟里，传完的卷进不了那个数——
          // 而它们确实已经在云上了，也早已不在 "ready to upload" 里（池子逐卷释放）。没有这一项，
          // 这批字节就在界面上凭空消失，看着像什么都没发生。整件完成时并入左边，这里归零消失。
          //
          // 措辞：`+` 表示它是左边那个数之外的追加量；复用 "uploaded" 是因为口径确实相同
          // （都是已落云的压缩后字节），换个词反而像是另一种东西；单位跟着上一行的 "objects"
          // 走，不另起 "items"——同一屏里两个词指同一件事，读起来就得先猜它们是不是一回事。
          detail.unfinishedItemBytes > 0 &&
            `+${formatBytes(detail.unfinishedItemBytes)} uploaded in unfinished objects`,
          // "ready to upload" 而不是 "staged"：后者是内部术语（暂存区），对着屏幕的人没理由知道，
          // 而且同一行里还有个 "N preparing"——那个是**正在压**的件数，这个是**已经压完**、
          // 躺在临时盘上等上传的字节，两者被混问过。
          // 这个数还顺带说明压缩与网络谁跑在前面：它涨＝压缩快过上传，落＝上传追上来了。
          detail.stagedBytes > 0 && `${formatBytes(detail.stagedBytes)} ready to upload`,
        ]
  )
    .filter(Boolean)
    .join(' · ')

  return (
    <div style={{ marginTop: '0.15rem', lineHeight: 1.5 }}>
      {detail.currentItem && (
        <div className="mono" style={{ wordBreak: 'break-all' }}>
          {detail.currentItem}
        </div>
      )}
      {/* 在途的每一条各占一行，带上尺寸与进度。从前这里挤的是内容寻址的 blob 名
          （加密时还是 HMAC），既看不出在传哪个文件，也看不出传了多少。 */}
      {/* 标题里点明"并发的传输流"：同时列出 2-3 个文件名会让人以为在并发压缩，
          而压缩是全局串行的（一把锁，就是上面那个 N preparing），并发的是上传/下载。 */}
      {detail.activeItems.length > 0 && (
        <div className="text-faint">
          {`${detail.activeItems.length} parallel ${inFlightUnit}${inFlightVerb}:`}
        </div>
      )}
      {/* 全部列出，不再截到 3 条。在途条数有上界——它就是设置里的上传/下载并发数（默认 5），
          闸门按**卷**发额度，所以不会因为队列长或文件大而涨。把其中几条折成 "+2 more" 反而
          藏掉了最有用的东西：卡住的那条通常就在被折起来的那几条里。 */}
      {detail.activeItems.map((a) => (
        <div key={a.label} className="mono" style={{ wordBreak: 'break-all' }}>
          {a.label}
          {a.total > 0 && (
            <span className="text-faint">
              {' '}
              — {formatBytes(a.sent)} / {formatBytes(a.total)}
              {a.percent !== null && ` · ${a.percent}%`}
            </span>
          )}
        </div>
      ))}
      <div>
        {/* 阶段名要写出来：两条明细并排时，光看两行数字分不清哪行是差分哪行是上传。 */}
        <span className="text-faint">{detail.stage}: </span>
        {counts}
        {/* 按字节的完成度**不放这里**——它紧跟在 "2,003 of 2,661 objects" 后面，会被读成那个数
            的完成度，而两者差得很远（实测件数 75% 时字节才 31%）。它挪去下一行，紧跟自己的那个
            分数（"60.6 GB / 191.0 GB original (31%)"），谁的百分比一眼可见，也就不必再加口径标注。
            这里只留件数百分比，且只在按字节算不出来时（扫描/差分不申报字节工作量）才出现——
            那时它和旁边的分数本来就是同一个口径。 */}
        {detail.workPercent == null && detail.percent != null && ` · ${detail.percent}%`}
        {inFlight && ` · ${inFlight}`}
        {speed}
        {eta}
      </div>
      {bytesLine && <div className="text-faint">{bytesLine}</div>}
    </div>
  )
}

// 状态徽标（§4.2 决策 2）：进行中（蓝，派生 activity）优先于持久 Error（红，tooltip + Reset）；否则不显示。
function StatusBadge({
  config, onReset, onShowError,
}: { config: BackupConfig; onReset: () => void; onShowError: () => void }) {
  if (config.activity !== 'Idle') {
    return <span className="badge badge-info">{activityBadgeLabels[config.activity]}</span>
  }
  if (config.status === BackupStatus.Error) {
    return (
      <span className="row-inline">
        {/* 徽章可点开看正文。从前错误只塞在 title（tooltip）里：那一大坨 Azure 异常在 tooltip
            里根本读不了，而且没人想到去悬停——刷新界面之后就"只能在日志里找错误"了。
            正文一直是持久化的（BackupConfig.LastError），缺的只是给它一个能读的地方。 */}
        <button type="button" className="badge badge-danger" onClick={onShowError}>
          Error
        </button>
        <button type="button" className="btn-ghost" onClick={onReset}>
          Reset
        </button>
      </span>
    )
  }
  return <span className="text-faint">—</span>
}

/// 备份最近一次失败的完整正文。Azure 的异常又长又带 XML，必须给足空间、可滚动、可复制。
function ErrorModal({ config, onClose }: { config: BackupConfig; onClose: () => void }) {
  const [copied, setCopied] = useState(false)
  const text = config.lastError ?? 'No error detail was recorded.'

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text)
      setCopied(true)
    } catch {
      // 剪贴板被浏览器策略挡住（非 https、无权限）——正文就在下面，用户还能自己选中复制。
    }
  }

  return (
    <Modal
      title={`Last error — ${config.name}`}
      onClose={onClose}
      footer={
        <>
          <button type="button" onClick={copy}>
            {copied ? 'Copied' : 'Copy'}
          </button>
          <button type="button" onClick={onClose}>
            Close
          </button>
        </>
      }
    >
      {config.lastErrorAt && (
        <p className="text-faint" style={{ marginTop: 0 }}>
          {new Date(config.lastErrorAt).toLocaleString()}
        </p>
      )}
      <pre
        className="mono"
        style={{
          maxHeight: '50vh', overflow: 'auto', whiteSpace: 'pre-wrap', wordBreak: 'break-all',
          background: 'var(--bg-raised)', border: '1px solid var(--border)',
          borderRadius: 'var(--r-md)', padding: 'var(--sp-3)', margin: 0,
        }}
      >
        {text}
      </pre>
    </Modal>
  )
}

// 与 RunStatus 同形的三态展示；RepairRun 没有 version/progress 字段（见 api/backupConfigs.ts），故没有对应显示。
function RepairStatus({ run, onStop }: { run: RepairRun; onStop: () => void }) {
  if (run.status === 'Failed')
    return <div className="text-danger">Repair failed: {run.error}</div>
  if (run.status === 'Canceled')
    return <div className="text-warn">Repair stopped — files already repaired are kept</div>
  if (run.status === 'Completed')
    return <div className="text-ok">Repair completed</div>
  return <div className="text-faint">Repairing…<StopButton onStop={onStop} /></div>
}

// 检查的运行态。报告本身在 Check/Repair 对话框里看——这一行只回答「还在跑吗、跑到哪了」，
// 因为一次内容级检查要把整个备份下载重算 hash，可以跑上几小时。
function CheckStatus({ run, onStop }: { run: CheckRun; onStop: () => void }) {
  const [showDetail, setShowDetail] = useState(false)

  if (run.status === 'Failed')
    return <div className="text-danger">Check failed: {run.error}</div>
  if (run.status === 'Canceled')
    return <div className="text-warn">Check stopped — no report was produced</div>
  if (run.status === 'Completed') {
    const r = run.report
    if (!r) return <div className="text-ok">Check completed</div>
    return (
      <div className={r.ok ? 'text-ok' : 'text-danger'}>
        {r.ok
          ? `Check completed — all checked objects OK (version ${r.version})`
          : `Check completed — ${r.missingRefs.length} problem(s), ${r.repairablePaths.length} repairable (version ${r.version})`}
      </div>
    )
  }

  return (
    <div className="text-faint">
      Checking
      {run.detail?.percent != null && ` ${run.detail.percent}%`}
      <StopButton onStop={onStop} />
      {run.detail && (
        <>
          {' '}
          <button
            type="button"
            className="btn-ghost"
            style={{ padding: '0 0.3rem' }}
            onClick={() => setShowDetail((v) => !v)}
          >
            {showDetail ? '▾ Details' : '▸ Details'}
          </button>
          {showDetail && <StageDetail detail={run.detail} />}
        </>
      )}
    </div>
  )
}

function RestoreStatus({ run, onStop }: { run: RestoreRun; onStop: () => void }) {
  const [showDetail, setShowDetail] = useState(false)
  // 跳过/失败的逐条记录。完成之后才是最该看它的时候——一个数字说不出是哪些文件、为什么。
  const events = run.events ?? []
  const toggle = events.length > 0 || run.detail ? (
    <>
      {' '}
      <button
        type="button"
        className="btn-ghost"
        style={{ padding: '0 0.3rem' }}
        onClick={() => setShowDetail((v) => !v)}
      >
        {showDetail ? '▾ Details' : '▸ Details'}
      </button>
    </>
  ) : null

  const detailBlock = showDetail && (
    <>
      {run.detail && <StageDetail detail={run.detail} />}
      {events.length > 0 && (
        <ul className="mono" style={{ margin: '0.2rem 0 0 1.2rem', wordBreak: 'break-all' }}>
          {events.map((e, i) => (
            <li key={i}>{e}</li>
          ))}
        </ul>
      )}
    </>
  )

  if (run.status === 'Failed')
    return (
      <div className="text-danger">
        Restore failed: {run.error}
        {toggle}
        {detailBlock}
      </div>
    )
  if (run.status === 'Completed')
    return (
      <div className={run.failedFiles ? 'text-warn' : 'text-ok'}>
        Restored {run.restoredFiles} file(s), skipped {run.skippedFiles}
        {run.failedFiles ? `, failed ${run.failedFiles}` : ''} — version {run.version}
        {toggle}
        {detailBlock}
      </div>
    )
  // 还原是逐文件写出的，没有"回滚"这回事：已经落盘的那些文件停止后仍然留在目标目录里。
  if (run.status === 'Canceled')
    return (
      <div className="text-warn">
        Restore stopped — files already written are kept
        {toggle}
        {detailBlock}
      </div>
    )
  return (
    <div className="text-faint">
      {run.phase || 'Restoring…'}
      <StopButton onStop={onStop} />
      {toggle}
      {detailBlock}
    </div>
  )
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
}: {
  config: BackupConfig
  onClose: () => void
  /** 抛出即视为失败，错误显示在本弹窗内。成功由调用方关闭弹窗。 */
  onConfirm: (deleteContainer: boolean) => Promise<void>
}) {
  const [deleteContainer, setDeleteContainer] = useState(false)
  // 失败原因必须显示在**弹窗内部**。从前是写到页面上那条全局错误里，而弹窗正盖在它上面——
  // 后端拒绝删除正在运行的配置时（409），用户看到的就是"点了 Delete，什么都没发生"。
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const confirm = async () => {
    if (deleteContainer) {
      const sure = window.confirm(
        `This will PERMANENTLY delete the Azure container "${config.containerName}" and ALL backup data in it. ` +
          'This cannot be undone. Are you absolutely sure?',
      )
      if (!sure) return
    }
    setError(null)
    setBusy(true)
    try {
      await onConfirm(deleteContainer)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Modal
      title={`Delete Backup — ${config.name}`}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn-danger" onClick={confirm} disabled={busy}>
            {busy ? 'Deleting…' : 'Delete'}
          </button>
          <button type="button" onClick={onClose}>
            Cancel
          </button>
        </>
      }
    >
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
      {error && (
        <div className="text-danger" style={{ margin: '0.8rem 0' }}>
          {error}
        </div>
      )}
    </Modal>
  )
}

// §4.6：新建配置成功后，提示是否立即运行首次备份。"Run now" 复用表格行同款 run+poll 逻辑
// （进度显示在该配置所在行，无独立进度页）。
function PostCreateModal({
  config, onRunNow, onNotNow,
}: { config: BackupConfig; onRunNow: () => void; onNotNow: () => void }) {
  return (
    <Modal
      title={`Backup Created — ${config.name}`}
      onClose={onNotNow}
      footer={
        <>
          <button type="button" className="btn-primary" onClick={onRunNow}>
            Run first backup now
          </button>
          <button type="button" onClick={onNotNow}>
            Not now
          </button>
        </>
      }
    >
      <p>Run the first backup now?</p>
    </Modal>
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
    <Modal
      title={`Re-enter Password — ${config.name}`}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn-primary" onClick={() => onSubmit(password)} disabled={busy || !password}>
            Submit
          </button>
          <button type="button" onClick={onClose} disabled={busy}>
            Cancel
          </button>
        </>
      }
    >
      <p>
        Enter the original password used to encrypt this backup. It cannot be changed — a
        different password will fail verification.
      </p>

      <Field label="Password">
        <input
          type="password"
          className="w-lg"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
        />
      </Field>

      {error && <p className="text-danger">{error}</p>}
    </Modal>
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
  const [checkRun, setCheckRun] = useState<CheckRun | null>(null)
  const [repairing, setRepairing] = useState(false)
  const [repairReport, setRepairReport] = useState<RepairRun | null>(null)
  // 轮询要在对话框关掉时停下，否则它会一直往一个已卸载的组件里写状态。
  const aliveRef = useRef(true)
  useEffect(() => () => { aliveRef.current = false }, [])

  const report = checkRun?.report ?? null

  // 检查现在是后台 job：POST 只拿到 202，结果与进度都靠轮询。
  const follow = async (initial: CheckRun) => {
    setRunning(true)
    try {
      let run = initial
      setCheckRun(run)
      while (run.status === 'Running') {
        await delay(1000)
        if (!aliveRef.current) return null
        // 正在跑的检查一定有状态可报；真拿到空就停下轮询，别把 run 打成空值。
        const next = await backupConfigsApi.checkStatus(config.id)
        if (!next) break
        run = next
        setCheckRun(run)
      }
      if (run.status === 'Failed' && run.error) onError(run.error)
      return run
    } finally {
      if (aliveRef.current) setRunning(false)
    }
  }

  // 走 ref 而不是把 follow 放进下面 effect 的依赖：follow 每次渲染都是新函数，
  // 直接依赖会让这个「打开时读一次」的 effect 每渲染重跑一遍。
  const followRef = useRef(follow)
  followRef.current = follow

  useEffect(() => {
    backupConfigsApi.versions(config.id).then((vs) => setVersions(vs.map((v) => v.version))).catch(() => {})
    // 服务端保留着最近一次检查的报告：关掉对话框再打开要能看回结果，而一次内容级检查
    // 要把整个备份下载重算一遍 hash，重跑的代价是实打实的出站流量。空 = 从没查过。
    // 仍在跑就接着轮询；已跑完则只把报告摆出来——不走 follow，免得把上一次的失败
    // 当成这次的错误再弹一遍横幅。
    backupConfigsApi
      .checkStatus(config.id)
      .then((s) => { if (!s) return; if (s.status === 'Running') void followRef.current(s); else setCheckRun(s) })
      .catch(() => {})
  }, [config.id])

  const rehydrateArg = () => (cloud === CloudCheckLevel.Content ? rehydrate : null)

  // 检查是后台 job，进度在列表那一行（Checking N% + Details）已经有了，这里再显示一份没有意义：
  // 启动成功就直接关掉对话框，报告下次打开时从服务端读回来。
  const runCheck = async () => {
    setRepairReport(null)
    try {
      await backupConfigsApi.check(config.id, cloud, local, version, rehydrateArg(), listOrphans)
      onClose()
    } catch (e) {
      onError(e instanceof Error ? e.message : String(e))
    }
  }

  const stopCheck = async () => {
    try {
      await backupConfigsApi.cancel(config.id, 'check')
    } catch (e) {
      onError(e instanceof Error ? e.message : String(e))
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
        await follow(await backupConfigsApi.check(config.id, cloud, local, version, rehydrateArg(), listOrphans))
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
    <Modal
      title={`Check / Repair — ${config.name}`}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn-primary" onClick={runCheck} disabled={running || repairing}>
            {running ? 'Checking…' : 'Run check'}
          </button>
          {running && (
            <button type="button" className="btn-danger" onClick={stopCheck}>Stop</button>
          )}
          {(problems.some((f) => f.repairable) || (report?.orphanBlobs?.length ?? 0) > 0) && (
            <button type="button" onClick={runRepair} disabled={repairing || running}>
              {repairing ? 'Repairing…' : 'Repair from local'}
            </button>
          )}
          <button type="button" onClick={onClose}>Close</button>
        </>
      }
    >
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
        {/* 说明文字与勾选框同在外层 <label class="field"> 里——不再自己套一层 <label>：
            标签不能嵌套，而且嵌套那层会让勾选框逃出 .field 的居中规则，看上去偏高。 */}
        <span className="field-check">
          <input type="checkbox" checked={listOrphans} onChange={(e) => setListOrphans(e.target.checked)} />
          Detect unreferenced blobs (repair deletes them)
        </span>
      </Field>

      {/* 进度不在这里重复一遍：检查在服务端后台跑，列表里那一行已经有阶段、百分比和 Details。
          对话框只说明它正在跑（报告要等它结束才有），并留下 Stop。 */}
      {running && (
        <div className="text-faint" style={{ marginBottom: '0.6rem' }}>
          A check is running — progress is shown in the backup list. The report appears here when it finishes.
        </div>
      )}
      {checkRun?.status === 'Canceled' && (
        <div className="text-warn" style={{ marginBottom: '0.6rem' }}>Check stopped — no report was produced.</div>
      )}
      {checkRun?.status === 'Failed' && (
        <div className="text-danger" style={{ marginBottom: '0.6rem' }}>Check failed: {checkRun.error}</div>
      )}

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
            <div className="table-scroll" tabIndex={0}>
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
            </div>
          )}
        </div>
      )}
    </Modal>
  )
}
