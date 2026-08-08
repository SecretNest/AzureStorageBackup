import { api } from './client'
import type { LocalRootPreview } from '../lib/localRootVerdict'

// 与后端 enum 对应（System.Text.Json 默认序列化为数字）
export const StorageTier = { Hot: 0, Cool: 1, Cold: 2, Archive: 3 } as const
export const RetentionMode = {
  VersionOnly: 0,
  TimeOnly: 1,
  EitherTriggers: 2,
  BothRequired: 3,
} as const

export const tierLabels: Record<number, string> = {
  0: 'Hot',
  1: 'Cool',
  2: 'Cold',
  3: 'Archive',
}

export const retentionModeLabels: Record<number, string> = {
  0: 'By version count only',
  1: 'By age only',
  2: 'Either triggers',
  3: 'Both required',
}

// 备份管线阶段
export const BackupStage = {
  Scanning: 0,
  Diffing: 1,
  Uploading: 2,
  WritingIndex: 3,
  Finalizing: 4,
  CleaningUp: 5,
  Completed: 6,
} as const

export const backupStageLabels: Record<number, string> = {
  [BackupStage.Scanning]: 'Scanning',
  [BackupStage.Diffing]: 'Diffing',
  [BackupStage.Uploading]: 'Uploading',
  [BackupStage.WritingIndex]: 'Writing index',
  [BackupStage.Finalizing]: 'Finalizing',
  [BackupStage.CleaningUp]: 'Cleaning up',
  [BackupStage.Completed]: 'Completed',
}

// 持久状态（§4.2 决策 2）：仅 Normal/Error。瞬时态见 BackupActivity（派生，不落库）。
export const BackupStatus = { Normal: 0, Error: 1 } as const

// 还原冲突模式（§4.1c 决策 3，与后端 enum 数值对应）
export const RestoreConflictMode = { OverwriteIfChanged: 0, Skip: 1, RenameKeep: 2 } as const
export const restoreConflictModeLabels: Record<number, string> = {
  0: 'Overwrite if changed',
  1: 'Skip existing',
  2: 'Keep existing (rename)',
}

// Archive 活化优先级（与后端 enum 数值对应）
export const RestoreRehydratePriority = { Standard: 0, High: 1 } as const
export type BackupActivity = 'Idle' | 'BackingUp' | 'Restoring' | 'Checking' | 'Repairing' | 'CleaningUp'

/** 读得通的形式，用在句子里（"Currently backing up — …"）。后端 Humanize 的对应物。 */
export const activityLabels: Record<BackupActivity, string> = {
  Idle: 'idle',
  BackingUp: 'backing up',
  Restoring: 'restoring',
  Checking: 'checking',
  Repairing: 'repairing',
  CleaningUp: 'cleaning up',
}

/**
 * 徽标里单独站着的形式。不能复用 activityLabels：那一组是为句子中间准备的小写形式，
 * 摆进徽标就成了半截句子；也不能直接打 activity 本身——那是后端的 enum 名，
 * "BackingUp" / "CleaningUp" 这种驼峰直接糊到屏幕上是漏出来的实现细节，不是文案。
 */
export const activityBadgeLabels: Record<BackupActivity, string> = {
  Idle: 'Idle',
  BackingUp: 'Backing Up',
  Restoring: 'Restoring',
  Checking: 'Checking',
  Repairing: 'Repairing',
  CleaningUp: 'Cleaning Up',
}

/** 后端解析后的生效值（null 字段已用全局设置填充）。只读，仅供显示。 */
export interface EffectiveBackupSettings {
  ignoreRules: string | null
  dontCompressRules: string | null
  dontGroupRules: string | null
  // 命中者允许跨目录装箱。空 = 全部按目录打包（历史行为）。
  crossDirGroupRules: string | null
  includeSymlinks: boolean
  maxVersions: number
  maxAgeDays: number
  retentionMode: number
  singleFileThresholdBytes: number
  groupCapBytes: number
  volumeBytes: number | null
  verboseLogging: boolean
}

