import { useEffect, useState, type ReactNode } from 'react'
import { settingsApi, type GlobalSettings } from '../api/settings'
import { StorageTier, tierLabels, retentionModeLabels } from '../api/backupConfigs'

const MB = 1024 * 1024

export function SettingsPage() {
  const [s, setS] = useState<GlobalSettings | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)

  useEffect(() => {
    settingsApi.get().then(setS).catch((e) => setError(String(e)))
  }, [])

  if (!s) return <section><h1>Settings</h1><p>Loading…</p></section>

  const set = <K extends keyof GlobalSettings>(k: K, v: GlobalSettings[K]) =>
    setS((cur) => (cur ? { ...cur, [k]: v } : cur))

  const save = async () => {
    setError(null)
    setSaved(false)
    try {
      setS(await settingsApi.update(s))
      setSaved(true)
    } catch (e) {
      setError(String(e))
    }
  }

  return (
    <section>
      <h1>Settings</h1>
      <p style={{ color: '#666' }}>Defaults for new backups, plus global options.</p>
      {error && <p style={{ color: 'crimson' }}>{error}</p>}

      <h2>New-backup defaults</h2>
      <Field label="Index tier">
        <TierSelect value={s.defaultIndexTier} onChange={(v) => set('defaultIndexTier', v)} archive={false} />
      </Field>
      <Field label="Data tier">
        <TierSelect value={s.defaultDataTier} onChange={(v) => set('defaultDataTier', v)} archive />
      </Field>
      <Field label="Max versions">
        <Num value={s.defaultMaxVersions} onChange={(v) => set('defaultMaxVersions', v)} />
      </Field>
      <Field label="Max age (days)">
        <Num value={s.defaultMaxAgeDays} onChange={(v) => set('defaultMaxAgeDays', v)} />
      </Field>
      <Field label="Retention mode">
        <select value={s.defaultRetentionMode} onChange={(e) => set('defaultRetentionMode', Number(e.target.value))}>
          {Object.entries(retentionModeLabels).map(([v, label]) => (
            <option key={v} value={v}>{label}</option>
          ))}
        </select>
      </Field>
      <Field label="Single-file threshold (MB)">
        <Num value={Math.round(s.defaultSingleFileThresholdBytes / MB)} onChange={(v) => set('defaultSingleFileThresholdBytes', v * MB)} />
      </Field>
      <Field label="Group cap (MB)">
        <Num value={Math.round(s.defaultGroupCapBytes / MB)} onChange={(v) => set('defaultGroupCapBytes', v * MB)} />
      </Field>
      <Field label="Volume size (MB, 0=off)">
        <Num value={s.defaultVolumeBytes ? Math.round(s.defaultVolumeBytes / MB) : 0} onChange={(v) => set('defaultVolumeBytes', v > 0 ? v * MB : null)} />
      </Field>
      <Field label="Repack: download by tier">
        <span style={{ display: 'flex', gap: '0.8rem', flexWrap: 'wrap', fontSize: '0.85rem' }}>
          <label><input type="checkbox" checked={s.repackDownloadHot} onChange={(e) => set('repackDownloadHot', e.target.checked)} /> Hot</label>
          <label><input type="checkbox" checked={s.repackDownloadCool} onChange={(e) => set('repackDownloadCool', e.target.checked)} /> Cool</label>
          <label><input type="checkbox" checked={s.repackDownloadCold} onChange={(e) => set('repackDownloadCold', e.target.checked)} /> Cold</label>
          <label><input type="checkbox" checked={s.repackDownloadArchive} onChange={(e) => set('repackDownloadArchive', e.target.checked)} /> Archive</label>
        </span>
      </Field>
      <Field label="Include symlinks">
        <input type="checkbox" checked={s.defaultIncludeSymlinks} onChange={(e) => set('defaultIncludeSymlinks', e.target.checked)} />
      </Field>
      <Field label="Ignore rules">
        <Rules value={s.defaultIgnoreRules} onChange={(v) => set('defaultIgnoreRules', v)} />
      </Field>
      <Field label="Don't compress">
        <Rules value={s.defaultDontCompressRules} onChange={(v) => set('defaultDontCompressRules', v)} />
      </Field>
      <Field label="Don't group">
        <Rules value={s.defaultDontGroupRules} onChange={(v) => set('defaultDontGroupRules', v)} />
      </Field>

      <h2>Global</h2>
      <Field label="Upload concurrency">
        <Num value={s.uploadConcurrency} onChange={(v) => set('uploadConcurrency', v)} />
      </Field>
      <Field label="Download concurrency">
        <Num value={s.downloadConcurrency} onChange={(v) => set('downloadConcurrency', v)} />
      </Field>
      <Field label="Ephemeral log retention (days)">
        <Num value={s.logEphemeralMaxAgeDays} onChange={(v) => set('logEphemeralMaxAgeDays', v)} />
      </Field>
      <Field label="Verbose (debug) logging default">
        <input type="checkbox" checked={s.defaultVerboseLogging} onChange={(e) => set('defaultVerboseLogging', e.target.checked)} />
      </Field>
      <Field label="Retry backoff (seconds)">
        <input style={{ width: 300, fontFamily: 'monospace' }} value={s.retryBackoffSeconds}
          onChange={(e) => set('retryBackoffSeconds', e.target.value)} placeholder="5,30,90,300" />
      </Field>
      <Field label="Retry max total (min)">
        <Num value={s.retryMaxTotalMinutes} onChange={(v) => set('retryMaxTotalMinutes', v)} />
      </Field>
      <Field label="Dead-weight threshold (%)">
        <Num value={s.deadWeightThresholdPercent} onChange={(v) => set('deadWeightThresholdPercent', v)} />
      </Field>

      <div style={{ marginTop: '1rem' }}>
        <button type="button" onClick={save}>Save</button>
        {saved && <span style={{ color: 'green', marginLeft: '0.6rem' }}>Saved.</span>}
      </div>
    </section>
  )
}

function TierSelect({ value, onChange, archive }: { value: number; onChange: (v: number) => void; archive: boolean }) {
  return (
    <select value={value} onChange={(e) => onChange(Number(e.target.value))}>
      <option value={StorageTier.Hot}>{tierLabels[StorageTier.Hot]}</option>
      <option value={StorageTier.Cool}>{tierLabels[StorageTier.Cool]}</option>
      <option value={StorageTier.Cold}>{tierLabels[StorageTier.Cold]}</option>
      {archive && <option value={StorageTier.Archive}>{tierLabels[StorageTier.Archive]}</option>}
    </select>
  )
}

function Num({ value, onChange }: { value: number; onChange: (v: number) => void }) {
  return <input type="number" value={value} onChange={(e) => onChange(Number(e.target.value))} />
}

function Rules({ value, onChange }: { value: string | null; onChange: (v: string) => void }) {
  return (
    <textarea rows={2} style={{ width: 300, fontFamily: 'monospace', fontSize: '0.85rem' }}
      value={value ?? ''} onChange={(e) => onChange(e.target.value)} />
  )
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label style={{ display: 'flex', gap: '0.5rem', alignItems: 'flex-start', margin: '0.4rem 0' }}>
      <span style={{ width: 200, display: 'inline-block' }}>{label}</span>
      {children}
    </label>
  )
}
