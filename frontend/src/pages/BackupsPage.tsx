import { useEffect, useState } from 'react'
import { backupsApi, healthApi, type BackupJob } from '../api/backups'

// 备份任务主页。骨架阶段仅展示后端连通状态与任务列表，
// 具体的创建表单 / 执行 / 进度等随需求补充。
export function BackupsPage() {
  const [health, setHealth] = useState<string>('Checking…')
  const [jobs, setJobs] = useState<BackupJob[]>([])
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    healthApi
      .check()
      .then((h) => setHealth(h.status))
      .catch(() => setHealth('Unavailable'))

    backupsApi
      .list()
      .then(setJobs)
      .catch((e) => setError(String(e)))
  }, [])

  return (
    <section>
      <h1>Azure Storage Backup</h1>
      <p>
        Backend status: <strong>{health}</strong>
      </p>

      <h2>Backup Jobs</h2>
      {error && <p style={{ color: 'crimson' }}>Failed to load: {error}</p>}
      {jobs.length === 0 ? (
        <p>No jobs yet.</p>
      ) : (
        <ul>
          {jobs.map((j) => (
            <li key={j.id}>
              {j.name} — {j.status}（{j.sourcePath} → {j.containerName}）
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
