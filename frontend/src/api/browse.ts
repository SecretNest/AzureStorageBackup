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
  entries: BrowseEntry[]
}

export const browseApi = {
  // signal：调用方（PathBrowser）在目录快速切换时用它取消上一次尚未完成的请求，
  // 避免慢的旧响应后到达反而覆盖了新目录的数据。
  list: (path?: string, signal?: AbortSignal) =>
    api.get<BrowseResult>(`/system/browse${path ? `?path=${encodeURIComponent(path)}` : ''}`, { signal }),
}
