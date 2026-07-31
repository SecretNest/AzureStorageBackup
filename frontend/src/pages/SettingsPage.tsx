import { useEffect, useState } from 'react'
import { settingsApi, type GlobalSettings } from '../api/settings'
import { StorageTier, tierLabels, retentionModeLabels } from '../api/backupConfigs'
import { Field } from '../components/Field'
import { AccountsSection } from './AccountsPage'
import { NotificationsSection } from './NotificationsPage'

const MB = 1024 * 1024

/// Settings 是几个区域的外壳。Accounts 排在**最前面**并且**不等全局设置加载完**：
/// 没有配过账户时用户会被直接带到这一页（见 App.tsx 里挑默认标签那段），第一眼就该看到它——
/// 让它排在下面、或者压在 "Loading…" 后面，挡住的恰恰是新用户唯一能做的那件事。
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
      {/* 登出从侧栏搬到这里：手机上底部栏只有四格，塞不下第五个入口。
          桌面端也一并搬过来——同一个功能摆两个位置，是后续最容易忘记同步的那种东西。 */}
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
      {/* 与新建备份表单同一句提示：规则的路径基准是各备份的 Local Root，不是主机上的完整路径。 */}
      <p className="text-muted">
        The rule lists below use gitignore syntax and match paths <strong>relative to each backup's local
        root</strong> — write <span className="mono">/Backup/</span>, not the full host path. A trailing{' '}
        <span className="mono">/</span> means "this directory and everything under it".
      </p>
      <Field label="Ignore rules">
        <Rules value={s.defaultIgnoreRules} onChange={(v) => set('defaultIgnoreRules', v)} />
      </Field>
      <Field label="Don't compress">
        <Rules value={s.defaultDontCompressRules} onChange={(v) => set('defaultDontCompressRules', v)} />
      </Field>
      <Field label="Don't group">
        <Rules value={s.defaultDontGroupRules} onChange={(v) => set('defaultDontGroupRules', v)} />
      </Field>
      <Field label="Pack across directories">
        <Rules value={s.defaultCrossDirGroupRules} onChange={(v) => set('defaultCrossDirGroupRules', v)} />
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
  // 输入框本地维护原始文本，而不是直接受控于 value：这样清空输入框时，界面上
  // 显示的是空白（而非被 value 立刻拉回原数字），但父状态仍保留上一个数值，
  // 不会把 Number('') === 0 悄悄写进去。只有用户真正键入了一个数字才回写。
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
      // 离开输入框时把空白拉回真实值。否则清空后不再键入就保存，value 从未改变，
      // 上面那个 useEffect 也就不会重跑，输入框会一直显示空白——屏幕说这项是空的，
      // 实际存的却还是原来的数字。
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
