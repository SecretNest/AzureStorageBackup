import { describe, expect, it } from 'vitest'
import { backupNamesOf, nameBackup } from './backupNames'

describe('nameBackup', () => {
  const names = backupNamesOf([
    { accountId: 1, containerName: 'photos', name: 'Family photos' },
    { accountId: 2, containerName: 'photos', name: 'Work photos' },
  ])

  it('calls a backup by its configuration name, keyed by account and container together', () => {
    expect(nameBackup(names, { accountId: 1, containerName: 'photos' }, 'x')).toBe('Family photos')
    expect(nameBackup(names, { accountId: 2, containerName: 'photos' }, 'x')).toBe('Work photos')
  })

  it('falls back for a backup with no configuration', () => {
    expect(nameBackup(names, { accountId: 3, containerName: 'photos' }, 'acct / photos')).toBe('acct / photos')
  })
})
