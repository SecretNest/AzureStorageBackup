import { useEffect, useRef, useState } from 'react'
import { logsApi, levelLabels, OperationLogLevel, type LogEntry } from '../api/logs'
import { latestWins } from '../lib/latestWins'
import { EmptyRow } from '../components/EmptyRow'
import { formatLocalDateTime, formatUtcOffset } from '../constants/format'

export function LogsPage() {
  // The zone every time on this page is written in and read back as. Named on screen because the
  // backend stores UTC and the reader cannot otherwise tell which of the two they are looking at.
  const timeZone = formatUtcOffset(new Date())

  const [logs, setLogs] = useState<LogEntry[]>([])
  // Set once a query comes back, whatever it came back with. Without it an empty `logs` cannot say
  // "no entries" from "not asked yet", and the table announces "No log entries." while the request is
  // still in flight — which on this page is doubly wrong, because a filter change re-runs the query
  // and the flash then lands between two populated results. See EmptyRow.
  const [loaded, setLoaded] = useState(false)
  const [minLevel, setMinLevel] = useState<number | ''>('')
  const [source, setSource] = useState('')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [error, setError] = useState<string | null>(null)

  // latestWins, not a cancelled flag: every keystroke in Source and every filter change re-runs the
  // query, and a broad earlier query (no filter) can return AFTER the narrower one that superseded it —
  // without the gate the stale, larger result set overwrites the fresh one, and the table no longer
  // matches the filter controls it sits under. See the note in latestWins.ts.
  const loadGate = useRef(latestWins())
  const load = () => {
    const isLatest = loadGate.current.begin()
    logsApi
      .query({
        minLevel: minLevel === '' ? undefined : minLevel,
        source: source || undefined,
        from: from ? new Date(from).toISOString() : undefined,
        to: to ? new Date(to).toISOString() : undefined,
        limit: 300,
      })
      .then((r) => {
        if (isLatest()) setLogs(r)
      })
      .catch((e) => {
        if (isLatest()) setError(e instanceof Error ? e.message : String(e))
      })
      // finally, not then: a failed query must still end the "loading" state, or the table sits on
      // "Loading…" forever with the real reason in the error line above it.
      // Deliberately never reset to false on a re-query: a filter change then leaves the previous
      // result on screen until the new one lands, which is what should happen — blanking the table
      // between two populated results is the same flash this flag exists to remove.
      .finally(() => setLoaded(true))
  }
  useEffect(load, [minLevel, source, from, to])

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
    // The cutoff is spelled out in the same zone as the filter that set it, so nobody confirms a delete against a time they read as UTC.
    if (!window.confirm(`Delete ALL logs before ${formatLocalDateTime(to)} (${timeZone})?`)) return
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
          From ({timeZone}):{' '}
          <input type="datetime-local" value={from} onChange={(e) => setFrom(e.target.value)} />
        </label>
        <label>
          To ({timeZone}): <input type="datetime-local" value={to} onChange={(e) => setTo(e.target.value)} />
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
              <th>Time ({timeZone})</th>
              <th>Level</th>
              <th>Source</th>
              <th>Message</th>
            </tr>
          </thead>
          <tbody>
            {logs.length === 0 ? (
              <EmptyRow loaded={loaded} colSpan={4}>
                No log entries.
              </EmptyRow>
            ) : (
              logs.map((l) => (
                <tr key={l.id}>
                  {/* The stored UTC instant stays reachable on hover: it is what the backend logs and what a bug report has to quote. */}
                  <td className="text-faint" style={{ whiteSpace: 'nowrap' }} title={`${l.timestamp} (UTC)`}>
                    {formatLocalDateTime(l.timestamp)}
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
    </section>
  )
}
