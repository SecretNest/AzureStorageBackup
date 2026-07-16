import { useEffect, useState, type ReactNode } from 'react'
import {
  notificationsApi,
  NotificationMethod,
  eventList,
  type NotificationConfig,
  type TestResult,
} from '../api/notifications'

const emptyCfg: NotificationConfig = {
  enabled: false,
  url: '',
  method: NotificationMethod.Post,
  bodyTemplate: '{Title}\n{Body}',
  contentType: 'text/plain',
  events: 0,
  proxyUrl: '',
}

export function NotificationsPage() {
  const [cfg, setCfg] = useState<NotificationConfig>(emptyCfg)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  const [test, setTest] = useState<TestResult | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    notificationsApi
      .get()
      .then((c) => setCfg({ ...c, bodyTemplate: c.bodyTemplate ?? '', proxyUrl: c.proxyUrl ?? '' }))
      .catch((e) => setError(String(e)))
  }, [])

  const set = <K extends keyof NotificationConfig>(k: K, v: NotificationConfig[K]) =>
    setCfg((c) => ({ ...c, [k]: v }))

  const toggleEvent = (bit: number, on: boolean) =>
    setCfg((c) => ({ ...c, events: on ? c.events | bit : c.events & ~bit }))

  const save = async () => {
    setBusy(true)
    setError(null)
    setSaved(false)
    try {
      await notificationsApi.update(cfg)
      setSaved(true)
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  const runTest = async () => {
    setBusy(true)
    setTest(null)
    try {
      setTest(await notificationsApi.test(cfg))
    } catch (e) {
      setTest({ success: false, error: String(e) })
    } finally {
      setBusy(false)
    }
  }

  return (
    <section>
      <h1>Notifications</h1>
      {error && <p style={{ color: 'crimson' }}>{error}</p>}

      <Field label="Enabled">
        <input type="checkbox" checked={cfg.enabled} onChange={(e) => set('enabled', e.target.checked)} />
      </Field>
      <Field label="URL">
        <input
          style={{ width: 380 }}
          placeholder="https://hook.example/notify?t={Title}"
          value={cfg.url}
          onChange={(e) => set('url', e.target.value)}
        />
      </Field>
      <Field label="Method">
        <select value={cfg.method} onChange={(e) => set('method', Number(e.target.value))}>
          <option value={NotificationMethod.Get}>GET</option>
          <option value={NotificationMethod.Post}>POST</option>
        </select>
      </Field>

      {cfg.method === NotificationMethod.Post && (
        <>
          <Field label="Body template">
            <textarea
              rows={3}
              style={{ width: 380, fontFamily: 'monospace' }}
              placeholder="{Title} and {Body} placeholders"
              value={cfg.bodyTemplate ?? ''}
              onChange={(e) => set('bodyTemplate', e.target.value)}
            />
          </Field>
          <Field label="Content-Type">
            <input value={cfg.contentType ?? ''} onChange={(e) => set('contentType', e.target.value)} />
          </Field>
        </>
      )}

      <Field label="Proxy URL">
        <input
          placeholder="http://host:port (optional)"
          value={cfg.proxyUrl ?? ''}
          onChange={(e) => set('proxyUrl', e.target.value)}
        />
      </Field>

      <fieldset style={{ marginTop: '1rem' }}>
        <legend>Notify on events</legend>
        {eventList.map((e) => (
          <label key={e.bit} style={{ display: 'inline-flex', gap: '0.3rem', width: 200, margin: '0.2rem 0' }}>
            <input
              type="checkbox"
              checked={(cfg.events & e.bit) !== 0}
              onChange={(ev) => toggleEvent(e.bit, ev.target.checked)}
            />
            {e.label}
          </label>
        ))}
      </fieldset>

      <div style={{ marginTop: '1rem' }}>
        <button type="button" onClick={save} disabled={busy}>
          Save
        </button>{' '}
        <button type="button" onClick={runTest} disabled={busy}>
          Send test
        </button>
        {saved && <span style={{ color: 'green', marginLeft: '0.6rem' }}>Saved.</span>}
      </div>

      {test && (
        <p style={{ color: test.success ? 'green' : 'crimson' }}>
          {test.success ? 'Test notification sent.' : `Test failed: ${test.error}`}
        </p>
      )}
    </section>
  )
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label style={{ display: 'flex', gap: '0.5rem', alignItems: 'flex-start', margin: '0.4rem 0' }}>
      <span style={{ width: 130, display: 'inline-block' }}>{label}</span>
      {children}
    </label>
  )
}
