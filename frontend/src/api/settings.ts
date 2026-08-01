import { api } from './client'

// 7z 进程的 CPU 优先级。Lowest 是 0——后端枚举照着"加列时既有行填 0 就该落在最低档"排的。
export const SevenZipCpuPriority = { Lowest: 0, BelowNormal: 1, Normal: 2 } as const

export const sevenZipPriorityLabels: Record<number, string> = {
  0: 'Lowest (default)',
  1: 'Below normal',
  2: 'Normal',
}

export interface GlobalSettings {
  defaultIndexTier: number
  defaultDataTier: number
  defaultMaxVersions: number
  defaultMaxAgeDays: number
  defaultRetentionMode: number
  defaultSingleFileThresholdBytes: number
  defaultGroupCapBytes: number
  defaultVolumeBytes: number | null
  repackDownloadHot: boolean
  repackDownloadCool: boolean
  repackDownloadCold: boolean
  repackDownloadArchive: boolean
  defaultIncludeSymlinks: boolean
  defaultIgnoreRules: string | null
  defaultDontCompressRules: string | null
  defaultDontGroupRules: string | null
  defaultCrossDirGroupRules: string | null
  uploadConcurrency: number
  downloadConcurrency: number
  logEphemeralMaxAgeDays: number
  defaultVerboseLogging: boolean
  retryBackoffSeconds: string
  retryMaxTotalMinutes: number
  deadWeightThresholdPercent: number
  stagedLimitBytes: number
  processingMaxAttempts: number
  overlapDiffAndUpload: boolean
  sevenZipPriority: number
}

export const settingsApi = {
  get: () => api.get<GlobalSettings>('/settings'),
  update: (s: GlobalSettings) => api.put<GlobalSettings>('/settings', s),
}
