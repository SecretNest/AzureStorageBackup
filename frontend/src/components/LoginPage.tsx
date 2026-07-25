import { useState } from 'react'
import { authApi } from '../api/auth'

/** 预置密码登录页（设计 §6）。无用户名；文案一律英文。 */
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
    } catch {
      setError('Incorrect password.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div style={{ maxWidth: 320, margin: '6rem auto', padding: '0 1rem' }}>
      <h1 style={{ fontSize: '1.25rem', marginBottom: '1rem' }}>Azure Storage Backup</h1>
      <form onSubmit={submit}>
        <input
          type="password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          placeholder="Password"
          autoFocus
          style={{ width: '100%', padding: '0.5rem', marginBottom: '0.75rem' }}
        />
        <button type="submit" disabled={busy || !password} style={{ width: '100%', padding: '0.5rem' }}>
          {busy ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
      {error && <p style={{ color: '#b91c1c', marginTop: '0.75rem' }}>{error}</p>}
    </div>
  )
}
