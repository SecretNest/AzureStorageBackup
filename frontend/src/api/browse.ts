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
  /** Children omitted because their attributes could not be read (e.g. a directory with mode r--: readdir works, stat on children does not). */
  skipped: number
  /** Total children in the directory, unaffected by paging. */
  total: number
  /** Where this page starts. */
  offset: number
  entries: BrowseEntry[]
}

export const browseApi = {
  // signal: the caller (PathBrowser) uses it to cancel a still-pending request when directories are
  // switched quickly, so a slow old response cannot arrive late and overwrite the new directory.
  // offset/limit: ScopeTree pages through large directories. Omitting both is the old behaviour
  // (everything at once, truncated past the cap).
  list: (path?: string, signal?: AbortSignal, page?: { offset: number; limit: number }) => {
    const params = new URLSearchParams()
    if (path) params.set('path', path)
    if (page) {
      params.set('offset', String(page.offset))
      params.set('limit', String(page.limit))
    }
    const qs = params.toString()
    return api.get<BrowseResult>(`/system/browse${qs ? `?${qs}` : ''}`, { signal })
  },
}
