import { useEffect, useState } from 'react'
import {
  tasksApi,
  TaskTargetKind,
  ScheduledTaskType,
  CloudCheckLevel,
  LocalCheckLevel,
  cloudCheckLabels,
  localCheckLabels,
  taskTypeLabels,
  targetKindLabels,
  type ScheduledTask,
  type TaskInput,
} from '../api/tasks'
import { groupsApi, type Group } from '../api/groups'
import { backupsApi, backupKey, type DiscoveredBackup } from '../api/backups'
import { CronEditor } from '../components/CronEditor'
import { GroupsSection } from './GroupsPage'
import { Field } from '../components/Field'

const emptyForm: TaskInput = {
  targetKind: TaskTargetKind.Backup,
  accountId: null,
  containerName: null,
  groupId: null,
  taskType: ScheduledTaskType.Backup,
  cronExpression: '0 2 * * *',
  enabled: true,
  checkCloudLevel: CloudCheckLevel.ExistenceSize,
  checkLocalLevel: LocalCheckLevel.Content,
  checkRehydrateTier: null,
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

  const loadTasks = () => tasksApi.list().then(setTasks).catch((e) => setError(e instanceof Error ? e.message : String(e)))
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
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))

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
      checkCloudLevel: t.checkCloudLevel,
      checkLocalLevel: t.checkLocalLevel,
      checkRehydrateTier: t.checkRehydrateTier,
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
      setError(e instanceof Error ? e.message : String(e))
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
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setRunning(null)
    }
  }

  const remove = async (t: ScheduledTask) => {
    if (!window.confirm('Delete this schedule?')) return
    try {
      await tasksApi.remove(t.id)
      loadTasks()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
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
      <div className="page-header">
        <h1>Schedules</h1>
        <button type="button" className="btn-primary" onClick={startNew}>
          New Schedule
        </button>
      </div>

      {error && <p className="text-danger">{error}</p>}

      <table className="cards">
        <thead>
          <tr>
            <th>Target</th>
            <th>Action</th>
            <th>Schedule</th>
            <th>Enabled</th>
            <th>Last run</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          {tasks.length === 0 ? (
            <tr>
              <td colSpan={6} className="empty-state">
                No schedules yet.
              </td>
            </tr>
          ) : (
            tasks.map((t) => (
              <tr key={t.id}>
                <td className="card-title">
                  {targetKindLabels[t.targetKind]}: {describeTarget(t)}
                </td>
                <td data-label="Action">{taskTypeLabels[t.taskType]}</td>
                <td data-label="Schedule">
                  <code>{t.cronExpression}</code>
                </td>
                <td data-label="Enabled">{t.enabled ? 'Yes' : 'No'}</td>
                <td data-label="Last run" className="text-faint">
                  {t.lastRunAt ? new Date(t.lastRunAt).toLocaleString() : 'never'}
                </td>
                <td className="card-actions" style={{ textAlign: 'right' }}>
                  <button type="button" className="btn-ghost" onClick={() => runNow(t)} disabled={running === t.id}>
                    {running === t.id ? 'Running…' : 'Run now'}
                  </button>{' '}
                  <button type="button" className="btn-ghost" onClick={() => startEdit(t)}>
                    Edit
                  </button>{' '}
                  <button type="button" className="btn-ghost btn-danger" onClick={() => remove(t)}>
                    Delete
                  </button>
                </td>
              </tr>
            ))
          )}
        </tbody>
      </table>

      {showForm && (
        <div className="panel">
          <h2>{editing ? 'Edit Schedule' : 'New Schedule'}</h2>

          <Field label="Target">
            <select
              value={form.targetKind}
              onChange={(e) => set('targetKind', Number(e.target.value))}
            >
              <option value={TaskTargetKind.Backup}>Backup</option>
              <option value={TaskTargetKind.Group}>Group</option>
            </select>
          </Field>

          {form.targetKind === TaskTargetKind.Backup ? (
            <Field label="Backup">
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
            </Field>
          ) : (
            <Field label="Group">
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
            </Field>
          )}

          <Field label="Scheduled action">
            <select value={form.taskType} onChange={(e) => set('taskType', Number(e.target.value))}>
              <option value={ScheduledTaskType.Backup}>Backup</option>
              <option value={ScheduledTaskType.Check}>Check</option>
              <option value={ScheduledTaskType.Cleanup}>Cleanup</option>
            </select>
          </Field>

          {form.taskType === ScheduledTaskType.Check && (
            <div className="stack" style={{ paddingLeft: '1rem', borderLeft: '2px solid var(--border)' }}>
              <label style={{ display: 'block' }}>
                Cloud check{' '}
                <select value={form.checkCloudLevel ?? CloudCheckLevel.ExistenceSize} onChange={(e) => set('checkCloudLevel', Number(e.target.value))}>
                  {Object.entries(cloudCheckLabels).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
                </select>
              </label>
              <label style={{ display: 'block' }}>
                Local check{' '}
                <select value={form.checkLocalLevel ?? LocalCheckLevel.Content} onChange={(e) => set('checkLocalLevel', Number(e.target.value))}>
                  {Object.entries(localCheckLabels).map(([v, l]) => <option key={v} value={v}>{l}</option>)}
                </select>
              </label>
            </div>
          )}

          <div style={{ margin: '0.5rem 0' }}>
            <div>Schedule</div>
            <CronEditor value={form.cronExpression} onChange={(c) => set('cronExpression', c)} />
          </div>

          <Field label="Enabled">
            <input
              type="checkbox"
              checked={form.enabled}
              onChange={(e) => set('enabled', e.target.checked)}
            />
          </Field>

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
      <GroupsSection onChanged={() => groupsApi.list().then(setGroups).catch(() => {})} />
    </section>
  )
}
