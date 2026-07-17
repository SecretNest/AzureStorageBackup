import { useEffect, useState } from 'react'
import { logsApi, levelLabels, OperationLogLevel, type LogEntry } from '../api/logs'
import { systemApi } from '../api/system'

const levelColor: Record<number, string> = { 0: '#555', 1: '#b8860b', 2: 'crimson' }

export function LogsPage() {
  const [logs, setLogs] = useState<LogEntry[]>([])
  const [minLevel, setMinLevel] = useState<number | ''>('')
  const [source, setSource] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [paths, setPaths] = useState<Record<string, string>>({})
  const [version, setVersion] = useState('')

  const load = () => {
    logsApi
      .query({
        minLevel: minLevel === '' ? undefined : minLevel,
        source: source || undefined,
        from: from ? new Date(from).toISOString() : undefined,
        to: to ? new Date(to).toISOString() : undefined,
        limit: 300,
      })
      .then(setLogs)
      .catch((e) => setError(String(e)))
  }
  useEffect(load, [minLevel, source, from, to])

  useEffect(() => {
    systemApi.paths().then(setPaths).catch(() => {})
    systemApi.version().then((v) => setVersion(v.version)).catch(() => {})
  }, [])

  const clear = async () => {
    if (!window.confirm('Clear all logs?')) return
    try {
      await logsApi.clear()
      load()
    } catch (e) {
      setError(String(e))
    }
  }

  // 删除早于某时间的全部日志（含长存审计）。用 "To" 值作截止点。
  const purgeBefore = async () => {
    if (!to) {
      setError('Set the "To" time to purge everything before it.')
      return
    }
    if (!window.confirm(`Delete ALL logs before ${to}?`)) return
    try {
      await logsApi.purgeBefore(new Date(to).toISOString())
      load()
    } catch (e) {
      setError(String(e))
    }
  }

  return (
    <section>
      <h1>Logs</h1>
      {error && <p style={{ color: 'crimson' }}>{error}</p>}

      <div style={{ display: 'flex', gap: '1rem', alignItems: 'center', marginBottom: '0.8rem' }}>
        <label>
          Level:{' '}
          <select value={minLevel} onChange={(e) => setMinLevel(e.target.value === '' ? '' : Number(e.target.value))}>
            <option value="">All</option>
            <option value={OperationLogLevel.Debug}>Debug+</option>
            <option value={OperationLogLevel.Info}>Info+</option>
            <option value={OperationLogLevel.Warning}>Warning+</option>
            <option value={OperationLogLevel.Error}>Error</option>
          </select>
        </label>
        <label>
          Source:{' '}
          <input value={source} placeholder="e.g. backup:photos" onChange={(e) => setSource(e.target.value)} />
        </label>
        <label>
          From: <input type="datetime-local" value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label>
          To: <input type="datetime-local" value={to} onChange={(e) => setTo(e.target.value)} />
        </label>
        <button type="button" onClick={load}>
          Refresh
        </button>
        <button type="button" onClick={purgeBefore}>
          Delete before "To"
        </button>
        <button type="button" onClick={clear}>
          Clear all
        </button>
      </div>

      <table style={{ width: '100%', borderCollapse: 'collapse' }}>
        <thead>
          <tr style={{ textAlign: 'left', borderBottom: '1px solid #ccc' }}>
            <th>Time</th>
            <th>Level</th>
            <th>Source</th>
            <th>Message</th>
          </tr>
        </thead>
        <tbody>
          {logs.length === 0 ? (
            <tr>
              <td colSpan={4} style={{ padding: '1rem 0', color: '#666' }}>
                No log entries.
              </td>
            </tr>
          ) : (
            logs.map((l) => (
              <tr key={l.id} style={{ borderBottom: '1px solid #eee' }}>
                <td style={{ whiteSpace: 'nowrap', fontSize: '0.8rem' }}>
                  {new Date(l.timestamp).toLocaleString()}
                </td>
                <td style={{ color: levelColor[l.level], fontWeight: l.level === 2 ? 'bold' : 'normal' }}>
                  {levelLabels[l.level]}
                </td>
                <td style={{ fontFamily: 'monospace', fontSize: '0.8rem' }}>{l.source}</td>
                <td>{l.message}</td>
              </tr>
            ))
          )}
        </tbody>
      </table>

      <h2 style={{ marginTop: '2rem' }}>System</h2>
      <p style={{ color: '#666' }}>Version: {version || '…'}</p>
      <p style={{ color: '#666' }}>Temp directories (map these as docker volumes):</p>
      <ul style={{ fontFamily: 'monospace', fontSize: '0.85rem' }}>
        {Object.entries(paths).map(([k, v]) => (
          <li key={k}>
            {k}: {v}
          </li>
        ))}
      </ul>
    </section>
  )
}
