import { api } from './client'

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

/** 后端解析后的生效值（null 字段已用全局设置填充）。只读，仅供显示。 */
export interface EffectiveBackupSettings {
  ignoreRules: string | null
  dontCompressRules: string | null
  dontGroupRules: string | null
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
  includeSymlinks: boolean | null
  maxVersions: number | null
  maxAgeDays: number | null
  retentionMode: number | null
  singleFileThresholdBytes: number | null
  groupCapBytes: number | null
  volumeBytes: number | null
  verboseLogging: boolean | null
}

// 某个阶段正在做什么。上传之外的阶段此前完全没有进度——扫描和 diff 各自只在进入时报一次，
// 而首次备份的 diff 要把每个文件完整读一遍算 hash，可以跑几小时。
export interface StageProgress {
  stage: string
  processed: number
  total: number // 0 = 总数未知（扫描还没走完）
  bytes: number
  currentItem: string | null
  activeItems: string[]
  bytesPerSecond: number
  percent: number | null
  estimatedRemaining: string | null // .NET TimeSpan 序列化为 "hh:mm:ss"
}

export interface BackupProgress {
  stage: number
  changedFiles: number
  changedBytes: number
  uploadedItems: number
  totalItems: number
  percent: number
  detail: StageProgress | null
}

export interface BackupRun {
  status: 'Running' | 'Completed' | 'Failed'
  progress: BackupProgress | null
  version: number | null
  // 本轮读不开、因而沿用了旧索引条目的文件数。一次"成功"的备份可能什么都没存下来。
  unreadableFiles: number | null
  error: string | null
}

export interface RestoreRun {
  status: 'Running' | 'Completed' | 'Failed'
  version: number | null
  restoredFiles: number | null
  skippedFiles: number | null
  failedFiles: number | null
  error: string | null
  phase: string | null
}

export interface BackupVersionInfo {
  version: number
  createdAt: string
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
  status: 'Running' | 'Completed' | 'Failed'
  repaired: string[] | null
  unrecoverable: string[] | null
  deletedOrphans: string[] | null
  error: string | null
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

export const backupConfigsApi = {
  list: () => api.get<BackupConfig[]>('/backup-configs'),
  get: (id: number) => api.get<BackupConfig>(`/backup-configs/${id}`),
  create: (input: BackupConfigInput) => api.post<BackupConfig>('/backup-configs', input),
  import: (accountId: number, containerName: string, password: string | null) =>
    api.post<BackupConfig>('/backup-configs/import', { accountId, containerName, password }),
  update: (id: number, input: BackupConfigInput) =>
    api.put<BackupConfig>(`/backup-configs/${id}`, input),
  remove: (id: number, deleteContainer = false) =>
    api.del(`/backup-configs/${id}${deleteContainer ? '?deleteContainer=true' : ''}`),
  resetStatus: (id: number) => api.post<void>(`/backup-configs/${id}/reset-status`, {}),
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
    return api.post<CheckReport>(`/backup-configs/${id}/check?${p.toString()}`, {})
  },
  repair: (id: number, cloud: number, version: number | null = null, rehydrate: number | null = null, cleanupOrphans = false) => {
    const p = new URLSearchParams()
    p.set('cloud', String(cloud))
    if (version != null) p.set('version', String(version))
    if (rehydrate != null) p.set('rehydrate', String(rehydrate))
    if (cleanupOrphans) p.set('cleanupOrphans', 'true')
    return api.post<RepairRun>(`/backup-configs/${id}/repair?${p.toString()}`, {})
  },
  repairStatus: (id: number) => api.get<RepairRun>(`/backup-configs/${id}/repair`),
  resetPassword: (id: number, password: string) =>
    api.post<void>(`/backup-configs/${id}/reset-password`, { password }),
}
