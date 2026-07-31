import { useEffect, useState } from 'react'
import { groupsApi, type Group, type GroupMember } from '../api/groups'
import { backupsApi, backupKey, type DiscoveredBackup } from '../api/backups'
import { Field } from '../components/modal'

export function GroupsSection({ onChanged }: { onChanged?: () => void } = {}) {
  const [groups, setGroups] = useState<Group[]>([])
  const [pool, setPool] = useState<DiscoveredBackup[]>([])
  const [poolLoaded, setPoolLoaded] = useState(false)
  const [editing, setEditing] = useState<Group | null>(null)
  const [showForm, setShowForm] = useState(false)
  const [name, setName] = useState('')
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [error, setError] = useState<string | null>(null)

  const loadGroups = () =>
    groupsApi
      .list()
      .then(setGroups)
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
  useEffect(() => {
    loadGroups()
  }, [])

  const loadPool = () =>
    backupsApi
      .list()
      .then((b) => {
        setPool(b)
        setPoolLoaded(true)
      })
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))

  const startNew = () => {
    setEditing(null)
    setName('')
    setSelected(new Set())
    setError(null)
    setShowForm(true)
    if (!poolLoaded) loadPool()
  }

  const startEdit = (g: Group) => {
    setEditing(g)
    setName(g.name)
    setSelected(new Set(g.members.map(backupKey)))
    setError(null)
    setShowForm(true)
    if (!poolLoaded) loadPool()
  }

  const toggle = (key: string) =>
    setSelected((s) => {
      const n = new Set(s)
      if (n.has(key)) n.delete(key)
      else n.add(key)
      return n
    })

  const memberFromKey = (key: string): GroupMember => {
    const slash = key.indexOf('/')
    return { accountId: Number(key.slice(0, slash)), containerName: key.slice(slash + 1) }
  }

  const save = async () => {
    const members = [...selected].map(memberFromKey)
    if (members.length === 0) {
      setError('Select at least one backup.')
      return
    }
    try {
      const input = { name, members }
      if (editing) await groupsApi.update(editing.id, input)
      else await groupsApi.create(input)
      setShowForm(false)
      loadGroups()
      onChanged?.()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const remove = async (g: Group) => {
    if (!window.confirm(`Delete group "${g.name}"?`)) return
    try {
      await groupsApi.remove(g.id)
      loadGroups()
      onChanged?.()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const poolKeys = new Set(pool.map(backupKey))
  const extraKeys = [...selected].filter((k) => !poolKeys.has(k))

  // 组只被计划任务使用，所以作为 Tasks 页里的一个分区，而不是自成一个顶级标签。
  return (
    <>
      <div className="page-header" style={{ marginTop: '2rem' }}>
        <h2>Groups</h2>
        <button type="button" onClick={startNew}>
          New Group
        </button>
      </div>

      {error && <p className="text-danger">{error}</p>}

      <div className="table-scroll" tabIndex={0}>
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>Backups</th>
              <th></th>
            </tr>
          </thead>
          <tbody>
            {groups.length === 0 ? (
              <tr>
                <td colSpan={3} className="empty-state">
                  No groups yet.
                </td>
              </tr>
            ) : (
              groups.map((g) => (
                <tr key={g.id}>
                  <td>{g.name}</td>
                  <td>{g.members.length}</td>
                  <td style={{ textAlign: 'right' }}>
                    <button type="button" className="btn-ghost" onClick={() => startEdit(g)}>
                      Edit
                    </button>{' '}
                    <button type="button" className="btn-ghost btn-danger" onClick={() => remove(g)}>
                      Delete
                    </button>
                  </td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      {showForm && (
        <div className="panel">
          <h2>{editing ? `Edit: ${editing.name}` : 'New Group'}</h2>
          <Field label="Name">
            <input className="w-md" value={name} onChange={(e) => setName(e.target.value)} />
          </Field>

          <div style={{ margin: '0.5rem 0' }}>
            <strong>Backups</strong>{' '}
            <button type="button" onClick={loadPool}>
              {poolLoaded ? 'Reload' : 'Load backups'}
            </button>
          </div>

          {!poolLoaded && pool.length === 0 ? (
            <p className="text-muted">Load backups to pick members.</p>
          ) : (
            <div
              className="stack"
              style={{ maxHeight: 200, overflow: 'auto', border: '1px solid var(--border)', padding: 'var(--sp-2)' }}
            >
              {pool.map((b) => {
                const key = backupKey(b)
                return (
                  <label key={key} style={{ display: 'block' }}>
                    <input type="checkbox" checked={selected.has(key)} onChange={() => toggle(key)} />{' '}
                    {b.accountName} / {b.containerName}
                  </label>
                )
              })}
              {extraKeys.map((key) => (
                <label key={key} className="text-warn" style={{ display: 'block' }}>
                  <input type="checkbox" checked onChange={() => toggle(key)} /> {key} (not in current list)
                </label>
              ))}
            </div>
          )}

          <div className="row" style={{ marginTop: '1rem' }}>
            <button type="button" className="btn-primary" onClick={save}>
              {editing ? 'Save' : 'Create'}
            </button>
            <button type="button" onClick={() => setShowForm(false)}>
              Cancel
            </button>
          </div>
        </div>
      )}
    </>
  )
}
