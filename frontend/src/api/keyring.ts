import { useEffect, useState } from 'react'
import { api } from './client'

export interface KeyringStatus {
  status: 'Healthy' | 'Lost'
  accountsPending: number
  backupConfigsPending: number
}

export const keyringApi = {
  status: () => api.get<KeyringStatus>('/system/keyring'),
}

// One in-process copy of the status, plus subscriptions (design §3.5). The banner, the accounts
// page and the backups page must all read the same one: after any of them resets successfully,
// calling refreshKeyringStatus() updates every subscriber at once — otherwise the banner fetches
// once on mount and keeps showing "keys were lost" after recovery has finished, until the user
// hard-refreshes the page.
let cached: KeyringStatus | null = null
const listeners = new Set<(s: KeyringStatus | null) => void>()

export async function refreshKeyringStatus(): Promise<KeyringStatus | null> {
  try {
    cached = await keyringApi.status()
  } catch {
    cached = null
  }
  for (const listen of listeners) listen(cached)
  return cached
}

export function useKeyringStatus(): KeyringStatus | null {
  const [status, setStatus] = useState<KeyringStatus | null>(cached)

  useEffect(() => {
    listeners.add(setStatus)
    void refreshKeyringStatus()
    return () => {
      listeners.delete(setStatus)
    }
  }, [])

  return status
}
