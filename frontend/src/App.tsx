import { useState } from 'react'
import { AccountsPage } from './pages/AccountsPage'
import { BackupsPage } from './pages/BackupsPage'
import { GroupsPage } from './pages/GroupsPage'
import { TasksPage } from './pages/TasksPage'

type Tab = 'accounts' | 'backups' | 'groups' | 'tasks'

const tabs: { key: Tab; label: string }[] = [
  { key: 'accounts', label: 'Accounts' },
  { key: 'backups', label: 'Backups' },
  { key: 'groups', label: 'Groups' },
  { key: 'tasks', label: 'Tasks' },
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
      {tab === 'backups' && <BackupsPage />}
      {tab === 'groups' && <GroupsPage />}
      {tab === 'tasks' && <TasksPage />}
    </div>
  )
}

export default App
