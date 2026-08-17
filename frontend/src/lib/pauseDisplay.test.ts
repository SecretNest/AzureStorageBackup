import { describe, expect, test } from 'vitest'
import { pauseDisplay } from './pauseDisplay'
import { PauseSource, type PauseInfo } from '../api/backupConfigs'

// PauseSource is pinned as a number on the wire (see api/backupConfigs.ts, commit 721b923) — there is no
// string union to import, so the fixture below builds PauseInfo with the numeric values directly.
const pause = (source: number, reason = 'network down'): PauseInfo => ({
  reason,
  since: '2026-08-17T10:00:00Z',
  nextRetryAt: null,
  failures: 1,
  source,
})

describe('pauseDisplay', () => {
  test('a run that is not paused renders nothing', () => {
    expect(pauseDisplay(null, false)).toBeNull()
  })

  /**
   * The two reasons offer different actions, which is the whole point of carrying the source: a
   * transient-error pause is waiting on a timer the user can skip, and a user pause is waiting on the
   * user. Offering "Retry now" for a pause nobody is retrying would be nonsense.
   */
  test('a user pause offers Resume and not Retry now', () => {
    const d = pauseDisplay(pause(PauseSource.User), true)!
    expect(d.canResume).toBe(true)
    expect(d.canRetryNow).toBe(false)
    expect(d.label).toBe('Paused')
  })

  test('a transient-error pause offers Retry now and not Resume', () => {
    const d = pauseDisplay(pause(PauseSource.TransientError), false)!
    expect(d.canResume).toBe(false)
    expect(d.canRetryNow).toBe(true)
    expect(d.label).toContain('network down')
  })

  /**
   * An older backend sends no source. Treating that as a user pause would put a Resume button on a
   * run nobody paused; the transient-error reading is the safe default because it is what every pause
   * meant before this field existed.
   */
  test('a pause with no source reads as a transient error', () => {
    const d = pauseDisplay({ ...pause(PauseSource.TransientError), source: undefined } as unknown as PauseInfo, false)!
    expect(d.canRetryNow).toBe(true)
    expect(d.canResume).toBe(false)
  })

  /**
   * The composed case: the operator pressed Pause while a transient-error backoff already held the gate.
   * `source` keeps reporting TransientError — with its own reason and countdown — until that backoff's
   * own timer fires, up to one steady interval (five minutes by default; see PauseGate.ReleaseLocked).
   * `pausedByUser` is what says the operator's hold is standing regardless. Getting this wrong renders a
   * run the operator just paused as "stuck, retrying in 4:37" with a Retry-now button that looks like it
   * does nothing — the exact bug this field exists to prevent.
   */
  test('a user pause on top of a live backoff offers Resume, not Retry now, and says both', () => {
    const d = pauseDisplay(pause(PauseSource.TransientError, 'network down'), true)!
    expect(d.canResume).toBe(true)
    expect(d.canRetryNow).toBe(false)
    expect(d.label).toContain('Paused')
    expect(d.label).toContain('network down')
  })

  /**
   * Once that backoff's own timer fires, PauseGate.ReleaseLocked relabels `source` to User (see the gate's
   * own comment) without releasing anyone — the composed window is over, and the plain user-pause label
   * applies again with nothing about the old error hanging off it.
   */
  test('once the backoff timer relabels the source to User, the composed wording drops away', () => {
    const d = pauseDisplay(pause(PauseSource.User, 'Paused by the user.'), true)!
    expect(d.label).toBe('Paused')
  })
})
