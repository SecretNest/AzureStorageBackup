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
export const backupStageLabels: Record<number, string> = {
  0: 'Scanning',
  1: 'Diffing',
  2: 'Uploading',
  3: 'Writing index',
  4: 'Finalizing',
  5: 'Cleaning up',
  6: 'Completed',
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
  includeSymlinks: boolean
  maxVersions: number
  maxAgeDays: number
  retentionMode: number
  singleFileThresholdBytes: number
  groupCapBytes: number
  volumeBytes: number | null
  verboseLogging: boolean
  createdAt: string
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
  includeSymlinks: boolean
  maxVersions: number
  maxAgeDays: number
  retentionMode: number
  singleFileThresholdBytes: number
  groupCapBytes: number
  volumeBytes: number | null
  verboseLogging: boolean
}

export interface BackupProgress {
  stage: number
  changedFiles: number
  changedBytes: number
  uploadedItems: number
  totalItems: number
  percent: number
}

export interface BackupRun {
  status: 'Running' | 'Completed' | 'Failed'
  progress: BackupProgress | null
  version: number | null
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

// 分级检查（枚举按数值序列化，与后端一致）
export const CloudCheckLevel = { None: 0, Metadata: 1, ExistenceSize: 2, Content: 3 } as const
export const LocalCheckLevel = { None: 0, Attributes: 1, Content: 2 } as const
export const CloudState = { NotChecked: 0, Ok: 1, MissingOrBad: 2 } as const
export const LocalState = { NotChecked: 0, Ok: 1, Missing: 2, Changed: 3 } as const

export interface FileFinding {
  path: string
  ref: string | null
  cloud: number // CloudState
  local: number // LocalState
  repairable: boolean
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

export const backupConfigsApi = {
  list: () => api.get<BackupConfig[]>('/backup-configs'),
  get: (id: number) => api.get<BackupConfig>(`/backup-configs/${id}`),
  create: (input: BackupConfigInput) => api.post<BackupConfig>('/backup-configs', input),
  import: (accountId: number, containerName: string, password: string | null) =>
    api.post<BackupConfig>('/backup-configs/import', { accountId, containerName, password }),
  update: (id: number, input: BackupConfigInput) =>
    api.put<BackupConfig>(`/backup-configs/${id}`, input),
  remove: (id: number) => api.del(`/backup-configs/${id}`),
  run: (id: number) => api.post<BackupRun>(`/backup-configs/${id}/run`, {}),
  runStatus: (id: number) => api.get<BackupRun>(`/backup-configs/${id}/run`),
  versions: (id: number) => api.get<BackupVersionInfo[]>(`/backup-configs/${id}/versions`),
  restore: (id: number, targetRoot: string | null, version: number | null, substitutions?: Record<string, number>) =>
    api.post<RestoreRun>(`/backup-configs/${id}/restore`, { targetRoot, version, substitutions }),
  restoreStatus: (id: number) => api.get<RestoreRun>(`/backup-configs/${id}/restore`),
  fileVersions: (id: number, path: string) =>
    api.get<FileVersionOption[]>(`/backup-configs/${id}/file-versions?path=${encodeURIComponent(path)}`),
  unrecoverablePaths: (id: number, version: number | null) =>
    api.get<string[]>(`/backup-configs/${id}/unrecoverable${version != null ? `?version=${version}` : ''}`),
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
}
