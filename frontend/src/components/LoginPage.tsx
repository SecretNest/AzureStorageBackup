import { useState } from 'react'
import { authApi } from '../api/auth'
import { ApiError } from '../api/client'

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
    } catch (e) {
      // 只有 401 才是「密码不对」。把网络故障/500 也说成密码错，会让人在故障期间
      // 反复重打一个本来就正确的密码。
      if (e instanceof ApiError && e.status === 401) setError('Incorrect password.')
      else if (e instanceof ApiError)
        setError(`Sign-in failed: the server returned ${e.status}. Please try again.`)
      else setError('Sign-in failed: could not reach the server. Please try again.')
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
          name="password"
          // 让密码管理器认得出这是登录框并愿意保存/填充——一串又长又随机的密码
          // 只有在能被自动填充时才用得下去。
          autoComplete="current-password"
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
