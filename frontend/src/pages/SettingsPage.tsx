import { useEffect, useState } from 'react'
import { settingsApi, sevenZipPriorityLabels, type GlobalSettings } from '../api/settings'
import { StorageTier, tierLabels, retentionModeLabels } from '../api/backupConfigs'
import { Field } from '../components/Field'
import { AccountsSection } from './AccountsPage'
import { NotificationsSection } from './NotificationsPage'

const MB = 1024 * 1024

/// Settings is a shell around several sections. Accounts comes **first** and does **not** wait for
/// global settings to load: a user with no accounts configured lands straight here (see the
/// default-tab logic in App.tsx) and should see it immediately — putting it lower, or behind a
/// "Loading…", hides the one thing a new user can actually do.
export function SettingsPage({
  authRequired,
  onLogout,
}: {
  authRequired?: boolean
  onLogout?: () => void
}) {
  return (
    <section>
      <div className="page-header">
        <h1>Settings</h1>
      </div>
      <AccountsSection />
      <BackupDefaults />
      <NotificationsSection />
      {/* Log out moved here from the sidebar: the phone tier's bottom bar has four slots and no room
          for a fifth. Desktop moved too — one function in two places is what later maintenance forgets to sync. */}
      {authRequired && onLogout && (
        <>
          <h2>Session</h2>
          <button type="button" onClick={onLogout}>
            Log out
          </button>
        </>
      )}
    </section>
  )
}

