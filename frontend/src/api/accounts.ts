import { api } from './client'

// Mirrors the backend enum (System.Text.Json serialises it as a number by default)
export const AzureRegion = { Global: 0, China: 1, UsGov: 2 } as const
export const ProxyMode = { Independent: 0, DockerEnv: 1 } as const

export const regionLabels: Record<number, string> = {
  0: 'Global',
  1: 'China',
  2: 'US Gov',
}

export interface Account {
  id: number
  name: string
  description: string | null
  blobEndpoint: string
  region: number
  useProxy: boolean
  proxyMode: number
  proxyHost: string | null
  proxyPort: number | null
  proxyUsername: string | null
  createdAt: string
  secretsUnavailable: boolean
  /** Names of the backups using this account, sorted. Non-empty means it cannot be deleted. */
  usedByBackups: string[]
}

export interface AccountInput {
  name: string
  description: string | null
  blobEndpoint: string
  region: number
  accountKey: string | null
  useProxy: boolean
  proxyMode: number
  proxyHost: string | null
  proxyPort: number | null
  proxyUsername: string | null
  proxyPassword: string | null
}

export interface ConnectionResult {
  success: boolean
  error: string | null
}

export const accountsApi = {
  list: () => api.get<Account[]>('/accounts'),
  get: (id: number) => api.get<Account>(`/accounts/${id}`),
  create: (input: AccountInput) => api.post<Account>('/accounts', input),
  update: (id: number, input: AccountInput) => api.put<Account>(`/accounts/${id}`, input),
  remove: (id: number) => api.del(`/accounts/${id}`),
  testConnection: (input: AccountInput) =>
    api.post<ConnectionResult>('/accounts/test-connection', input),
  /**
   * Connectivity test while editing: an empty Key means "use the one already stored", while every
   * other field comes from the form. It cannot reuse the one above — that returns 400 for an empty
   * Key, and an empty Key box is exactly the normal state while editing.
   */
  testConnectionFor: (id: number, input: AccountInput) =>
    api.post<ConnectionResult>(`/accounts/${id}/test-connection`, input),
  resetSecrets: (id: number, accountKey: string, proxyPassword: string | null) =>
    api.post<void>(`/accounts/${id}/reset-secrets`, { accountKey, proxyPassword }),
}
