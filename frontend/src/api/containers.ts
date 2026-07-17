import { api } from './client'

// 与后端 BackupPresence 对应
export const BackupPresence = { None: 0, Plain: 1, Encrypted: 2 } as const

export const backupPresenceLabels: Record<number, string> = {
  0: 'Empty',
  1: 'Backup',
  2: 'Backup (encrypted)',
}

// 信息记录文件的约定文件名（后端 BackupDiscovery，PRD 1.3）。用于展示。
export const infoFileName = (presence: number): string | null =>
  presence === BackupPresence.Plain
    ? 'azurestoragebackup.index.json'
    : presence === BackupPresence.Encrypted
      ? 'azurestoragebackup.index.json.enc'
      : null

export interface ContainerInfo {
  name: string
  backup: number
}

export const containersApi = {
  list: (accountId: number) => api.get<ContainerInfo[]>(`/accounts/${accountId}/containers`),
  create: (accountId: number, name: string) =>
    api.post<{ name: string }>(`/accounts/${accountId}/containers`, { name }),
  remove: (accountId: number, name: string) =>
    api.del(`/accounts/${accountId}/containers/${name}`),
}
