import { useEffect, useState } from 'react'
import { KeyringBanner } from './components/KeyringBanner'
import { LoginPage } from './components/LoginPage'
import { AccountsPage } from './pages/AccountsPage'
import { BackupConfigsPage } from './pages/BackupConfigsPage'
import { BackupsPage } from './pages/BackupsPage'
import { GroupsPage } from './pages/GroupsPage'
import { TasksPage } from './pages/TasksPage'
import { NotificationsPage } from './pages/NotificationsPage'
import { LogsPage } from './pages/LogsPage'
import { SettingsPage } from './pages/SettingsPage'
import { authApi, type AuthStatus } from './api/auth'
import { setUnauthorizedHandler } from './api/client'

type Tab = 'accounts' | 'backups' | 'discovered' | 'groups' | 'tasks' | 'notifications' | 'logs' | 'settings'

const tabs: { key: Tab; label: string }[] = [
  { key: 'accounts', label: 'Accounts' },
  { key: 'backups', label: 'Backups' },
  { key: 'discovered', label: 'Discovered' },
  { key: 'groups', label: 'Groups' },
  { key: 'tasks', label: 'Tasks' },
  { key: 'notifications', label: 'Notifications' },
  { key: 'logs', label: 'Logs' },
  { key: 'settings', label: 'Settings' },
]

function App() {
  const [tab, setTab] = useState<Tab>('accounts')
  const [auth, setAuth] = useState<AuthStatus | null>(null)

  const refreshAuth = () => {
    authApi.status().then(setAuth).catch(() => setAuth({ required: true, authenticated: false }))
  }
  useEffect(() => {
    setUnauthorizedHandler(() => setAuth({ required: true, authenticated: false }))
    refreshAuth()
  }, [])

  // 状态未知时不渲染任何东西，避免主界面闪一下再被登录页替换
  if (auth === null) return null

  // 未认证时**不挂载**主界面组件——挂了它们会各自发请求，拿回一片 401
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
        {auth.required && (
          <div className="sidebar-footer">
            <button
              type="button"
              // 无论服务端登出成功与否都清掉本地状态：失败却停在主界面，
              // 会让人以为自己已经退出了——在共用机器上这就是个安全问题。
              onClick={() => {
                const signedOut = () => setAuth({ required: true, authenticated: false })
                authApi.logout().then(signedOut, signedOut)
              }}
            >
              Log out
            </button>
          </div>
        )}
      </aside>

      <main className="app-main">
        <KeyringBanner onGoToAccounts={() => setTab('accounts')} />

        {tab === 'accounts' && <AccountsPage />}
        {tab === 'backups' && <BackupConfigsPage />}
        {tab === 'discovered' && <BackupsPage />}
        {tab === 'groups' && <GroupsPage />}
        {tab === 'tasks' && <TasksPage />}
        {tab === 'notifications' && <NotificationsPage />}
        {tab === 'logs' && <LogsPage />}
        {tab === 'settings' && <SettingsPage />}
      </main>
    </div>
  )
}

export default App
