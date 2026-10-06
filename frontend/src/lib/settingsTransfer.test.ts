import { describe, expect, test } from 'vitest'

import type { ImportPlan, SettingsAccountEntry, SettingsDocument } from '../api/settings'
import { exportFileName, importSummary, keyPrompts, sectionLabel, withKeys } from './settingsTransfer'

const entry = (name: string, blobEndpoint: string, extra: Partial<SettingsAccountEntry> = {}): SettingsAccountEntry => ({
  name,
  description: null,
  blobEndpoint,
  region: 0,
  accountKey: null,
  useProxy: false,
  proxyMode: 0,
  proxyHost: null,
  proxyPort: null,
  proxyUsername: null,
  proxyPassword: null,
  ...extra,
})

const doc: SettingsDocument = {
  format: 'azure-storage-backup-settings',
  version: 1,
  accounts: [
    entry('kept', 'https://kept.blob.core.windows.net'),
    entry('new-plain', 'https://np.blob.core.windows.net'),
    entry('new-proxied', 'https://npx.blob.core.windows.net', { useProxy: true, proxyUsername: 'pu' }),
    entry('new-with-key', 'https://nk.blob.core.windows.net', { accountKey: 'k' }),
  ],
}

const plan: ImportPlan = {
  accounts: [
    { name: 'kept', blobEndpoint: 'https://kept.blob.core.windows.net', action: 'update', needsAccountKey: false },
    { name: 'new-plain', blobEndpoint: 'https://np.blob.core.windows.net', action: 'create', needsAccountKey: true },
    { name: 'new-proxied', blobEndpoint: 'https://npx.blob.core.windows.net', action: 'create', needsAccountKey: true },
    { name: 'new-with-key', blobEndpoint: 'https://nk.blob.core.windows.net', action: 'create', needsAccountKey: false },
  ],
  backupDefaults: true,
  performance: false,
  notifications: true,
}

describe('exportFileName', () => {
  test('is asb-settings-YYYYMMDD-HHMM.json in local time', () => {
    expect(exportFileName(new Date(2026, 9, 6, 9, 5))).toBe('asb-settings-20261006-0905.json')
  })
})

describe('keyPrompts', () => {
  test('lists only the creates that need a key, asking for a proxy password only where a proxy user is set', () => {
    expect(keyPrompts(doc, plan)).toEqual([
      { blobEndpoint: 'https://np.blob.core.windows.net', name: 'new-plain', askProxyPassword: false },
      { blobEndpoint: 'https://npx.blob.core.windows.net', name: 'new-proxied', askProxyPassword: true },
    ])
  })
})

describe('withKeys', () => {
  test('writes the typed keys into the matching entries and leaves every other entry untouched', () => {
    const out = withKeys(doc, {
      'https://np.blob.core.windows.net': { accountKey: 'np-key', proxyPassword: '' },
      'https://npx.blob.core.windows.net': { accountKey: 'npx-key', proxyPassword: 'pp' },
    })
    const by = Object.fromEntries(out.accounts!.map((a) => [a.name, a]))
    expect(by['new-plain']).toMatchObject({ accountKey: 'np-key', proxyPassword: null })
    expect(by['new-proxied']).toMatchObject({ accountKey: 'npx-key', proxyPassword: 'pp' })
    expect(by['kept']).toMatchObject({ accountKey: null })
    expect(by['new-with-key']).toMatchObject({ accountKey: 'k' })
    // The input is not mutated.
    expect(doc.accounts![1].accountKey).toBeNull()
  })
})

describe('importSummary', () => {
  test('counts creates and updates and names the sections applied', () => {
    expect(importSummary(plan)).toBe(
      '3 accounts created, 1 updated. Applied: backup defaults, notifications. Not in file: performance.',
    )
  })

  test('reads naturally with nothing to do', () => {
    expect(importSummary({ accounts: [], backupDefaults: false, performance: false, notifications: false })).toBe(
      'No accounts in file. Nothing applied; the file carried no settings sections.',
    )
  })

  test('singular forms', () => {
    expect(importSummary({
      accounts: [{ name: 'a', blobEndpoint: 'e', action: 'create', needsAccountKey: false }],
      backupDefaults: true, performance: true, notifications: true,
    })).toBe('1 account created, 0 updated. Applied: backup defaults, performance, notifications.')
  })
})

describe('sectionLabel', () => {
  test('tells overwrite from absent', () => {
    expect(sectionLabel(true)).toBe('will be overwritten')
    expect(sectionLabel(false)).toBe('not in file')
  })
})
