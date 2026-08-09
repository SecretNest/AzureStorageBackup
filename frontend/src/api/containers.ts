import { api } from './client'

// Mirrors the backend's BackupPresence
export const BackupPresence = { None: 0, Plain: 1, Encrypted: 2 } as const

export const backupPresenceLabels: Record<number, string> = {
  0: 'Empty',
  1: 'Backup',
  2: 'Backup (encrypted)',
}

// The conventional filename of the info file (backend BackupDiscovery, PRD 1.3). Display only.
export const infoFileName = (presence: number): string | null =>
  presence === BackupPresence.Plain
    ? 'azurestoragebackup.index.json'
    : presence === BackupPresence.Encrypted
      ? 'azurestoragebackup.index.json.enc'
      : null

export interface ContainerInfo {
  name: string
  backup: number
  /**
   * When a local backup configuration already holds this container, the name of that backup.
   *
   * `backup` can only say whether the cloud info file exists, and that file is written by the very
   * last step of a backup: a container holding a half-finished first run already has this run's data
   * in it while carrying no cloud marker at all. Occupancy is authoritative locally.
   */
  inUseBy?: string | null
}

/** The status cell for a container in the list. Occupancy outranks cloud presence — it is both earlier and more certain. */
export const containerStatusLabel = (c: ContainerInfo): string =>
  c.inUseBy
    ? `In use by "${c.inUseBy}"`
    : (backupPresenceLabels[c.backup] ?? 'Unknown')

export const containersApi = {
  list: (accountId: number) => api.get<ContainerInfo[]>(`/accounts/${accountId}/containers`),
  create: (accountId: number, name: string) =>
    api.post<{ name: string }>(`/accounts/${accountId}/containers`, { name }),
  remove: (accountId: number, name: string) =>
    api.del(`/accounts/${accountId}/containers/${encodeURIComponent(name)}`),
}

/**
 * Azure container naming rules. Equivalent to the backend's Services/ContainerName.cs — the backend
 * is authoritative, and this copy exists only to give feedback while typing rather than after a
 * network round trip. Change one and you must change the other.
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
