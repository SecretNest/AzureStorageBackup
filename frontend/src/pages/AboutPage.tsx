import { useEffect, useState } from 'react'
import { systemApi } from '../api/system'

/// What this install *is*, and the one action that ends a session with it.
///
/// The version and the temp-directory map used to sit at the bottom of the Logs page, under everything the log table
/// scrolls through. They are not log data: nobody filters them, they never change while you watch, and the reason to
/// look them up — quoting a version in a bug report, or finding out which paths a docker volume has to cover — has
/// nothing to do with reading logs. Behind Logs' filter toolbar they were also the last thing on the longest page in
/// the app.
///
/// Log out shares this page rather than a fifth one of its own: it is the same category of thing — about this
/// installation and this session, not about any backup.
export function AboutSection({ authRequired, onLogout }: { authRequired?: boolean; onLogout?: () => void }) {
  const [paths, setPaths] = useState<Record<string, string>>({})
  const [version, setVersion] = useState('')

  // Failures are swallowed on purpose, as they were on the Logs page: neither figure is worth an error banner, and a
  // dead /system endpoint is already going to announce itself everywhere else.
  useEffect(() => {
    systemApi.paths().then(setPaths).catch(() => {})
    systemApi.version().then((v) => setVersion(v.version)).catch(() => {})
  }, [])

  return (
    <>
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

      {/* Log out is not in the sidebar: the phone tier's bottom bar has four slots and no room for a fifth. Desktop
          moved with it — one function in two places is what later maintenance forgets to sync. */}
      {authRequired && onLogout && (
        <>
          <h2>Session</h2>
          <button type="button" onClick={onLogout}>
            Log out
          </button>
        </>
      )}
    </>
  )
}
