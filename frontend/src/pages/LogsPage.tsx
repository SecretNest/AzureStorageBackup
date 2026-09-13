import { useEffect, useRef, useState } from 'react'
import { logsApi, levelLabels, OperationLogLevel, type LogEntry } from '../api/logs'
import { latestWins } from '../lib/latestWins'
import { gatedLoad } from '../lib/gatedLoad'
import {
  incompleteTimeNotice,
  initialLogFilters,
  noLogFilters,
  timeFieldHint,
  toForRefresh,
} from '../lib/logFilters'
import { EmptyRow } from '../components/EmptyRow'
import { formatLocalDateTime, formatUtcOffset } from '../constants/format'

/**
 * The line beside a time box: the value as the table writes it, or a word about why the box is not a
 * filter yet. See timeFieldHint — a `datetime-local` renders in the browser's locale, so the box can
 * say `07:30 PM` over a table that says `19:30`, and a half-filled one says nothing at all.
 */
function TimeEcho({ value, badInput }: { value: string; badInput: boolean }) {
  const hint = timeFieldHint(value, badInput)
  if (!hint)
    return null
  // text-faint carries the small size, text-warn the colour, and it is declared after it in the
  // stylesheet — so the complaint reads as the same aside as the echo, not as a second control.
  return <span className={hint.incomplete ? 'text-faint text-warn' : 'text-faint'}> {hint.text}</span>
}

export function LogsPage() {
  // The zone every time on this page is written in and read back as. Named on screen because the
  // backend stores UTC and the reader cannot otherwise tell which of the two they are looking at.
  const timeZone = formatUtcOffset(new Date())

  const [logs, setLogs] = useState<LogEntry[]>([])
  // Set once a query that still counts comes back, whatever it came back with. Without it an empty
  // `logs` cannot say "no entries" from "not asked yet", and the table announces "No log entries."
  // while the request is still in flight — which on this page is doubly wrong, because a filter change
  // re-runs the query and the flash then lands between two populated results. See EmptyRow.
  // "Still counts": a query superseded by the next keystroke must not set it either. It used to,
  // through a `.finally` next to the gated `.then`, so on a slow server the superseded query's return
  // showed "No log entries." over entries that had merely not arrived, with no error line (gatedLoad).
  const [loaded, setLoaded] = useState(false)
  // The four controls travel together so that Clear filters is one assignment — see LogFilters.
  const [filters, setFilters] = useState(() => initialLogFilters(new Date()))
  const { minLevel, source, from, to } = filters
  // Whether the operator has decided what To is. False while the box still holds the time the page
  // put there at load, which is what lets Refresh mean "up to now"; true the moment they type in it
  // or clear the filters, after which the value is theirs and nothing moves it.
  const [toPinned, setToPinned] = useState(false)
  // A `datetime-local` box the operator has half filled in reports its value as the empty string, so
  // the filter would silently become "no bound" and the query would widen to everything — a refresh
  // that ran looking exactly like a refresh that did not. The control's own `badInput` is the only
  // way to tell that state from an empty box, so it is carried here and the query waits for it.
  const [fromBad, setFromBad] = useState(false)
  const [toBad, setToBad] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // latestWins, not a cancelled flag: every keystroke in Source and every filter change re-runs the
  // query, and a broad earlier query (no filter) can return AFTER the narrower one that superseded it —
  // without the gate the stale, larger result set overwrites the fresh one, and the table no longer
  // matches the filter controls it sits under. See the note in latestWins.ts.
  const loadGate = useRef(latestWins())
  const load = () => {
    // A bound that cannot be read is not the same as no bound. Running the query anyway would drop
    // the filter and answer with everything, which is the shape the operator reported: Refresh with a
    // half-typed To looked like it did nothing at all. The table keeps the previous result and the
    // notice above it says which box to finish.
    if (fromBad || toBad)
      return
    // Both outcomes end the "loading" state — a failed query too, or the table sits on "Loading…"
    // forever with the real reason in the error line above it — but only for the latest query; a
    // superseded one delivers nothing (gatedLoad). Deliberately never reset to false on a re-query: a
    // filter change then leaves the previous result on screen until the new one lands, which is what
    // should happen — blanking the table between two populated results is the same flash this flag
    // exists to remove.
    void gatedLoad(
      loadGate.current,
      () =>
        logsApi.query({
          minLevel: minLevel === '' ? undefined : minLevel,
          source: source || undefined,
          from: from ? new Date(from).toISOString() : undefined,
          to: to ? new Date(to).toISOString() : undefined,
          limit: 300,
        }),
      (r) => {
        setLogs(r)
        setLoaded(true)
      },
      (e) => {
        setError(e instanceof Error ? e.message : String(e))
        setLoaded(true)
      },
    )
  }
  // The two flags belong in the list beside the values: a box that goes from half-typed to empty
  // leaves the value at '' both times, so only the flag's change says the query may run again.
  useEffect(load, [minLevel, source, from, to, fromBad, toBad])

  // Refresh, rather than a bare load(): while To is still the page's own "now" it is restamped, so
  // the button means "up to now" and an entry written since the page opened actually shows up.
  const refresh = () => setFilters((f) => ({ ...f, to: toForRefresh(f.to, toPinned, new Date()) }))

  // The way back to the whole list. Without it the only way to widen the view was to empty each
  // segment of two `datetime-local` boxes by hand.
  const clearFilters = () => {
    setFilters(noLogFilters)
    setFromBad(false)
    setToBad(false)
    // Cleared on purpose is a decision like any other: Refresh must not put "now" back.
    setToPinned(true)
  }

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
    // Said apart from the empty case: a half-typed box looks filled in and reads as empty, and
    // "set the To time" over a box that visibly has a date in it explains nothing.
    if (toBad) {
      setError('The "To" time is incomplete — fill in the date and the time before purging.')
      return
    }
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
      {incompleteTimeNotice(fromBad, toBad) && (
        <p className="text-warn">{incompleteTimeNotice(fromBad, toBad)}</p>
      )}

      <div className="toolbar">
        <label>
          Level:{' '}
          <select
            value={minLevel}
            onChange={(e) => setFilters((f) => ({ ...f, minLevel: e.target.value === '' ? '' : Number(e.target.value) }))}
          >
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
            onChange={(e) => setFilters((f) => ({ ...f, source: e.target.value }))}
          />
        </label>
        <label>
          From ({timeZone}):{' '}
          <input
            type="datetime-local"
            value={from}
            onChange={(e) => {
              setFilters((f) => ({ ...f, from: e.target.value }))
              setFromBad(e.target.validity.badInput)
            }}
          />
          <TimeEcho value={from} badInput={fromBad} />
        </label>
        <label>
          To ({timeZone}):{' '}
          <input
            type="datetime-local"
            value={to}
            onChange={(e) => {
              setFilters((f) => ({ ...f, to: e.target.value }))
              setToBad(e.target.validity.badInput)
              setToPinned(true)
            }}
          />
          <TimeEcho value={to} badInput={toBad} />
        </label>
        <button type="button" onClick={refresh}>
          Refresh
        </button>
        <button type="button" onClick={clearFilters}>
          Clear filters
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
