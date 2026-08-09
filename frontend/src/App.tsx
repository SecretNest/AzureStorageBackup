import { useEffect, useState } from 'react'
import { KeyringBanner } from './components/KeyringBanner'
import { LoginPage } from './components/LoginPage'
import { BackupConfigsPage } from './pages/BackupConfigsPage'
import { TasksPage } from './pages/TasksPage'
import { LogsPage } from './pages/LogsPage'
import { SettingsPage } from './pages/SettingsPage'
import { accountsApi } from './api/accounts'
import { authApi, type AuthStatus } from './api/auth'
import { setUnauthorizedHandler } from './api/client'

// Accounts is no longer a top-level tab; it moved into Settings (first section): accounts are
// configured once and never touched again, so a permanent nav entry misrepresents them. The cost is
// that new users have no obvious entry point — handled by the default-tab logic below.
type Tab = 'backups' | 'tasks' | 'logs' | 'settings'

const tabs: { key: Tab; label: string }[] = [
  { key: 'backups', label: 'Backups' },
  { key: 'tasks', label: 'Tasks' },
  { key: 'logs', label: 'Logs' },
  { key: 'settings', label: 'Settings' },
]

function App() {
  // null = which page to show is undecided. Ask "are there any accounts?" first: with none, go to
  // Settings (the accounts section is at the top of it); otherwise default to Backups, the page
  // people actually look at every day.
  const [tab, setTab] = useState<Tab | null>(null)
  const [auth, setAuth] = useState<AuthStatus | null>(null)

  const refreshAuth = () => {
    authApi.status().then(setAuth).catch(() => setAuth({ required: true, authenticated: false }))
  }

  // Clear local state whether or not the server logout succeeded: failing and staying on the main UI
  // makes people believe they logged out — on a shared machine that is a security problem.
  const logout = () => {
    const signedOut = () => setAuth({ required: true, authenticated: false })
    authApi.logout().then(signedOut, signedOut)
  }

  useEffect(() => {
    setUnauthorizedHandler(() => setAuth({ required: true, authenticated: false }))
    refreshAuth()
  }, [])

  // Only ask about accounts once authenticated — asking earlier just returns 401. If it cannot be
  // determined, assume there are accounts: sending an existing user to Settings is more annoying
  // than one extra click for a new one.
  const signedIn = auth !== null && (!auth.required || auth.authenticated)
  useEffect(() => {
    if (!signedIn || tab !== null) return
    accountsApi
      .list()
      .then((list) => setTab(list.length === 0 ? 'settings' : 'backups'))
      .catch(() => setTab('backups'))
  }, [signedIn, tab])

  // Render nothing while the status is unknown, so the main UI does not flash before the login page replaces it
  if (auth === null) return null

  // While unauthenticated, do **not** mount the main UI — its components would each fire requests and get back a wall of 401s
  if (auth.required && !auth.authenticated)
    return <LoginPage onSignedIn={refreshAuth} />

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="sidebar-brand">Azure Storage Backup</div>
        <nav className="sidebar-nav">
          {tabs.map((t) => (
            <button
              key={t.key}
              type="button"
              onClick={() => setTab(t.key)}
              className={tab === t.key ? 'nav-item nav-item-active' : 'nav-item'}
              aria-current={tab === t.key ? 'page' : undefined}
            >
              {t.label}
            </button>
          ))}
        </nav>
      </aside>

      <main className="app-main">
        <KeyringBanner onGoToAccounts={() => setTab('settings')} />

        {tab === 'backups' && <BackupConfigsPage />}
        {tab === 'tasks' && <TasksPage />}
        {tab === 'logs' && <LogsPage />}
        {tab === 'settings' && <SettingsPage authRequired={auth.required} onLogout={logout} />}
      </main>
    </div>
  )
}

export default App
