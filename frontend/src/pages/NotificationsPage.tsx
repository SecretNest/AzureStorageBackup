import { useEffect, useState } from 'react'
import {
  notificationsApi,
  NotificationMethod,
  eventList,
  type NotificationConfig,
  type TestResult,
} from '../api/notifications'
import { Field } from '../components/modal'

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
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
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
      setError(e instanceof Error ? e.message : String(e))
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
      setTest({ success: false, error: e instanceof Error ? e.message : String(e) })
    } finally {
      setBusy(false)
    }
  }

  return (
    <section>
      <div className="page-header">
        <h1>Notifications</h1>
      </div>
      {error && <p className="text-danger">{error}</p>}

      <Field label="Enabled">
        <input type="checkbox" checked={cfg.enabled} onChange={(e) => set('enabled', e.target.checked)} />
      </Field>
      <Field label="URL">
        <input
          className="w-lg"
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
              className="w-lg"
              placeholder="{Title} and {Body} placeholders"
              value={cfg.bodyTemplate ?? ''}
              onChange={(e) => set('bodyTemplate', e.target.value)}
            />
          </Field>
          <Field label="Content-Type">
            <input className="w-md" value={cfg.contentType ?? ''} onChange={(e) => set('contentType', e.target.value)} />
          </Field>
        </>
      )}

      <Field label="Proxy URL">
        <input
          className="w-md"
          placeholder="http://host:port (optional)"
          value={cfg.proxyUrl ?? ''}
          onChange={(e) => set('proxyUrl', e.target.value)}
        />
      </Field>

      <fieldset style={{ marginTop: '1rem' }}>
        <legend>Notify on events</legend>
        {eventList.map((e) => (
          <label key={e.bit} className="row" style={{ width: 200 }}>
            <input
              type="checkbox"
              checked={(cfg.events & e.bit) !== 0}
              onChange={(ev) => toggleEvent(e.bit, ev.target.checked)}
            />
            {e.label}
          </label>
        ))}
      </fieldset>

      <div className="row" style={{ marginTop: '1rem' }}>
        <button type="button" className="btn-primary" onClick={save} disabled={busy}>
          Save
        </button>
        <button type="button" onClick={runTest} disabled={busy}>
          Send test
        </button>
        {saved && <span className="text-ok">Saved.</span>}
      </div>

      {test && (
        <p className={test.success ? 'text-ok' : 'text-danger'}>
          {test.success ? 'Test notification sent.' : `Test failed: ${test.error}`}
        </p>
      )}
    </section>
  )
}
