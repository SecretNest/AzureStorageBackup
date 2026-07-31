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

// Accounts 不再是顶级标签，它收进了 Settings（排在最前面）：账户是"配一次就不再碰"的东西，
// 常驻一个导航项名不副实。代价是新用户没有明显的入口——由下面挑默认标签那段补上。
type Tab = 'backups' | 'tasks' | 'logs' | 'settings'

const tabs: { key: Tab; label: string }[] = [
  { key: 'backups', label: 'Backups' },
  { key: 'tasks', label: 'Tasks' },
  { key: 'logs', label: 'Logs' },
  { key: 'settings', label: 'Settings' },
]

function App() {
  // null = 还没决定看哪一页。要先问一句"有没有账户"：一个都没有时先带去 Settings
  // （账户区在那一页最上面），否则默认 Backups——那才是天天要看的那页。
  const [tab, setTab] = useState<Tab | null>(null)
  const [auth, setAuth] = useState<AuthStatus | null>(null)

  const refreshAuth = () => {
    authApi.status().then(setAuth).catch(() => setAuth({ required: true, authenticated: false }))
  }

  // 无论服务端登出成功与否都清掉本地状态：失败却停在主界面，
  // 会让人以为自己已经退出了——在共用机器上这就是个安全问题。
  const logout = () => {
    const signedOut = () => setAuth({ required: true, authenticated: false })
    authApi.logout().then(signedOut, signedOut)
  }

  useEffect(() => {
    setUnauthorizedHandler(() => setAuth({ required: true, authenticated: false }))
    refreshAuth()
  }, [])

  // 认证过了才问账户——没认证就发这个请求只会拿回 401。查不出来时按"有账户"处理：
  // 把老用户扔到 Settings 比让新用户多点一下更烦人。
  const signedIn = auth !== null && (!auth.required || auth.authenticated)
  useEffect(() => {
    if (!signedIn || tab !== null) return
    accountsApi
      .list()
      .then((list) => setTab(list.length === 0 ? 'settings' : 'backups'))
      .catch(() => setTab('backups'))
  }, [signedIn, tab])

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