function BackupDefaults() {
  const [s, setS] = useState<GlobalSettings | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)

  useEffect(() => {
    settingsApi.get().then(setS).catch((e) => setError(e instanceof Error ? e.message : String(e)))
  }, [])

  if (!s) return <><h2 style={{ marginTop: '2rem' }}>Backup defaults</h2><p>Loading…</p></>

  const set = <K extends keyof GlobalSettings>(k: K, v: GlobalSettings[K]) =>
    setS((cur) => (cur ? { ...cur, [k]: v } : cur))

  const save = async () => {
    setError(null)
    setSaved(false)
    try {
      setS(await settingsApi.update(s))
      setSaved(true)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  return (
    <>
      <h2 style={{ marginTop: '2rem' }}>Backup defaults</h2>
      <p className="text-muted">Defaults for new backups, and for any existing backup field set to Use default.</p>
      {error && <p className="text-danger">{error}</p>}

      <h2>Defaults</h2>
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
      <Field label="Repack: download by tier" multi>
        <span className="row" style={{ flexWrap: 'wrap' }}>
          <label><input type="checkbox" checked={s.repackDownloadHot} onChange={(e) => set('repackDownloadHot', e.target.checked)} /> Hot</label>
          <label><input type="checkbox" checked={s.repackDownloadCool} onChange={(e) => set('repackDownloadCool', e.target.checked)} /> Cool</label>
          <label><input type="checkbox" checked={s.repackDownloadCold} onChange={(e) => set('repackDownloadCold', e.target.checked)} /> Cold</label>
          <label><input type="checkbox" checked={s.repackDownloadArchive} onChange={(e) => set('repackDownloadArchive', e.target.checked)} /> Archive</label>
        </span>
      </Field>
      <Field label="Include symlinks">
        <input type="checkbox" checked={s.defaultIncludeSymlinks} onChange={(e) => set('defaultIncludeSymlinks', e.target.checked)} />
      </Field>
      {/* The same note as on the new-backup form: rule paths are relative to each backup's Local Root, not to a full path on the host. */}
      <p className="text-muted">
        The rule lists below use gitignore syntax and match paths <strong>relative to each backup's local
        root</strong> — write <span className="mono">/Backup/</span>, not the full host path. A trailing{' '}
        <span className="mono">/</span> means "this directory and everything under it".{' '}
        Each list has two boxes taking the same syntax: the first matches <strong>case-sensitively</strong>,
        the second <strong>ignoring case</strong>. Either box takes paths or extensions — extensions are
        usually easier in the second (<span className="mono">*.mp4</span> then also catches{' '}
        <span className="mono">.MP4</span>), paths in the first, because on Linux{' '}
        <span className="mono">Temp/</span> and <span className="mono">temp/</span> really are two directories.
        The two boxes form one list with the insensitive one last, so a rule there overrides a matching rule above it.
      </p>
      <Field label="Ignore rules">
        <Rules value={s.defaultIgnoreRules} onChange={(v) => set('defaultIgnoreRules', v)} />
        <p className="text-muted" style={{ margin: 'var(--sp-2) 0 var(--sp-1)' }}>
          Ignoring case — same syntax as above.
        </p>
        <Rules value={s.defaultIgnoreRulesCaseInsensitive} onChange={(v) => set('defaultIgnoreRulesCaseInsensitive', v)} />
      </Field>
      <Field label="Don't compress">
        <Rules value={s.defaultDontCompressRules} onChange={(v) => set('defaultDontCompressRules', v)} />
        <p className="text-muted" style={{ margin: 'var(--sp-2) 0 var(--sp-1)' }}>
          Ignoring case — same syntax as above.
        </p>
        <Rules value={s.defaultDontCompressRulesCaseInsensitive} onChange={(v) => set('defaultDontCompressRulesCaseInsensitive', v)} />
      </Field>
      <Field label="Don't group">
        <Rules value={s.defaultDontGroupRules} onChange={(v) => set('defaultDontGroupRules', v)} />
        <p className="text-muted" style={{ margin: 'var(--sp-2) 0 var(--sp-1)' }}>
          Ignoring case — same syntax as above.
        </p>
        <Rules value={s.defaultDontGroupRulesCaseInsensitive} onChange={(v) => set('defaultDontGroupRulesCaseInsensitive', v)} />
      </Field>
      <Field label="Pack across directories">
        <Rules value={s.defaultCrossDirGroupRules} onChange={(v) => set('defaultCrossDirGroupRules', v)} />
        <p className="text-muted" style={{ margin: 'var(--sp-2) 0 var(--sp-1)' }}>
          Ignoring case — same syntax as above.
        </p>
        <Rules value={s.defaultCrossDirGroupRulesCaseInsensitive} onChange={(v) => set('defaultCrossDirGroupRulesCaseInsensitive', v)} />
      </Field>
      <p className="text-muted" style={{ marginTop: '-0.4rem' }}>
        Small files are normally packed per directory. Paths matching these rules are packed across
        directory boundaries instead — useful for hash-sharded trees (media metadata, Git objects,
        caches) where each directory holds only a file or two, which would otherwise produce nearly
        one archive per file. Empty means everything is packed per directory. "Don't group" wins over
        this.
      </p>

      <h2>Global</h2>
      <Field label="Upload concurrency">
        <Num value={s.uploadConcurrency} onChange={(v) => set('uploadConcurrency', v)} />
      </Field>
      <Field label="Download concurrency">
        <Num value={s.downloadConcurrency} onChange={(v) => set('downloadConcurrency', v)} />
      </Field>
      {/* Set globally, but spent per run — the one thing about these two numbers a user is likely to
          get wrong, since everything else under this heading really is one shared budget. */}
      <p className="text-muted" style={{ marginTop: '-0.4rem' }}>
        Counted per operation, not shared between them. Each backup gets this many upload streams of
        its own, and each restore or deep check this many downloads, so two backups running at once
        open twice this many connections — divide by how many you expect to overlap if you are
        sizing this against a bandwidth cap. A backup runs one stream above the number set here; the
        extra one is what keeps a split archive's volumes from stalling at the hand-off. The staging
        area below is the setting that <em>is</em> shared: its allowance is split evenly across the
        runs in flight.
      </p>
      <Field label="Check HEAD concurrency">
        <Num value={s.checkHeadConcurrency} onChange={(v) => set('checkHeadConcurrency', v)} />
      </Field>
      <p className="text-muted" style={{ marginTop: '-0.4rem' }}>
        Used by the check&apos;s existence+size probing and by rehydration estimates — requests that
        ask about a blob without downloading it. They cost a round-trip each and no bandwidth, so
        this runs higher than the download concurrency and sizing that against a bandwidth cap does
        not slow checking down.
      </p>
      <Field label="Ephemeral log retention (days)">
        <Num value={s.logEphemeralMaxAgeDays} onChange={(v) => set('logEphemeralMaxAgeDays', v)} />
      </Field>
      <Field label="Verbose (debug) logging default">
        <input type="checkbox" checked={s.defaultVerboseLogging} onChange={(e) => set('defaultVerboseLogging', e.target.checked)} />
      </Field>
      <Field label="Retry backoff (seconds)">
        <input className="w-md mono" value={s.retryBackoffSeconds}
          onChange={(e) => set('retryBackoffSeconds', e.target.value)} placeholder="5,30,90,300" />
      </Field>
      <Field label="Retry max total (min)">
        <Num value={s.retryMaxTotalMinutes} onChange={(v) => set('retryMaxTotalMinutes', v)} />
      </Field>
      <Field label="Dead-weight threshold (%)">
        <Num value={s.deadWeightThresholdPercent} onChange={(v) => set('deadWeightThresholdPercent', v)} />
      </Field>
      <Field label="Staging area size limit (MB)">
        <Num value={Math.round(s.stagedLimitBytes / MB)} onChange={(v) => set('stagedLimitBytes', v * MB)} />
      </Field>
      <Field label="Processing re-verify max attempts">
        <Num value={s.processingMaxAttempts} onChange={(v) => set('processingMaxAttempts', v)} />
      </Field>
      <Field label="Overlap diffing and uploading">
        <input type="checkbox" checked={s.overlapDiffAndUpload}
          onChange={(e) => set('overlapDiffAndUpload', e.target.checked)} />
      </Field>
      <p className="text-muted" style={{ marginTop: '-0.4rem' }}>
        On by default: a changed file starts uploading as soon as the diff decides it changed,
        instead of waiting for every file to be hashed first. On a first backup that keeps the
        network busy for hours that would otherwise be idle. Turn it off if your disks are slow
        enough that reading for the diff and reading for compression at the same time makes the
        whole run take longer.
      </p>
      <Field label="Resume interrupted backups on startup">
        <input type="checkbox" checked={s.autoResumeInterruptedRuns}
          onChange={(e) => set('autoResumeInterruptedRuns', e.target.checked)} />
      </Field>
      <p className="text-muted" style={{ marginTop: '-0.4rem' }}>
        On by default: when the server shuts down cleanly — a planned reboot, or an image upgrade —
        any backup that was still running is parked, and picks up where it stopped once the server
        is back, without re-uploading what already reached the cloud. Only that case is resumed
        automatically. A backup you paused or cancelled yourself, and one that stopped for any
        other reason, waits for you to press Run.
      </p>
      <Field label="7-Zip CPU priority">
        <select value={s.sevenZipPriority} onChange={(e) => set('sevenZipPriority', Number(e.target.value))}>
          {Object.entries(sevenZipPriorityLabels).map(([v, label]) => (
            <option key={v} value={v}>{label}</option>
          ))}
        </select>
      </Field>
      <p className="text-muted" style={{ marginTop: '-0.4rem' }}>
        Compression and extraction are the most CPU-hungry things this app does. Lowest gives them
        only the CPU nobody else wants. Raise it if backups are the reason you bought the machine.
        Applies to every 7-Zip process: backup, restore, check, and repair.
      </p>
      {/* This setting used to promise it kept backups "out of the way of everything else on the machine",
          full stop. On Linux it sets nice, which reaches the CPU scheduler and not the block-IO queue, so
          for the complaint people actually have — the box going unresponsive during a backup — it does
          nothing, and the old wording sent them to the wrong knob. */}
      <p className="text-muted" style={{ marginTop: '-0.4rem' }}>
        <strong>CPU only.</strong> If what suffers during a backup is the <em>disk</em> — file listings
        over SMB stalling for seconds, other apps crawling — this setting will not help, because the
        contention is not for the CPU. Lower the whole process's disk priority instead, with the{' '}
        <span className="mono">Backup__IoPriority</span> environment variable (
        <span className="mono">Normal</span> / <span className="mono">Low</span> /{' '}
        <span className="mono">Idle</span>); it covers scanning, hashing, compression and uploading
        alike. It has to be set before the app starts, which is why it is not on this page. Note that
        only the BFQ disk scheduler acts on it — the startup log records whether it took.
      </p>

      <div className="row" style={{ marginTop: '1rem' }}>
        <button type="button" className="btn-primary" onClick={save}>Save</button>
        {saved && <span className="text-ok">Saved.</span>}
      </div>
    </>
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
  // The input keeps its own raw text rather than being controlled by value directly: clearing the box
  // then shows blank (instead of value snapping it back to the old number) while the parent state
  // keeps the previous number, so Number('') === 0 is never written silently. It writes back only
  // once the user has actually typed a number.
  const [text, setText] = useState(String(value))
  useEffect(() => setText(String(value)), [value])

  return (
    <input
      type="number"
      className="w-sm"
      value={text}
      onChange={(e) => {
        const raw = e.target.value
        setText(raw)
        if (raw === '') return
        const n = Number(raw)
        if (!Number.isNaN(n)) onChange(n)
      }}
      // Restore the real value on blur. Otherwise, clearing the box and saving without typing again
      // never changes value, so the effect above never re-runs and the box stays blank — the screen
      // says the field is empty while the stored value is still the old number.
      onBlur={() => setText(String(value))}
    />
  )
}

function Rules({ value, onChange }: { value: string | null; onChange: (v: string) => void }) {
  return (
    <textarea rows={2} className="w-lg"
      value={value ?? ''} onChange={(e) => onChange(e.target.value)} />
  )
}
