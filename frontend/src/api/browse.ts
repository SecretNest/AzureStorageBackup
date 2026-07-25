import { api } from './client'

export interface BrowseEntry {
  name: string
  fullPath: string
  isDirectory: boolean
  length: number | null
  modifiedAt: string
  outsideRoot: boolean
}

export interface BrowseResult {
  path: string
  parent: string | null
  truncated: boolean
  entries: BrowseEntry[]
}

export const browseApi = {
  list: (path?: string) =>
    api.get<BrowseResult>(`/system/browse${path ? `?path=${encodeURIComponent(path)}` : ''}`),
}