export interface BackupConfig {
  id: number
  accountId: number
  containerName: string
  name: string
  description: string | null
  localRoot: string
  hasPassword: boolean
  indexTier: number
  dataTier: number
  ignoreRules: string | null
  dontCompressRules: string | null
  dontGroupRules: string | null
  // 命中者允许跨目录装箱。空 = 全部按目录打包（历史行为）。
  crossDirGroupRules: string | null
  /** 备份范围。null = 根下全部内容。不可继承，因此不出现在 EffectiveBackupSettings 里。 */
  scopeRules: string | null
  includeSymlinks: boolean | null
  maxVersions: number | null
  maxAgeDays: number | null
  retentionMode: number | null
  singleFileThresholdBytes: number | null
  groupCapBytes: number | null
  volumeBytes: number | null
  verboseLogging: boolean | null
  effective: EffectiveBackupSettings
  createdAt: string
  status: number // BackupStatus
  lastError: string | null
  lastErrorAt: string | null
  activity: BackupActivity
  secretsUnavailable: boolean
}

export interface BackupConfigInput {
  accountId: number
  containerName: string
  name: string
  description: string | null
  localRoot: string
  password: string | null
  indexTier: number
  dataTier: number
  ignoreRules: string | null
  dontCompressRules: string | null
  dontGroupRules: string | null
  // 命中者允许跨目录装箱。空 = 全部按目录打包（历史行为）。
  crossDirGroupRules: string | null
  /** 备份范围。null = 根下全部内容。不可继承，因此不出现在 EffectiveBackupSettings 里。 */
  scopeRules: string | null
  includeSymlinks: boolean | null
  maxVersions: number | null
  maxAgeDays: number | null
  retentionMode: number | null
  singleFileThresholdBytes: number | null
  groupCapBytes: number | null
  volumeBytes: number | null
  verboseLogging: boolean | null
}

// 迁移本地根路径的校验报告（后端 LocalRootPreviewResponse）。
// 形状定义在 lib/localRootVerdict.ts，与判定逻辑放一起，这里只做转出。
export type { LocalRootPreview } from '../lib/localRootVerdict'

// 某个阶段正在做什么。上传之外的阶段此前完全没有进度——扫描和 diff 各自只在进入时报一次，
// 而首次备份的 diff 要把每个文件完整读一遍算 hash，可以跑几小时。
/** 一条正在传的流。label 是**源文件路径**（上传）或包的描述，不是内容寻址的 blob 名。 */
export interface ActiveTransfer {
  label: string
  sent: number
  total: number // 0 = 未知（下载在拿到响应头前不知道）
  percent: number | null
}

