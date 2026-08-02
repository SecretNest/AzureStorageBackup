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
  /** 属性读不出来因而未列出的子项数（例如目录 mode 为 r--：可 readdir、不可 stat 子项）。 */
  skipped: number
  /** 该目录的子项总数，不受分页影响。 */
  total: number
  /** 本页起始位置。 */
  offset: number
  entries: BrowseEntry[]
}

export const browseApi = {
  // signal：调用方（PathBrowser）在目录快速切换时用它取消上一次尚未完成的请求，
  // 避免慢的旧响应后到达反而覆盖了新目录的数据。
  // offset/limit：ScopeTree 用来分页拉大目录；都不传时是老行为（一次性，超量则 truncated）。
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
