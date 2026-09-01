import { useEffect, useState } from 'react'
import { settingsApi, sevenZipPriorityLabels, type GlobalSettings } from '../api/settings'
import { StorageTier, tierLabels, retentionModeLabels } from '../api/backupConfigs'
import { Field } from '../components/Field'
import { AccountsSection } from './AccountsPage'
import { NotificationsSection } from './NotificationsPage'
import { AboutSection } from './AboutPage'

const MB = 1024 * 1024

/// The five sub-pages. Settings had grown into one page several screens tall, where finding a knob meant scrolling past
/// every other knob; each of these is now a page you can read to the end of.
export type SettingsTab = 'accounts' | 'defaults' | 'performance' | 'notifications' | 'about'

const settingsTabs: { key: SettingsTab; label: string }[] = [
  // Accounts stays first: a user with no accounts configured lands on Settings (see the default-tab logic in App.tsx)
  // and this is the one thing they can actually do.
  { key: 'accounts', label: 'Accounts' },
  { key: 'defaults', label: 'Backup defaults' },
  { key: 'performance', label: 'Performance' },
  { key: 'notifications', label: 'Notifications' },
  { key: 'about', label: 'About' },
]

export function SettingsPage({
  tab,
  onTabChange,
  authRequired,
  onLogout,
}: {
  tab: SettingsTab
  onTabChange: (t: SettingsTab) => void
  authRequired?: boolean
  onLogout?: () => void
}) {
  // Backup defaults and Performance are two halves of ONE object: settingsApi.get/update serve the whole GlobalSettings, and
  // splitting the page does not split the endpoint. So the state is held here, above both, rather than in each sub-page.
  // Two independent copies would each save from their own snapshot, and whichever went last would silently overwrite the
  // other half with values it read before the first save. Sharing it also means edits made on one tab survive a switch to
  // the other and go up together.
  const settings = useGlobalSettings()

  return (
    <section>
      <div className="page-header">
        <h1>Settings</h1>
      </div>

      {/* A strip here rather than nested entries in the sidebar: on phones the sidebar is a fixed four-slot bottom bar,
          which has no room for five more. Scrolls horizontally when it does not fit. */}
      <nav className="subnav">
        {settingsTabs.map((t) => (
          <button
            key={t.key}
            type="button"
            onClick={() => onTabChange(t.key)}
            className={tab === t.key ? 'subnav-item subnav-item-active' : 'subnav-item'}
            aria-current={tab === t.key ? 'page' : undefined}
          >
            {t.label}
          </button>
        ))}
      </nav>

      {tab === 'accounts' && <AccountsSection />}
      {tab === 'defaults' && <BackupDefaults settings={settings} />}
      {tab === 'performance' && <PerformanceOptions settings={settings} />}
      {tab === 'notifications' && <NotificationsSection />}
      {tab === 'about' && <AboutSection authRequired={authRequired} onLogout={onLogout} />}
    </section>
  )
}

type SettingsState = ReturnType<typeof useGlobalSettings>

/// Load, edit and save the global settings object, shared by the two sub-pages that each show half of it.
function useGlobalSettings() {
  const [s, setS] = useState<GlobalSettings | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)
  // In-flight guard, same as NotificationsSection's `busy`: two overlapping updates each close over their
  // own snapshot of `s`, and whichever RESPONSE lands last wins setS — the reply to the older edit can
  // silently revert fields the newer request already stored.
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    settingsApi.get().then(setS).catch((e) => setError(e instanceof Error ? e.message : String(e)))
  }, [])

  const set = <K extends keyof GlobalSettings>(k: K, v: GlobalSettings[K]) => {
    // Any edit retracts "Saved." — otherwise it stays on screen next to fields that have since been changed and not
    // saved, which is exactly the wrong moment to be reassuring.
    setSaved(false)
    setS((cur) => (cur ? { ...cur, [k]: v } : cur))
  }

  const save = async () => {
    if (!s) return
    setError(null)
    setSaved(false)
    setBusy(true)
    try {
      // The response is deliberately NOT written back into `s` (same as NotificationsSection): the fields
      // stay editable while the request is in flight, and echoing the request's snapshot back over them
      // would silently revert an edit made mid-save — with "Saved." showing, so the loss goes unnoticed.
      await settingsApi.update(s)
      setSaved(true)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  return { s, set, save, busy, saved, error }
}

/// The Save row, rendered identically at the foot of both halves.
///
/// The button changes its own text rather than only greying out. On a loaded machine the round trip can take several
/// seconds, and a disabled button with unchanged text says nothing about whether the click registered — the reported
/// experience was pressing Save, seeing no change at all, and waiting for a "Saved." that took its time arriving.
function SaveBar({ settings }: { settings: SettingsState }) {
  return (
    <div className="row" style={{ marginTop: '1rem' }}>
      <button type="button" className="btn-primary" onClick={settings.save} disabled={settings.busy}>
        {settings.busy ? 'Saving…' : 'Save'}
      </button>
      {settings.saved && <span className="text-ok">Saved.</span>}
      {/* One object, one request: whichever of the two pages you press Save on writes both. Said out loud because the
          tabs make them look like separate forms with separate buttons. */}
      <span className="text-faint">Saves both Backup defaults and Performance.</span>
    </div>
  )
}

function BackupDefaults({ settings }: { settings: SettingsState }) {
  const { s, set, error } = settings
  if (!s) return <p>Loading…</p>

  return (
    <>
      <p className="text-muted">Defaults for new backups, and for any existing backup field set to Use default.</p>
      {error && <p className="text-danger">{error}</p>}

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

      <SaveBar settings={settings} />
    </>
  )
}

function PerformanceOptions({ settings }: { settings: SettingsState }) {
  const { s, set, error } = settings
  if (!s) return <p>Loading…</p>

  return (
    <>
      <p className="text-muted">One shared setting each — these apply to the whole server, not to any single backup.</p>
      {error && <p className="text-danger">{error}</p>}

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
      {/* The trade is the operator's: strict protects the disk absolutely; fair-share keeps every run
          moving when one stages a family bigger than the whole limit (a single archive cannot be split),
          at the price of a larger possible overshoot when every run handles huge files at once. */}
      <Field label="Staging fair share">
        <input
          type="checkbox"
          checked={s.stagingFairShare}
          onChange={(e) => set('stagingFairShare', e.target.checked)}
        />
        {' '}Reserve 20% of the limit as per-run guarantees (split evenly) and share the other 80%
        first-come, so one oversized archive cannot completely starve concurrent runs. Off = the strict
        ceiling: a full staging pool blocks every run until it drains.
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

      <SaveBar settings={settings} />
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