export interface StageProgress {
  stage: string
  processed: number
  total: number // 0 = 总数未知（扫描还没走完）
  bytes: number // 边传边加，**含在途**——测速用的那个
  currentItem: string | null
  activeItems: ActiveTransfer[]
  bytesPerSecond: number
  preparing: number // 正占着全局归档锁产出卷文件的（可以持续几十秒）——按锁的定义只会是 0 或 1
  queued: number // 还没被领走的——只数队列里的（排归档锁的见 waitingOnArchive）
  // 已领走、正排在**归档锁**后面干等的件数。那把锁是全局的（StagingArea 是单例，产出跨备份也不
  // 并发），所以一个备份的线程可以整段排在**另一个备份**手里的锁后面——那时本备份 preparing=0，
  // 合进 queued 的话屏幕上就只剩一万条 "queued"，说不出"它被别人挡着"。
  // 拆开之后判别是免费的：preparing=1 + 有人在等 = 锁在自己手里；preparing=0 + 有人在等 = 在别人手里。
  waitingOnArchive: number
  percent: number | null
  etaSeconds: number | null // 后端按全程平均进度外推的剩余秒数；estimatedRemaining 由它派生
  estimatedRemaining: string | null // .NET TimeSpan 序列化为 "hh:mm:ss"
  // 字节明细。各段互不重叠：workRemaining（还没处理的源字节）、stagedBytes（已压好没送出去的）、
  // transferredBytes（已落云且整件已完成的）、unfinishedItemBytes（已落云但整件还没完成的）。
  // workDone/workTotal 是压缩前口径，用于算真实完成度。
  workTotal: number // 源端字节总量（压缩前）。上传阶段在 diff 判完前还会往上长
  workDone: number // 其中已彻底完工的（不含在途）
  workRemaining: number
  transferredBytes: number // 已完工的项真正推上网线的字节（压缩后，不含在途）
  // 已落云、但所属那件活还没完成的字节（压缩后）。一件大活切成许多卷，前几卷传完时那些字节
  // 确实到了云上，可整件没完成，既进不了 transferredBytes（那本账按件记，才对得上按件销账的
  // workDone），也已不在 stagedBytes 里（池子逐卷释放）。整件完成时并入前者并归零。
  unfinishedItemBytes: number
  stagedBytes: number // 已压好、还没送出去的（压缩后）
  transferTotal: number // 这一阶段一共要过多少网线字节；0 = 未知（上传侧压完才知道，恒为 0）
  workPercent: number | null // 按源字节算的完成度；总量未定时为 null
  // 差分判得比压缩上传快几个数量级，必然跑到上传前面去；多出来的活攒到磁盘上（累计件数，只增）。
  // 从前这里是一个 boolean「被队列挡住了」——写侧现在不再阻塞，diff 一路跑到底，
  // 上传的剩余时间才有分母（总数只有 diff 收工才确定）。这个数是那件事的量化读数。
  spilledItems: number
  // 已经离开压缩/暂存段的**件**数：压缩早已完成，此后要么有卷在飞，要么正卡在下面那三段之一。
  // 与 activeItems 是两个口径——那里装的是**卷**，一件活可以同时有好几卷在飞，也可以一卷都没有。
  // processed + preparing + queued + uploading ≡ total 是个恒等式，屏幕上的数必须凑得出它：
  // 从前凑不出，一件卡在这一段的活谁也不算，只能靠把几屏截图排在一起做减法才发现得了。
  uploading: number
  waitingOnPeer: number // 其中在等同批同内容的首个上传者传完的件数
  waitingOnSlot: number // 其中在排全局上传闸门的**卷**数（闸门按卷排队，单位与另外两个不同）
  // 其中正在读盘核对、既不推字节也不在等任何东西的件数：单文件去重预筛整读算三段 hash、
  // 一箱 pack 压缩前后各逐成员 stat（变了的还要整读重算 hash）、加密多卷上传前列举云端残留卷。
  // 这几段在 NAS 上都能跑几十秒，而且一个进度事件都不发——从前屏幕上是一动不动的
  // "1 object starting upload"，既没在 starting 也没在 upload。
  // 它是 uploading 的**细分**（都发生在出了暂存段、还没登记在途卷的时候），所以算 starting upload
  // 时必须把它减掉，否则同一件活会被报两栏，屏幕上的数就凑不出那条恒等式了。
  checking: number
}

export interface BackupProgress {
  stage: number
  changedFiles: number
  changedBytes: number
  uploadedItems: number
  totalItems: number
  percent: number
  // 流水线化之后 Diffing 与 Uploading 是同时在跑的，所以明细是一个列表。
  details: StageProgress[]
  // 头条明细（= details[0]）。串行阶段只有一条，就是它。
  detail: StageProgress | null
}

// 后台运行的终态。Canceled = 用户按了停止：既不是成功也不是失败，后端因此不会把它写成
// 该备份的 Error 状态（否则停一次就要手动 Reset 一次）。
// Suspended = 现场安全保存后停下的，语义上离 Canceled 更近而不是 Failed：下次跑会原样接上。
export type RunStatus = 'Running' | 'Completed' | 'Failed' | 'Canceled' | 'Suspended'

// 卡在瞬时错误上等自愈重试。**这不是一种状态**：status 仍然是 Running，因为后台那个 Task
// 还活着、暂存席位也还占着。写成状态会让停止按钮消失，而卡住的时候恰恰是最想停的时候。
export interface PauseInfo {
  reason: string
  since: string
  // 下次自动重试的时刻（UTC）。已经在重试路上时为 null。
  nextRetryAt: string | null
  // 连续失败次数。涨到阈值就自动转挂起。
  failures: number
}

