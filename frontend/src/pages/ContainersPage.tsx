import { useCallback, useEffect, useState } from 'react'
import { containersApi, backupPresenceLabels, type ContainerInfo } from '../api/containers'
import type { Account } from '../api/accounts'

export function ContainersPage({ account, onBack }: { account: Account; onBack: () => void }) {
  const [containers, setContainers] = useState<ContainerInfo[]>([])
  const [newName, setNewName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  const load = useCallback(() => {
    setLoading(true)
    setError(null)
    containersApi
      .list(account.id)
      .then(setContainers)
      .catch((e) => setError(String(e)))
      .finally(() => setLoading(false))
  }, [account.id])

  useEffect(load, [load])

  const create = async () => {
    if (!newName.trim()) return
    try {
      await containersApi.create(account.id, newName.trim())
      setNewName('')
      load()
    } catch (e) {
      setError(String(e))
    }
  }

  const remove = async (name: string) => {
    if (!window.confirm(`Delete container "${name}"?`)) return
    try {
      await containersApi.remove(account.id, name)
      load()
    } catch (e) {
      setError(String(e))
    }
  }

  return (
    <section>
      <button type="button" onClick={onBack}>
        &larr; Back to accounts
      </button>

      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h1>Containers — {account.name}</h1>
        <button type="button" onClick={load} disabled={loading}>
          Refresh
        </button>
      </div>

      {error && <p style={{ color: 'crimson' }}>{error}</p>}

      <div style={{ margin: '1rem 0' }}>
        <input
          placeholder="New container name"
          value={newName}
          onChange={(e) => setNewName(e.target.value)}
        />{' '}
        <button type="button" onClick={create}>
          Create Container
        </button>
      </div>

      {loading ? (
        <p>Loading…</p>
      ) : containers.length === 0 ? (
        <p>No containers yet. We suggest creating one to start a backup.</p>
      ) : (
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr style={{ textAlign: 'left', borderBottom: '1px solid #ccc' }}>
              <th>Name</th>
              <th>Status</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {containers.map((c) => (
              <tr key={c.name} style={{ borderBottom: '1px solid #eee' }}>
                <td>{c.name}</td>
                <td>{backupPresenceLabels[c.backup] ?? 'Unknown'}</td>
                <td style={{ textAlign: 'right' }}>
                  <button type="button" onClick={() => remove(c.name)}>
                    Delete
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}
