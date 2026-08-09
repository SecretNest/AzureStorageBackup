import { api } from './client'

// A discovered backup, found by scanning each account's containers (PRD 2.1)
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

// A backup's stable identity: account + container
export const backupKey = (b: { accountId: number; containerName: string }) =>
  `${b.accountId}/${b.containerName}`

export const backupsApi = {
  list: () => api.get<DiscoveredBackup[]>('/backups'),
}