// 盘上留着的一次中途停下的运行（程序重启后内存里什么都没有，只能从这里知道）。
export interface InterruptedRun {
  runId: string
  startedAt: string
  // journal 里已确认在云上的块数。接着跑能省下的，大致就是这么多。
  blocks: number
  journalBytes: number
  // 便宜的那几项前置校验的预览，**不是承诺**：基线版本与加密身份要读索引和密码才能核，
  // 那要等真正开卷时才做。这里为 true 而开卷时仍被判作废是可能的。
  resumable: boolean
}

export interface BackupRun {
  status: RunStatus
  progress: BackupProgress | null
  version: number | null
  // 本轮读不开、因而沿用了旧索引条目的文件数。一次"成功"的备份可能什么都没存下来。
  unreadableFiles: number | null
  error: string | null
  // 本次备份的起止时刻（UTC），取自版本记录——与 /versions 给还原对话框的是同一组数字。
  // 运行中、以及未带这两个字段的老后端，为 null。
  startedAt: string | null
  completedAt: string | null
  // 本次运行的标识，与盘上的 journal 文件同名。
  runId: string
  // 非 null＝正卡在瞬时错误上等重试。status 仍是 'Running'。
  pause: PauseInfo | null
  // status === 'Suspended' 时的缘由：UserRequested / AutoSuspended。
  suspendReason: string | null
}

export interface RestoreRun {
  status: RunStatus
  version: number | null
  restoredFiles: number | null
  skippedFiles: number | null
  failedFiles: number | null
  detail: StageProgress | null
  // 跳过/失败的逐条记录。此前只有 phase 一个单值字段，后一条覆盖前一条，
  // 跑完只剩最后一条，其余仅体现为 failedFiles 那个数字。
  events: string[] | null
  error: string | null
  phase: string | null
}

export interface BackupVersionInfo {
  version: number
  /** 版本提交时刻（备份结束）。UTC，显示时转本地时区。 */
  createdAt: string
  /** 备份开始跑的时刻。UTC。此字段问世前写下的版本为 null。 */
  startedAt: string | null
  files: number
  bytes: number
  changedFiles: number
}

// 分级检查（枚举按数值序列化，与后端一致）。CloudCheckLevel/LocalCheckLevel 与 api/tasks.ts
// 共用同一份定义，见 constants/labels.ts（§5.7 合并重复 label 字典）。
export { CloudCheckLevel, LocalCheckLevel } from '../constants/labels'
export const CloudState = { NotChecked: 0, Ok: 1, MissingOrBad: 2 } as const
export const LocalState = { NotChecked: 0, Ok: 1, Missing: 2, Changed: 3 } as const

export interface FileFinding {
  path: string
  ref: string | null
  cloud: number // CloudState
  local: number // LocalState
  repairable: boolean
  // 非空＝云端这份是从更早版本沿用来的（备份一直读不到源文件）。没有它，local=Changed
  // 会被读成"本地被改了"，而真实原因是备份从未成功更新过云端这一份。
  unreadableAt: string | null
}

export interface CheckReport {
  version: number
  findings: FileFinding[]
  metadataIssue: string | null
  ok: boolean
  missingRefs: string[]
  corruptedPaths: string[]
  repairablePaths: string[]
  orphanBlobs: string[]
}

export interface RepairReport {
  repaired: string[]
  unrecoverable: string[]
  deletedOrphans: string[]
}

export interface RepairRun {
  status: RunStatus
  repaired: string[] | null
  unrecoverable: string[] | null
  deletedOrphans: string[] | null
  error: string | null
}

// 检查现在是后台 job（202 + 轮询）：内容级要把整个备份下载重算一遍 hash，同步端点时代
// 请求会先被浏览器/反向代理超时掐断。报告跑完仍留在服务端，关掉对话框再打开还能看回结果。
export interface CheckRun {
  status: RunStatus
  report: CheckReport | null
  error: string | null
  detail: StageProgress | null
}

export interface FileVersionOption {
  version: number
  createdAt: string
  length: number
}

