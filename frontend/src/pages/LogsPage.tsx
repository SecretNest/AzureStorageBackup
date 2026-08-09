import { useEffect, useState } from 'react'
import { logsApi, levelLabels, OperationLogLevel, type LogEntry } from '../api/logs'
import { systemApi } from '../api/system'

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
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
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
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  // Delete every log older than a given time, durable audit entries included. The "To" value is the cutoff.
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
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  return (
    <section>
      <div className="page-header">
        <h1>Logs</h1>
      </div>
      {error && <p className="text-danger">{error}</p>}

      <div className="toolbar">
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
          <input
            className="w-md"
            value={source}
            placeholder="e.g. backup:photos"
            onChange={(e) => setSource(e.target.value)}
          />
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

      <div className="table-scroll" tabIndex={0}>
        <table>
          <thead>
            <tr>
              <th>Time</th>
              <th>Level</th>
              <th>Source</th>
              <th>Message</th>
            </tr>
          </thead>
          <tbody>
            {logs.length === 0 ? (
              <tr>
                <td colSpan={4} className="empty-state">
                  No log entries.
                </td>
              </tr>
            ) : (
              logs.map((l) => (
                <tr key={l.id}>
                  <td className="text-faint" style={{ whiteSpace: 'nowrap' }}>
                    {new Date(l.timestamp).toLocaleString()}
                  </td>
                  <td>
                    {/* Severity order must match OperationLogLevel: the previous mapping was off by one, which rendered Error as the same plain grey badge as Debug. */}
                    <span className={
                      l.level === OperationLogLevel.Error ? 'badge badge-danger'
                      : l.level === OperationLogLevel.Warning ? 'badge badge-warn'
                      : 'badge'
                    }>
                      {levelLabels[l.level]}
                    </span>
                  </td>
                  <td className="mono text-faint">{l.source}</td>
                  <td>{l.message}</td>
                </tr>
              ))
            )}
          </tbody>
        </table>
      </div>

      <h2>System</h2>
      <p className="text-muted">Version: {version || '…'}</p>
      <p className="text-muted">Temp directories (map these as docker volumes):</p>
      <ul className="mono text-faint">
        {Object.entries(paths).map(([k, v]) => (
          <li key={k}>
            {k}: {v}
          </li>
        ))}
      </ul>
    </section>
  )
}
