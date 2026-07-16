import { useEffect, useState } from 'react'
import {
  tasksApi,
  TaskTargetKind,
  ScheduledTaskType,
  taskTypeLabels,
  targetKindLabels,
  type ScheduledTask,
  type TaskInput,
} from '../api/tasks'
import { groupsApi, type Group } from '../api/groups'
import { backupsApi, backupKey, type DiscoveredBackup } from '../api/backups'
import { CronEditor } from '../components/CronEditor'

const emptyForm: TaskInput = {
  targetKind: TaskTargetKind.Backup,
  accountId: null,
  containerName: null,
  groupId: null,
  taskType: ScheduledTaskType.Backup,
  cronExpression: '0 2 * * *',
  enabled: true,
}

export function TasksPage() {
  const [tasks, setTasks] = useState<ScheduledTask[]>([])
  const [groups, setGroups] = useState<Group[]>([])
  const [pool, setPool] = useState<DiscoveredBackup[]>([])
  const [poolLoaded, setPoolLoaded] = useState(false)
  const [editing, setEditing] = useState<ScheduledTask | null>(null)
  const [showForm, setShowForm] = useState(false)
  const [form, setForm] = useState<TaskInput>(emptyForm)
  const [error, setError] = useState<string | null>(null)

  const loadTasks = () => tasksApi.list().then(setTasks).catch((e) => setError(String(e)))
  useEffect(() => {
    loadTasks()
    groupsApi.list().then(setGroups).catch(() => {})
  }, [])

  const loadPool = () =>
    backupsApi
      .list()
      .then((b) => {
        setPool(b)
        setPoolLoaded(true)
      })
      .catch((e) => setError(String(e)))

  const set = <K extends keyof TaskInput>(k: K, v: TaskInput[K]) => setForm((f) => ({ ...f, [k]: v }))

  const startNew = () => {
    setEditing(null)
    setForm(emptyForm)
    setError(null)
    setShowForm(true)
    if (!poolLoaded) loadPool()
  }

  const startEdit = (t: ScheduledTask) => {
    setEditing(t)
    setForm({
      targetKind: t.targetKind,
      accountId: t.accountId,
      containerName: t.containerName,
      groupId: t.groupId,
      taskType: t.taskType,
      cronExpression: t.cronExpression,
      enabled: t.enabled,
    })
    setError(null)
    setShowForm(true)
    if (!poolLoaded) loadPool()
  }

  const save = async () => {
    try {
      if (editing) await tasksApi.update(editing.id, form)
      else await tasksApi.create(form)
      setShowForm(false)
      loadTasks()
    } catch (e) {
      setError(String(e))
    }
  }

  const [running, setRunning] = useState<number | null>(null)
  const runNow = async (t: ScheduledTask) => {
    setError(null)
    setRunning(t.id)
    try {
      await tasksApi.run(t.id)
      loadTasks()
    } catch (e) {
      setError(String(e))
    } finally {
      setRunning(null)
    }
  }

  const remove = async (t: ScheduledTask) => {
    if (!window.confirm('Delete this task?')) return
    try {
      await tasksApi.remove(t.id)
      loadTasks()
    } catch (e) {
      setError(String(e))
    }
  }

  const describeTarget = (t: ScheduledTask) =>
    t.targetKind === TaskTargetKind.Group
      ? `Group #${t.groupId}`
      : `${t.accountId} / ${t.containerName}`

  const pickBackup = (key: string) => {
    if (!key) {
      set('accountId', null)
      set('containerName', null)
      return
    }
    const slash = key.indexOf('/')
    setForm((f) => ({
      ...f,
      accountId: Number(key.slice(0, slash)),
      containerName: key.slice(slash + 1),
    }))
  }

  return (
    <section>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <h1>Scheduled Tasks</h1>
        <button type="button" onClick={startNew}>
          New Task
        </button>
      </div>

      {error && <p style={{ color: 'crimson' }}>{error}</p>}

      <table style={{ width: '100%', borderCollapse: 'collapse', marginTop: '1rem' }}>
        <thead>
          <tr style={{ textAlign: 'left', borderBottom: '1px solid #ccc' }}>
            <th>Target</th>
            <th>Type</th>
            <th>Schedule</th>
            <th>Enabled</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {tasks.length === 0 ? (
            <tr>
              <td colSpan={5} style={{ padding: '1rem 0', color: '#666' }}>
                No tasks yet.
              </td>
            </tr>
          ) : (
            tasks.map((t) => (
              <tr key={t.id} style={{ borderBottom: '1px solid #eee' }}>
                <td>
                  {targetKindLabels[t.targetKind]}: {describeTarget(t)}
                </td>
                <td>{taskTypeLabels[t.taskType]}</td>
                <td>
                  <code>{t.cronExpression}</code>
                </td>
                <td>{t.enabled ? 'Yes' : 'No'}</td>
                <td style={{ textAlign: 'right' }}>
                  <button type="button" onClick={() => runNow(t)} disabled={running === t.id}>
                    {running === t.id ? 'Running…' : 'Run now'}
                  </button>{' '}
                  <button type="button" onClick={() => startEdit(t)}>
                    Edit
                  </button>{' '}
                  <button type="button" onClick={() => remove(t)}>
                    Delete
                  </button>
                  <div style={{ fontSize: '0.75rem', color: '#888' }}>
                    Last run: {t.lastRunAt ? new Date(t.lastRunAt).toLocaleString() : 'never'}
                  </div>
                </td>
              </tr>
            ))
          )}
        </tbody>
      </table>

      {showForm && (
        <div style={{ marginTop: '1.5rem', padding: '1rem', border: '1px solid #ccc' }}>
          <h2>{editing ? 'Edit Task' : 'New Task'}</h2>

          <label style={{ display: 'block', margin: '0.5rem 0' }}>
            Target{' '}
            <select
              value={form.targetKind}
              onChange={(e) => set('targetKind', Number(e.target.value))}
            >
              <option value={TaskTargetKind.Backup}>Backup</option>
              <option value={TaskTargetKind.Group}>Group</option>
            </select>
          </label>

          {form.targetKind === TaskTargetKind.Backup ? (
            <label style={{ display: 'block', margin: '0.5rem 0' }}>
              Backup{' '}
              <select
                value={form.accountId !== null ? `${form.accountId}/${form.containerName}` : ''}
                onChange={(e) => pickBackup(e.target.value)}
              >
                <option value="">— select —</option>
                {pool.map((b) => (
                  <option key={backupKey(b)} value={backupKey(b)}>
                    {b.accountName} / {b.containerName}
                  </option>
                ))}
              </select>{' '}
              {!poolLoaded && (
                <button type="button" onClick={loadPool}>
                  Load backups
                </button>
              )}
            </label>
          ) : (
            <label style={{ display: 'block', margin: '0.5rem 0' }}>
              Group{' '}
              <select
                value={form.groupId ?? ''}
                onChange={(e) => set('groupId', e.target.value ? Number(e.target.value) : null)}
              >
                <option value="">— select —</option>
                {groups.map((g) => (
                  <option key={g.id} value={g.id}>
                    {g.name}
                  </option>
                ))}
              </select>
            </label>
          )}

          <label style={{ display: 'block', margin: '0.5rem 0' }}>
            Task type{' '}
            <select value={form.taskType} onChange={(e) => set('taskType', Number(e.target.value))}>
              <option value={ScheduledTaskType.Backup}>Backup</option>
              <option value={ScheduledTaskType.Check}>Check</option>
              <option value={ScheduledTaskType.Cleanup}>Cleanup</option>
            </select>
          </label>

          <div style={{ margin: '0.5rem 0' }}>
            <div>Schedule</div>
            <CronEditor value={form.cronExpression} onChange={(c) => set('cronExpression', c)} />
          </div>

          <label style={{ display: 'block', margin: '0.5rem 0' }}>
            <input
              type="checkbox"
              checked={form.enabled}
              onChange={(e) => set('enabled', e.target.checked)}
            />{' '}
            Enabled
          </label>

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
