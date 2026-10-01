import { backupKey } from '../api/backups'

/**
 * Backup configuration names by backup key (see backupKey), for calling a backup what the Backups page
 * calls it. The pickers on the Schedules page are fed by the cloud inventory, which knows a backup only as
 * account / container; the name lives on the local configuration, so the two are joined here. A backup
 * with no configuration (a container discovered in the cloud and never set up locally) has no name and
 * falls back to whatever the caller shows otherwise — it could not run from a schedule anyway.
 */
export type BackupNames = ReadonlyMap<string, string>

export function backupNamesOf(
  configs: readonly { accountId: number; containerName: string; name: string }[],
): BackupNames {
  return new Map(configs.map((c) => [backupKey(c), c.name]))
}

export function nameBackup(
  names: BackupNames,
  b: { accountId: number; containerName: string },
  fallback: string,
): string {
  return names.get(backupKey(b)) ?? fallback
}
