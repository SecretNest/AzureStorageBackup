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
      .catch((e) => setError(String(e)))
      .finally(() => setLoading(false))
  }

  return (
    <section>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h1>Backups</h1>
        <button type="button" onClick={refresh} disabled={loading}>
          {loading ? 'Refreshing…' : 'Refresh'}
        </button>
      </div>
      <p style={{ color: '#666' }}>
        Discovered from your accounts&apos; containers. Not auto-refreshed.
      </p>

      {error && <p style={{ color: 'crimson' }}>{error}</p>}

      {!loaded ? (
        <p>Click Refresh to discover backups.</p>
      ) : backups.length === 0 ? (
        <p>No backups found.</p>
      ) : (
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr style={{ textAlign: 'left', borderBottom: '1px solid #ccc' }}>
              <th>Account</th>
              <th>Container</th>
              <th>Type</th>
            </tr>
          </thead>
          <tbody>
            {backups.map((b) => (
              <tr key={backupKey(b)} style={{ borderBottom: '1px solid #eee' }}>
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
