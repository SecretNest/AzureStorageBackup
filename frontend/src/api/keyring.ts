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

// 进程内的单一状态副本 + 订阅(设计 §3.5)。横幅、账户页、备份页读的必须是同一份:
// 任一处重设成功后调用 refreshKeyringStatus(),所有订阅者立即更新——否则横幅只在挂载时
// 拉一次,恢复完成后仍挂着"密钥已丢失"的警告,直到用户硬刷新页面才消失。
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
