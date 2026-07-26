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
    api.del(`/accounts/${accountId}/containers/${encodeURIComponent(name)}`),
}

/**
 * Azure container 命名规则。与后端 Services/ContainerName.cs 保持等价——
 * 后端是权威，这份存在只是为了在敲键时就给出反馈，而不是等一趟网络往返。
 * 改动其中一处务必同步另一处。
 */
export const containerNameRule =
  '3–63 characters; lowercase letters, digits, and hyphens only; must begin and end with a letter or digit; no consecutive hyphens.'

export function validateContainerName(name: string): string | null {
  if (name.length < 3 || name.length > 63)
    return 'Container name must be between 3 and 63 characters long.'
  if (!/^[a-z0-9-]+$/.test(name))
    return 'Container name may only contain lowercase letters, digits, and hyphens.'
  if (name.startsWith('-') || name.endsWith('-'))
    return 'Container name must begin and end with a letter or a digit.'
  if (name.includes('--'))
    return 'Container name may not contain consecutive hyphens.'
  return null
}
