import { api } from './client'

export interface KeyringStatus {
  status: 'Healthy' | 'Lost'
  accountsPending: number
  backupConfigsPending: number
}

export const keyringApi = {
  status: () => api.get<KeyringStatus>('/system/keyring'),
}
