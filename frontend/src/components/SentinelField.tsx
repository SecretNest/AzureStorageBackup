import { useEffect, useState } from 'react'
import { browseApi } from '../api/browse'
import { ApiError } from '../api/client'

/**
 * The live verdict shown under the sentinel field.
 *
 * The setting is unusual in that its value is *supposed* to be absent much of the time — that is the whole
 * situation it exists for — so the form cannot refuse to save an absent path, and cannot colour it as an
 * error either. What it can do is say which of the two it is looking at right now, because a typo and an
 * unmounted disk are indistinguishable otherwise, and the difference only surfaces days later as a backup
 * that quietly skipped every night.
 *
 * The states are deliberately worded as facts rather than judgements: "not there right now" is not a
 * problem to fix if the disk is simply not mounted at this moment, and the operator is the only one who
 * knows which it is.
 */
type Probe =
  | { state: 'idle' }
  | { state: 'checking' }
  | { state: 'found'; kind: 'file' | 'directory' }
  | { state: 'absent' }
  | { state: 'refused'; message: string }

export function SentinelField({
  value,
  localRoot,
  onChange,
  onBrowse,
}: {
  value: string
  /** This backup's local root: the sentinel has to live under it, and it is where the picker starts. */
  localRoot: string
  onChange: (next: string) => void
  onBrowse: () => void
}) {
  const [probe, setProbe] = useState<Probe>({ state: 'idle' })
  // What is actually probed: the sentinel when there is one, the local root otherwise. It mirrors the
  // backend's SentinelGate exactly — with no sentinel configured the root stands in as one — so the line
  // under the field describes the check that will really run, not a check the form invented.
  const effective = value.trim() || localRoot.trim()

  useEffect(() => {
    if (!effective) {
      setProbe({ state: 'idle' })
      return
    }
    // Debounced, and the in-flight request is cancelled on every keystroke: typing a path a character at
    // a time would otherwise fire a probe per character, and the answers can arrive out of order — the
    // stale one landing last would describe a path the operator has already finished editing.
    const controller = new AbortController()
    setProbe({ state: 'checking' })
    const timer = setTimeout(() => {
      browseApi
        .exists(effective, controller.signal)
        .then((r) =>
          setProbe(r.exists && r.kind ? { state: 'found', kind: r.kind } : { state: 'absent' }),
        )
        .catch((e) => {
          if (controller.signal.aborted) return
          // 409 (outside the configured root) and 400 are real refusals worth showing verbatim — they
          // say the path can never work, which is different from "not there at the moment".
          setProbe({
            state: 'refused',
            message: e instanceof ApiError ? e.message : e instanceof Error ? e.message : String(e),
          })
        })
    }, 400)
    return () => {
      clearTimeout(timer)
      controller.abort()
    }
  }, [effective])

  return (
    <>
      <div className="row" style={{ gap: 'var(--sp-1)' }}>
        <input
          className="w-lg mono"
          placeholder={localRoot ? `${localRoot.replace(/\/+$/, '')}/.mounted` : '/data/photos/.mounted'}
          value={value}
          onChange={(e) => onChange(e.target.value)}
        />
        <button type="button" onClick={onBrowse} disabled={!localRoot.trim()}>
          Browse
        </button>
      </div>
      <div className="text-sm" style={{ marginTop: 'var(--sp-1)' }}>
        {verdict(probe, value.trim().length > 0)}
      </div>
    </>
  )
}

function verdict(probe: Probe, configured: boolean) {
  const subject = configured ? 'Sentinel' : 'Local root'
  switch (probe.state) {
    case 'idle':
      return <span className="text-faint">Leave empty to use the local root itself.</span>
    case 'checking':
      return <span className="text-faint">Checking…</span>
    case 'found':
      return <span className="text-ok">{subject} found ({probe.kind}) — backups will run.</span>
    case 'absent':
      // Warn, not danger. Nothing is wrong: this is exactly what the setting is for, and it is very
      // likely the state the operator is in while configuring it.
      return (
        <span className="text-warn">
          {subject} is not there right now — backups will be skipped until it appears.
        </span>
      )
    case 'refused':
      return <span className="text-danger">{probe.message}</span>
  }
}
