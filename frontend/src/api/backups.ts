import { api } from './client'

// 已发现的备份（来自各账户 container 发现，PRD 2.1）
export const backupPresenceLabels: Record<number, string> = {
  1: 'Plain',
  2: 'Encrypted',
}

export interface DiscoveredBackup {
  accountId: number
  accountName: string
  containerName: string
  presence: number
}

// 备份的稳定标识：account + container
export const backupKey = (b: { accountId: number; containerName: string }) =>
  `${b.accountId}/${b.containerName}`

export const backupsApi = {
  list: () => api.get<DiscoveredBackup[]>('/backups'),
}
