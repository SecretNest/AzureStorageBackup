import { api } from './client'

export const OperationLogLevel = { Info: 0, Warning: 1, Error: 2 } as const
export const levelLabels: Record<number, string> = { 0: 'Info', 1: 'Warning', 2: 'Error' }

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
}
