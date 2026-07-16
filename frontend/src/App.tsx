import { useState } from 'react'
import { AccountsPage } from './pages/AccountsPage'
import { BackupConfigsPage } from './pages/BackupConfigsPage'
import { BackupsPage } from './pages/BackupsPage'
import { GroupsPage } from './pages/GroupsPage'
import { TasksPage } from './pages/TasksPage'
import { NotificationsPage } from './pages/NotificationsPage'
import { LogsPage } from './pages/LogsPage'

type Tab = 'accounts' | 'backups' | 'discovered' | 'groups' | 'tasks' | 'notifications' | 'logs'

const tabs: { key: Tab; label: string }[] = [
  { key: 'accounts', label: 'Accounts' },
  { key: 'backups', label: 'Backups' },
  { key: 'discovered', label: 'Discovered' },
  { key: 'groups', label: 'Groups' },
  { key: 'tasks', label: 'Tasks' },
  { key: 'notifications', label: 'Notifications' },
  { key: 'logs', label: 'Logs' },
]

function App() {
  const [tab, setTab] = useState<Tab>('accounts')

  return (
    <div style={{ maxWidth: 900, margin: '2rem auto', padding: '0 1rem' }}>
      <nav style={{ display: 'flex', gap: '1rem', marginBottom: '1.5rem' }}>
        {tabs.map((t) => (
          <button
            key={t.key}
            type="button"
            onClick={() => setTab(t.key)}
            style={{ fontWeight: tab === t.key ? 'bold' : 'normal' }}
          >
            {t.label}
          </button>
        ))}
      </nav>

      {tab === 'accounts' && <AccountsPage />}
      {tab === 'backups' && <BackupConfigsPage />}
      {tab === 'discovered' && <BackupsPage />}
      {tab === 'groups' && <GroupsPage />}
      {tab === 'tasks' && <TasksPage />}
      {tab === 'notifications' && <NotificationsPage />}
      {tab === 'logs' && <LogsPage />}
    </div>
  )
}

export default App
