import { useState } from 'react'
import { backupsApi, backupPresenceLabels, backupKey, type DiscoveredBackup } from '../api/backups'

// 备份列表：从各账户 container 发现。手动刷新，不自动刷新（PRD 2.1）。
export function BackupsPage() {
  const [backups, setBackups] = useState<DiscoveredBackup[]>([])
  const [loaded, setLoaded] = useState(false)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const refresh = () => {
    setLoading(true)
    setError(null)
    backupsApi
      .list()
      .then((b) => {
        setBackups(b)
        setLoaded(true)
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setLoading(false))
  }

  return (
    <section>
      <div className="page-header">
        <h1>Backups</h1>
        <button type="button" onClick={refresh} disabled={loading}>
          {loading ? 'Refreshing…' : 'Refresh'}
        </button>
      </div>
      <p className="text-muted">
        Discovered from your accounts&apos; containers. Not auto-refreshed.
      </p>

      {error && <p className="text-danger">{error}</p>}

      {!loaded ? (
        <p className="empty-state">Click Refresh to discover backups.</p>
      ) : backups.length === 0 ? (
        <p className="empty-state">No backups found.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Account</th>
              <th>Container</th>
              <th>Type</th>
            </tr>
          </thead>
          <tbody>
            {backups.map((b) => (
              <tr key={backupKey(b)}>
                <td>{b.accountName}</td>
                <td>{b.containerName}</td>
                <td>{backupPresenceLabels[b.presence] ?? 'Unknown'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}
