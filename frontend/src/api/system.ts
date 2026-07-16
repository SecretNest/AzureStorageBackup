import { api } from './client'

export const systemApi = {
  paths: () => api.get<Record<string, string>>('/system/paths'),
  version: () => api.get<{ version: string }>('/system/version'),
}
