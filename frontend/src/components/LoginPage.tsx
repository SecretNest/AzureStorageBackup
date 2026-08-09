import { useState } from 'react'
import { authApi } from '../api/auth'
import { ApiError } from '../api/client'

/** Preset-password login page (design §6). No username. */
export function LoginPage({ onSignedIn }: { onSignedIn: () => void }) {
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await authApi.login(password)
      setPassword('')
      onSignedIn()
    } catch (e) {
      // Only a 401 means "wrong password". Reporting a network failure or a 500 as a bad password
      // makes people retype a perfectly correct password over and over during an outage.
      if (e instanceof ApiError && e.status === 401) setError('Incorrect password.')
      else if (e instanceof ApiError)
        setError(`Sign-in failed: the server returned ${e.status}. Please try again.`)
      else setError('Sign-in failed: could not reach the server. Please try again.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="auth-page">
      <div className="auth-card">
        <h1>Azure Storage Backup</h1>
        <form onSubmit={submit}>
          <input
            type="password"
            name="password"
            // Let password managers recognise this as a login form and offer to save or fill it —
            // a long random password is only usable if it can be autofilled.
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            placeholder="Password"
            autoFocus
          />
          <button type="submit" disabled={busy || !password}>
            {busy ? 'Signing in…' : 'Sign in'}
          </button>
        </form>
        {error && <p className="text-danger" style={{ marginTop: '0.75rem' }}>{error}</p>}
      </div>
    </div>
  )
}
