import { api } from './client'

export const OperationLogLevel = { Debug: 0, Info: 1, Warning: 2, Error: 3 } as const
export const levelLabels: Record<number, string> = { 0: 'Debug', 1: 'Info', 2: 'Warning', 3: 'Error' }

export interface LogEntry {
  id: number
  timestamp: string
  level: number
  source: string
  message: string
}

export interface LogQuery {
  minLevel?: number
  source?: string
  from?: string
  to?: string
  limit?: number
}

export const logsApi = {
  query: (q: LogQuery = {}) => {
    const p = new URLSearchParams()
    if (q.minLevel !== undefined) p.set('minLevel', String(q.minLevel))
    if (q.source) p.set('source', q.source)
    if (q.from) p.set('from', q.from)
    if (q.to) p.set('to', q.to)
    if (q.limit) p.set('limit', String(q.limit))
    const qs = p.toString()
    return api.get<LogEntry[]>(`/logs${qs ? `?${qs}` : ''}`)
  },
  clear: () => api.del('/logs'),
  // 删除早于 cutoff（ISO）的全部日志（含长存审计）。
  purgeBefore: (cutoffIso: string) => api.del(`/logs?before=${encodeURIComponent(cutoffIso)}`),
}
