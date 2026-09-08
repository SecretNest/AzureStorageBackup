import { api } from './client'

// The 7z process's CPU priority. Lowest is 0 — the backend enum is ordered so that adding the
// column with 0 for existing rows lands them on the lowest tier.
export const SevenZipCpuPriority = { Lowest: 0, BelowNormal: 1, Normal: 2 } as const

export const sevenZipPriorityLabels: Record<number, string> = {
  0: 'Lowest (default)',
  1: 'Below normal',
  2: 'Normal',
}

// One settings row on the server, two resources on the API — one per settings page. Each page reads and writes its
// own half, so a save on one page can never carry (and overwrite) the other page's fields; the whole object is
// read-only, for the pages that only need to look (the backup form's inherited defaults).
export interface BackupDefaultsSettings {
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
  defaultIgnoreRulesCaseInsensitive: string | null
  defaultDontCompressRulesCaseInsensitive: string | null
  defaultDontGroupRulesCaseInsensitive: string | null
  defaultCrossDirGroupRulesCaseInsensitive: string | null
  defaultDontGroupRules: string | null
  defaultCrossDirGroupRules: string | null
}

export interface PerformanceSettings {
  uploadConcurrency: number
  uploadMemoryLimitBytes: number
  downloadConcurrency: number
  checkHeadConcurrency: number
  logEphemeralMaxAgeDays: number
  defaultVerboseLogging: boolean
  retryBackoffSeconds: string
  retryMaxTotalMinutes: number
  deadWeightThresholdPercent: number
  stagedLimitBytes: number
  processingMaxAttempts: number
  overlapDiffAndUpload: boolean
  autoResumeInterruptedRuns: boolean
  sevenZipPriority: number
}

export type GlobalSettings = BackupDefaultsSettings & PerformanceSettings

export const settingsApi = {
  get: () => api.get<GlobalSettings>('/settings'),
  getDefaults: () => api.get<BackupDefaultsSettings>('/settings/defaults'),
  updateDefaults: (s: BackupDefaultsSettings) => api.put<BackupDefaultsSettings>('/settings/defaults', s),
  getPerformance: () => api.get<PerformanceSettings>('/settings/performance'),
  updatePerformance: (s: PerformanceSettings) => api.put<PerformanceSettings>('/settings/performance', s),
}
