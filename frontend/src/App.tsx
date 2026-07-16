import { useState } from 'react'
import { AccountsPage } from './pages/AccountsPage'
import { BackupsPage } from './pages/BackupsPage'

type Tab = 'accounts' | 'backups'

function App() {
  const [tab, setTab] = useState<Tab>('accounts')

  return (
    <div style={{ maxWidth: 900, margin: '2rem auto', padding: '0 1rem' }}>
      <nav style={{ display: 'flex', gap: '1rem', marginBottom: '1.5rem' }}>
        <button
          type="button"
          onClick={() => setTab('accounts')}
          style={{ fontWeight: tab === 'accounts' ? 'bold' : 'normal' }}
        >
          Accounts
        </button>
        <button
          type="button"
          onClick={() => setTab('backups')}
          style={{ fontWeight: tab === 'backups' ? 'bold' : 'normal' }}
        >
          Backups
        </button>
      </nav>

      {tab === 'accounts' ? <AccountsPage /> : <BackupsPage />}
    </div>
  )
}

export default App
