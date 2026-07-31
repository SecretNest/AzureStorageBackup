import { useCallback, useEffect, useState } from 'react'
import {
  containersApi,
  containerStatusLabel,
  infoFileName,
  validateContainerName,
  containerNameRule,
  type ContainerInfo,
} from '../api/containers'
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
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setLoading(false))
  }, [account.id])

  useEffect(load, [load])

  const trimmedName = newName.trim()
  const nameError = trimmedName ? validateContainerName(trimmedName) : null

  const create = async () => {
    if (!trimmedName || nameError) return
    try {
      await containersApi.create(account.id, trimmedName)
      setNewName('')
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const remove = async (name: string) => {
    if (!window.confirm(`Delete container "${name}"?`)) return
    try {
      await containersApi.remove(account.id, name)
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  return (
    <section>
      <button type="button" className="btn-ghost" onClick={onBack}>
        &larr; Back to accounts
      </button>

      <div className="page-header">
        <h1>Containers — {account.name}</h1>
        <button type="button" onClick={load} disabled={loading}>
          Refresh
        </button>
      </div>

      {error && <p className="text-danger">{error}</p>}

      <div className="toolbar">
        <input
          className="w-md"
          placeholder="New container name"
          value={newName}
          onChange={(e) => setNewName(e.target.value)}
        />
        <button
          type="button"
          className="btn-primary"
          onClick={create}
          disabled={!trimmedName || !!nameError}
        >
          Create Container
        </button>
        <span className={nameError ? 'text-danger' : 'text-faint'}>
          {nameError ?? containerNameRule}
        </span>
      </div>

      {loading ? (
        <p>Loading…</p>
      ) : containers.length === 0 ? (
        <p className="empty-state">No containers yet. We suggest creating one to start a backup.</p>
      ) : (
        <div className="table-scroll" tabIndex={0}>
          <table>
            <thead>
              <tr>
                <th>Name</th>
                <th>Status</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {containers.map((c) => (
                <tr key={c.name}>
                  <td>{c.name}</td>
                  <td>
                    {infoFileName(c.backup) ? (
                      <span className="row-inline">
                        <span>{containerStatusLabel(c)}</span>
                        <span className="text-faint">({infoFileName(c.backup)})</span>
                      </span>
                    ) : (
                      containerStatusLabel(c)
                    )}
                  </td>
                  <td style={{ textAlign: 'right' }}>
                    <button type="button" className="btn-ghost btn-danger" onClick={() => remove(c.name)}>
                      Delete
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}
