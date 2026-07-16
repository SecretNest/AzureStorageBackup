import { api } from './client'

export const NotificationMethod = { Get: 0, Post: 1 } as const

// 与后端 [Flags] NotificationEvents 对应
export const NotificationEvent = {
  BackupStart: 1,
  BackupSuccess: 2,
  BackupFailure: 4,
  RestoreStart: 8,
  RestoreSuccess: 16,
  RestoreFailure: 32,
  CheckStart: 64,
  CheckSuccess: 128,
  CheckFailure: 256,
  UnrecoverableError: 512,
} as const

export const eventList: { bit: number; label: string }[] = [
  { bit: NotificationEvent.BackupStart, label: 'Backup start' },
  { bit: NotificationEvent.BackupSuccess, label: 'Backup success' },
  { bit: NotificationEvent.BackupFailure, label: 'Backup failure' },
  { bit: NotificationEvent.RestoreStart, label: 'Restore start' },
  { bit: NotificationEvent.RestoreSuccess, label: 'Restore success' },
  { bit: NotificationEvent.RestoreFailure, label: 'Restore failure' },
  { bit: NotificationEvent.CheckStart, label: 'Check start' },
  { bit: NotificationEvent.CheckSuccess, label: 'Check success' },
  { bit: NotificationEvent.CheckFailure, label: 'Check failure' },
  { bit: NotificationEvent.UnrecoverableError, label: 'Unrecoverable error' },
]

export interface NotificationConfig {
  enabled: boolean
  url: string
  method: number
  bodyTemplate: string | null
  contentType: string | null
  events: number
  proxyUrl: string | null
}

export interface TestResult {
  success: boolean
  error: string | null
}

export const notificationsApi = {
  get: () => api.get<NotificationConfig>('/notifications'),
  update: (cfg: NotificationConfig) => api.put<NotificationConfig>('/notifications', cfg),
  test: (cfg: NotificationConfig) => api.post<TestResult>('/notifications/test', cfg),
}
