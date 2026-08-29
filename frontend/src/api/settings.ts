import { api } from './client'

// The 7z process's CPU priority. Lowest is 0 — the backend enum is ordered so that adding the
// column with 0 for existing rows lands them on the lowest tier.
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
  defaultIgnoreRulesCaseInsensitive: string | null
  defaultDontCompressRulesCaseInsensitive: string | null
  defaultDontGroupRulesCaseInsensitive: string | null
  defaultCrossDirGroupRulesCaseInsensitive: string | null
  defaultDontGroupRules: string | null
  defaultCrossDirGroupRules: string | null
  uploadConcurrency: number
  downloadConcurrency: number
  checkHeadConcurrency: number
  logEphemeralMaxAgeDays: number
  defaultVerboseLogging: boolean
  retryBackoffSeconds: string
  retryMaxTotalMinutes: number
  deadWeightThresholdPercent: number
  stagedLimitBytes: number
  // Staging pool policy when full: false = strict ceiling (everyone waits); true = 20% split as per-run
  // guarantees + 80% first-come shared, so one oversized family cannot completely starve the others.
  stagingFairShare: boolean
  processingMaxAttempts: number
  overlapDiffAndUpload: boolean
  autoResumeInterruptedRuns: boolean
  sevenZipPriority: number
}

export const settingsApi = {
  get: () => api.get<GlobalSettings>('/settings'),
  update: (s: GlobalSettings) => api.put<GlobalSettings>('/settings', s),
}
