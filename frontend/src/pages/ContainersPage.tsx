import { useCallback, useEffect, useRef, useState } from 'react'
import { latestWins } from '../lib/latestWins'
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

  // latestWins: load() fires from mount, Refresh, and the post-create/post-delete refreshes, with nothing
  // stopping two from being in flight at once — and the OLDER response can resolve last, reverting the list
  // to a snapshot from before the create/delete (the new container "vanishes" until the next refresh).
  const loadGate = useRef(latestWins())
  const load = useCallback(() => {
    const isLatest = loadGate.current.begin()
    setLoading(true)
    setError(null)
    containersApi
      .list(account.id)
      .then((r) => {
        if (isLatest()) setContainers(r)
      })
      .catch((e) => {
        if (isLatest()) setError(e instanceof Error ? e.message : String(e))
      })
      .finally(() => setLoading(false))
  }, [account.id])

  useEffect(load, [load])

  const trimmedName = newName.trim()
  const nameError = trimmedName ? validateContainerName(trimmedName) : null

  // In-flight guard (same pattern as GroupsSection's `saving`): a double-click fires two concurrent
  // creates of the same name, and the loser paints a spurious error banner over a successful creation.
  const [creating, setCreating] = useState(false)
  const create = async () => {
    if (!trimmedName || nameError) return
    setCreating(true)
    try {
      await containersApi.create(account.id, trimmedName)
      setNewName('')
      load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setCreating(false)
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
          disabled={!trimmedName || !!nameError || creating}
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