// 还原树浏览节点（§4.1a）：目录的直接子节点，懒加载展开用。
export interface TreeNode {
  name: string
  path: string
  isDir: boolean
  hasChildren: boolean
  length: number | null
  mtime: string | null
  storageKind: string | null
  storageRef: string | null
  // 非空＝这条记录沿用自更早的版本，值为自何时起没能再更新。还原选择时必须看得到：
  // 还原这个版本，拿到的不是这个版本时刻的内容。
  unreadableAt: string | null
}

// 某版本里内容为沿用的文件（备份那几轮读不开源文件）。与 unrecoverable 不同：内容有效，只是旧。
export interface UnreadableEntry {
  path: string
  unreadableAt: string
}

// 还原量估算（§4.1b）：本地纯算下载量/解压量/文件数，再对去重存储对象 HEAD 查活化状态。
export interface RestoreEstimate {
  downloadBytes: number
  uncompressedBytes: number
  fileCount: number
  archivedObjects: number
  rehydratePending: number
}

/** 一次导入的结果：建好的配置，加上导入自己发现的两件事。 */
export interface ImportResult {
  config: BackupConfig
  /** 云端核验已经在后台跑起来了——直接把检查面板打开，别让用户再去找那个按钮。 */
  checkStarted: boolean
  /** 文件列表读不出来的版本号。这些版本还原不了也检查不了，其余版本不受影响。 */
  unreadableVersions: number[]
}

