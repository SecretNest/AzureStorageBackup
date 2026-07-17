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
  error: string | null
}

export interface BackupVersionInfo {
  version: number
  createdAt: string
  files: number
  bytes: number
  changedFiles: number
}

export interface CheckResult {
  version: number
  checkedRefs: number
  missingRefs: string[]
  corruptedPaths: string[]
  ok: boolean
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
  restore: (id: number, targetRoot: string | null, version: number | null) =>
    api.post<RestoreRun>(`/backup-configs/${id}/restore`, { targetRoot, version }),
  restoreStatus: (id: number) => api.get<RestoreRun>(`/backup-configs/${id}/restore`),
  check: (id: number, deep = false) =>
    api.post<CheckResult>(`/backup-configs/${id}/check${deep ? '?deep=true' : ''}`, {}),
}
