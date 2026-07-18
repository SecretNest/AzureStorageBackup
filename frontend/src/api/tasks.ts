import { api } from './client'

export const TaskTargetKind = { Backup: 0, Group: 1 } as const
export const ScheduledTaskType = { Backup: 0, Check: 1, Cleanup: 2 } as const

export const taskTypeLabels: Record<number, string> = { 0: 'Backup', 1: 'Check', 2: 'Cleanup' }
export const targetKindLabels: Record<number, string> = { 0: 'Backup', 1: 'Group' }

// CloudCheckLevel/LocalCheckLevel 与 api/backupConfigs.ts 共用同一份定义，
// 见 constants/labels.ts（§5.7 合并重复 label 字典）。
export { CloudCheckLevel, LocalCheckLevel } from '../constants/labels'
export const cloudCheckLabels: Record<number, string> = {
  0: "Don't check cloud", 1: 'Metadata', 2: 'Existence + size', 3: 'Content (download)',
}
export const localCheckLabels: Record<number, string> = {
  0: "Don't check local", 1: 'Attributes', 2: 'Content hash',
}

export interface ScheduledTask {
  id: number
  targetKind: number
  accountId: number | null
  containerName: string | null
  groupId: number | null
  taskType: number
  cronExpression: string
  enabled: boolean
  createdAt: string
  lastRunAt: string | null
  checkCloudLevel: number
  checkLocalLevel: number
  checkRehydrateTier: number | null
}

export interface TaskInput {
  targetKind: number
  accountId: number | null
  containerName: string | null
  groupId: number | null
  taskType: number
  cronExpression: string
  enabled: boolean
  checkCloudLevel?: number
  checkLocalLevel?: number
  checkRehydrateTier?: number | null
}

export const tasksApi = {
  list: () => api.get<ScheduledTask[]>('/tasks'),
  create: (t: TaskInput) => api.post<ScheduledTask>('/tasks', t),
  update: (id: number, t: TaskInput) => api.put<ScheduledTask>(`/tasks/${id}`, t),
  remove: (id: number) => api.del(`/tasks/${id}`),
  run: (id: number) => api.post<ScheduledTask>(`/tasks/${id}/run`, {}),
}
