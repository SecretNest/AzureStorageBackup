import type { ImportPlan, SettingsAccountEntry, SettingsDocument } from '../api/settings'

/** asb-settings-YYYYMMDD-HHMM.json, local time — the moment the user clicked, as they would read it. */
export function exportFileName(now: Date): string {
  const p = (n: number, w = 2) => String(n).padStart(w, '0')
  return `asb-settings-${now.getFullYear()}${p(now.getMonth() + 1)}${p(now.getDate())}-${p(now.getHours())}${p(now.getMinutes())}.json`
}

/** One account the user has to type a key for before the import can run. */
export interface KeyPrompt {
  blobEndpoint: string
  name: string
  /** The entry uses a proxy with a username, so a proxy password may be wanted too (optional). */
  askProxyPassword: boolean
}

export function keyPrompts(doc: SettingsDocument, plan: ImportPlan): KeyPrompt[] {
  const entries = new Map((doc.accounts ?? []).map((a) => [a.blobEndpoint, a]))
  return plan.accounts
    .filter((a) => a.needsAccountKey)
    .map((a) => {
      const e = entries.get(a.blobEndpoint)
      return { blobEndpoint: a.blobEndpoint, name: a.name, askProxyPassword: !!(e?.useProxy && e.proxyUsername) }
    })
}

export type EnteredKeys = Record<string, { accountKey: string; proxyPassword: string }>

/** A copy of the document with the typed keys written into the matching entries. Empty strings become null. */
export function withKeys(doc: SettingsDocument, keys: EnteredKeys): SettingsDocument {
  const accounts = (doc.accounts ?? []).map((a): SettingsAccountEntry => {
    const k = keys[a.blobEndpoint]
    if (!k) return a
    return {
      ...a,
      accountKey: k.accountKey || a.accountKey,
      proxyPassword: k.proxyPassword || a.proxyPassword || null,
    }
  })
  return { ...doc, accounts }
}

export function sectionLabel(present: boolean): string {
  return present ? 'will be overwritten' : 'not in file'
}

const sectionNames: { key: keyof Omit<ImportPlan, 'accounts'>; label: string }[] = [
  { key: 'backupDefaults', label: 'backup defaults' },
  { key: 'performance', label: 'performance' },
  { key: 'notifications', label: 'notifications' },
]

/** The one-line result shown after an import. */
export function importSummary(plan: ImportPlan): string {
  const created = plan.accounts.filter((a) => a.action === 'create').length
  const updated = plan.accounts.length - created
  const accounts =
    plan.accounts.length === 0
      ? 'No accounts in file.'
      : `${created} ${created === 1 ? 'account' : 'accounts'} created, ${updated} updated.`

  const applied = sectionNames.filter((s) => plan[s.key]).map((s) => s.label)
  const absent = sectionNames.filter((s) => !plan[s.key]).map((s) => s.label)
  const sections =
    applied.length === 0
      ? 'Nothing applied; the file carried no settings sections.'
      : `Applied: ${applied.join(', ')}.${absent.length ? ` Not in file: ${absent.join(', ')}.` : ''}`

  return `${accounts} ${sections}`
}
