import { api } from './client'

// 与后端 BackupJob 契约对应的类型。随需求扩展。
export type BackupJobStatus = 'Pending' | 'Running' | 'Succeeded' | 'Failed'

export interface BackupJob {
  id: number
  name: string
  sourcePath: string
  containerName: string
  status: BackupJobStatus
  createdAt: string
  completedAt: string | null
}

export interface CreateBackupJobRequest {
  name: string
  sourcePath: string
  containerName: string
}

export const backupsApi = {
  list: () => api.get<BackupJob[]>('/backups'),
  get: (id: number) => api.get<BackupJob>(`/backups/${id}`),
  create: (req: CreateBackupJobRequest) => api.post<BackupJob>('/backups', req),
}

export interface HealthStatus {
  status: string
}

export const healthApi = {
  check: () => api.get<HealthStatus>('/health'),
}
