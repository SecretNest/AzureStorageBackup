import { api } from './client'
import type { NotificationConfig } from './notifications'

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

/** One account in a settings file: the AccountInput fields, secrets plaintext or null. */
export interface SettingsAccountEntry {
  name: string
  description: string | null
  blobEndpoint: string
  region: number
  accountKey: string | null
  useProxy: boolean
  proxyMode: number
  proxyHost: string | null
  proxyPort: number | null
  proxyUsername: string | null
  proxyPassword: string | null
}

/**
 * The settings file. Every section is optional on import; a missing one leaves that part of the server alone, and
 * so does a missing field inside a section (the server lays the file over its current values). The sections are
 * typed here for the export's benefit; on import the server reads them as plain JSON objects.
 */
export interface SettingsDocument {
  format: string
  version: number
  exportedAt?: string | null
  includesSecrets?: boolean
  accounts?: SettingsAccountEntry[] | null
  backupDefaults?: BackupDefaultsSettings | null
  performance?: PerformanceSettings | null
  notifications?: NotificationConfig | null
}

export interface ImportPlanAccount {
  name: string
  blobEndpoint: string
  action: 'create' | 'update'
  /** A create whose entry has no key: the user must type one before the import can run. */
  needsAccountKey: boolean
}

export interface ImportPlan {
  accounts: ImportPlanAccount[]
  backupDefaults: boolean
  performance: boolean
  notifications: boolean
}

export const settingsApi = {
  get: () => api.get<GlobalSettings>('/settings'),
  getDefaults: () => api.get<BackupDefaultsSettings>('/settings/defaults'),
  updateDefaults: (s: BackupDefaultsSettings) => api.put<BackupDefaultsSettings>('/settings/defaults', s),
  getPerformance: () => api.get<PerformanceSettings>('/settings/performance'),
  updatePerformance: (s: PerformanceSettings) => api.put<PerformanceSettings>('/settings/performance', s),
  export: (includeSecrets: boolean) =>
    api.get<SettingsDocument>(`/settings/export?includeSecrets=${includeSecrets}`),
  previewImport: (doc: SettingsDocument) => api.post<ImportPlan>('/settings/import/preview', doc),
  import: (doc: SettingsDocument) => api.post<ImportPlan>('/settings/import', doc),
}