export const backupConfigsApi = {
  list: () => api.get<BackupConfig[]>('/backup-configs'),
  get: (id: number) => api.get<BackupConfig>(`/backup-configs/${id}`),
  create: (input: BackupConfigInput) => api.post<BackupConfig>('/backup-configs', input),
  import: (
    accountId: number,
    containerName: string,
    password: string | null,
    checkAfterImport: boolean,
  ) =>
    api.post<ImportResult>('/backup-configs/import', {
      accountId,
      containerName,
      password,
      checkAfterImport,
    }),
  update: (id: number, input: BackupConfigInput) =>
    api.put<BackupConfig>(`/backup-configs/${id}`, input),
  remove: (id: number, deleteContainer = false) =>
    api.del(`/backup-configs/${id}${deleteContainer ? '?deleteContainer=true' : ''}`),
  resetStatus: (id: number) => api.post<void>(`/backup-configs/${id}/reset-status`, {}),
  // 迁移本地根路径。preview 是纯查询，可反复试；changeLocalRoot 才真的改。
  previewLocalRoot: (id: number, newRoot: string) =>
    api.post<LocalRootPreview>(`/backup-configs/${id}/local-root/preview`, { newRoot }),
  changeLocalRoot: (id: number, newRoot: string, force: boolean) =>
    api.post<BackupConfig>(`/backup-configs/${id}/local-root`, { newRoot, force }),
  run: (id: number) => api.post<BackupRun>(`/backup-configs/${id}/run`, {}),
  runStatus: (id: number) => api.get<BackupRun>(`/backup-configs/${id}/run`),
  versions: (id: number) => api.get<BackupVersionInfo[]>(`/backup-configs/${id}/versions`),
  tree: (id: number, version: number | null, path: string | null) =>
    api.get<TreeNode[]>(`/backup-configs/${id}/tree?${new URLSearchParams({
      ...(version != null ? { version: String(version) } : {}),
      ...(path ? { path } : {}),
    })}`),
  restoreEstimate: (id: number, version: number | null, paths: string[]) =>
    api.post<RestoreEstimate>(`/backup-configs/${id}/restore-estimate`, { version, paths }),
  restore: (
    id: number,
    targetRoot: string | null,
    version: number | null,
    substitutions?: Record<string, number>,
    // 选择性还原（需求 B）：为空则还原整版本；非空则只还原恰好这些路径（pack 只下一次、只写选中成员）。
    selectedPaths?: string[] | null,
    conflict: number = RestoreConflictMode.OverwriteIfChanged, // 冲突模式（决策 3）
    rehydratePriority: number = RestoreRehydratePriority.Standard, // Archive 活化优先级
  ) =>
    api.post<RestoreRun>(`/backup-configs/${id}/restore`, {
      targetRoot,
      version,
      substitutions,
      selectedPaths,
      conflict,
      rehydratePriority,
    }),
  restoreStatus: (id: number) => api.get<RestoreRun>(`/backup-configs/${id}/restore`),
  fileVersions: (id: number, path: string) =>
    api.get<FileVersionOption[]>(`/backup-configs/${id}/file-versions?path=${encodeURIComponent(path)}`),
  unrecoverablePaths: (id: number, version: number | null) =>
    api.get<string[]>(`/backup-configs/${id}/unrecoverable${version != null ? `?version=${version}` : ''}`),
  unreadableEntries: (id: number, version: number | null) =>
    api.get<UnreadableEntry[]>(`/backup-configs/${id}/unreadable${version != null ? `?version=${version}` : ''}`),
  check: (id: number, cloud: number, local: number, version: number | null = null, rehydrate: number | null = null, listOrphans = false) => {
    const p = new URLSearchParams()
    p.set('cloud', String(cloud))
    p.set('local', String(local))
    if (version != null) p.set('version', String(version))
    if (rehydrate != null) p.set('rehydrate', String(rehydrate))
    if (listOrphans) p.set('listOrphans', 'true')
    // 202：只是把检查跑起来，结果要靠 checkStatus 轮询。
    return api.post<CheckRun>(`/backup-configs/${id}/check?${p.toString()}`, {})
  },
  // 从没查过这个备份时后端答 204（不是 404：那会在浏览器控制台留下红色报错），这里拿到的是空。
  checkStatus: (id: number) => api.get<CheckRun | null>(`/backup-configs/${id}/check`),
  repair: (id: number, cloud: number, version: number | null = null, rehydrate: number | null = null, cleanupOrphans = false) => {
    const p = new URLSearchParams()
    p.set('cloud', String(cloud))
    if (version != null) p.set('version', String(version))
    if (rehydrate != null) p.set('rehydrate', String(rehydrate))
    if (cleanupOrphans) p.set('cleanupOrphans', 'true')
    return api.post<RepairRun>(`/backup-configs/${id}/repair?${p.toString()}`, {})
  },
  repairStatus: (id: number) => api.get<RepairRun>(`/backup-configs/${id}/repair`),
  // 停止正在跑的操作。what 省略＝停掉这个配置上所有在跑的操作。
  //
  // 备份这一支后端是**等落盘再答**的，所以这个 Promise resolve 就意味着现场已经安全了——
  // 除非 stopping 为 true：那表示后端等了 20 秒还没收尾（正在传的大文件还没传完），
  // 运行仍会走到终态，界面继续轮询即可。
  //
  // finishCurrentFiles=true：正在传的文件（含它所有分卷）传完再停，这部分算数；
  // false：立刻停，半截的分卷和在途的块都删掉，不留没法用的残渣。
  cancel: (id: number, what?: 'backup' | 'restore' | 'repair' | 'check', finishCurrentFiles = false) => {
    const p = new URLSearchParams()
    if (what) p.set('what', what)
    if (finishCurrentFiles) p.set('finishCurrentFiles', 'true')
    const q = p.toString()
    return api.post<{ canceled: string[]; stopping?: boolean }>(
      `/backup-configs/${id}/cancel${q ? `?${q}` : ''}`, {})
  },
  // 挂起：安全落盘后停下。**没有对应的 resume**——恢复不是一种模式，每一轮备份开卷时都会去认
  // 还有效的 journal，所以"继续"就是再调一次 run()。
  suspend: (id: number) => api.post<void>(`/backup-configs/${id}/suspend`, {}),
  // 不等自愈计时器，立刻放行一次重试。
  retryNow: (id: number) => api.post<void>(`/backup-configs/${id}/retry-now`, {}),
  interrupted: (id: number) => api.get<InterruptedRun[]>(`/backup-configs/${id}/interrupted`),
  discardInterrupted: (id: number) => api.del(`/backup-configs/${id}/interrupted`),
  resetPassword: (id: number, password: string) =>
    api.post<void>(`/backup-configs/${id}/reset-password`, { password }),
}
