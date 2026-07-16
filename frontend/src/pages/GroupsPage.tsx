import { useEffect, useState } from 'react'
import { groupsApi, type Group, type GroupMember } from '../api/groups'
import { backupsApi, backupKey, type DiscoveredBackup } from '../api/backups'

export function GroupsPage() {
  const [groups, setGroups] = useState<Group[]>([])
  const [pool, setPool] = useState<DiscoveredBackup[]>([])
  const [poolLoaded, setPoolLoaded] = useState(false)
  const [editing, setEditing] = useState<Group | null>(null)
  const [showForm, setShowForm] = useState(false)
  const [name, setName] = useState('')
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [error, setError] = useState<string | null>(null)

  const loadGroups = () => groupsApi.list().then(setGroups).catch((e) => setError(String(e)))
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
      .catch((e) => setError(String(e)))

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
    } catch (e) {
      setError(String(e))
    }
  }

  const remove = async (g: Group) => {
    if (!window.confirm(`Delete group "${g.name}"?`)) return
    try {
      await groupsApi.remove(g.id)
      loadGroups()
    } catch (e) {
      setError(String(e))
    }
  }

  const poolKeys = new Set(pool.map(backupKey))
  const extraKeys = [...selected].filter((k) => !poolKeys.has(k))

  return (
    <section>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h1>Groups</h1>
        <button type="button" onClick={startNew}>
          New Group
        </button>
      </div>

      {error && <p style={{ color: 'crimson' }}>{error}</p>}

      <table style={{ width: '100%', borderCollapse: 'collapse', marginTop: '1rem' }}>
        <thead>
          <tr style={{ textAlign: 'left', borderBottom: '1px solid #ccc' }}>
            <th>Name</th>
            <th>Backups</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {groups.length === 0 ? (
            <tr>
              <td colSpan={3} style={{ padding: '1rem 0', color: '#666' }}>
                No groups yet.
              </td>
            </tr>
          ) : (
            groups.map((g) => (
              <tr key={g.id} style={{ borderBottom: '1px solid #eee' }}>
                <td>{g.name}</td>
                <td>{g.members.length}</td>
                <td style={{ textAlign: 'right' }}>
                  <button type="button" onClick={() => startEdit(g)}>
                    Edit
                  </button>{' '}
                  <button type="button" onClick={() => remove(g)}>
                    Delete
                  </button>
                </td>
              </tr>
            ))
          )}
        </tbody>
      </table>

      {showForm && (
        <div style={{ marginTop: '1.5rem', padding: '1rem', border: '1px solid #ccc' }}>
          <h2>{editing ? `Edit: ${editing.name}` : 'New Group'}</h2>
          <label style={{ display: 'block', margin: '0.5rem 0' }}>
            Name <input value={name} onChange={(e) => setName(e.target.value)} />
          </label>

          <div style={{ margin: '0.5rem 0' }}>
            <strong>Backups</strong>{' '}
            <button type="button" onClick={loadPool}>
              {poolLoaded ? 'Reload' : 'Load backups'}
            </button>
          </div>

          {!poolLoaded && pool.length === 0 ? (
            <p style={{ color: '#666' }}>Load backups to pick members.</p>
          ) : (
            <div style={{ maxHeight: 200, overflow: 'auto', border: '1px solid #eee', padding: '0.5rem' }}>
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
                <label key={key} style={{ display: 'block', color: '#a60' }}>
                  <input type="checkbox" checked onChange={() => toggle(key)} /> {key} (not in current list)
                </label>
              ))}
            </div>
          )}

          <div style={{ marginTop: '1rem' }}>
            <button type="button" onClick={save}>
              {editing ? 'Save' : 'Create'}
            </button>{' '}
            <button type="button" onClick={() => setShowForm(false)}>
              Cancel
            </button>
          </div>
        </div>
      )}
    </section>
  )
}
