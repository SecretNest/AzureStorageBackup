import { api } from './client'

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
  uploadConcurrency: number
  logMaxEntries: number
  logMaxAgeDays: number
  retryBackoffSeconds: string
  retryMaxTotalMinutes: number
  deadWeightThresholdPercent: number
}

export const settingsApi = {
  get: () => api.get<GlobalSettings>('/settings'),
  update: (s: GlobalSettings) => api.put<GlobalSettings>('/settings', s),
}
