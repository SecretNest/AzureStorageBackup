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
  // The other side of the same pin. The backend has PauseSource as explicit numerics and a case asserting the
  // JSON it produces; this mirror is hand-written, so nothing but a literal here keeps the two from drifting.
  // Every other test in this file goes through the constant and would follow a renumbering in silence.
  test('the PauseSource mirror still matches the numbers the backend sends', () => {
    expect(PauseSource.TransientError).toBe(0)
    expect(PauseSource.User).toBe(1)
  })

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

  /**
   * The hold is up from the button press, but the volumes on the wire finish first.
   * Until the backend says the last of them has landed, the row says so — "Paused" over a run visibly still
   * uploading read as the button having done nothing. Resume stays on offer: lifting a hold that has not
   * taken effect is as safe as lifting one that has.
   */
  test('a hold that has not taken effect yet reads as Pausing, with Resume still on offer', () => {
    const d = pauseDisplay(pause(PauseSource.User, 'Paused by the user.'), true, false)!
    expect(d.label).toBe('Pausing…')
    expect(d.canResume).toBe(true)
    expect(d.canRetryNow).toBe(false)
  })

  test('once settled, the same hold reads as Paused', () => {
    expect(pauseDisplay(pause(PauseSource.User, 'Paused by the user.'), true, true)!.label).toBe('Paused')
  })

  /** The composed case keeps both facts while the hold is still taking effect. */
  test('a hold taking effect on top of a live backoff says Pausing and the error', () => {
    const d = pauseDisplay(pause(PauseSource.TransientError, 'network down'), true, false)!
    expect(d.label).toContain('Pausing…')
    expect(d.label).toContain('network down')
    expect(d.canResume).toBe(true)
  })

  /**
   * A backend older than `pauseSettled` sends nothing, and the page passes `?? true`: that backend's pause
   * reads "Paused" the moment the hold is up, which is the only reading it ever had. Settled-by-default is
   * what keeps an old backend from showing "Pausing…" forever.
   */
  test('with no settled reading the hold reads as Paused, as it did before the field existed', () => {
    expect(pauseDisplay(pause(PauseSource.User, 'Paused by the user.'), true)!.label).toBe('Paused')
  })

  /** A transient-error pause nobody pressed is never "pausing": nothing about it is waiting on the operator. */
  test('settled does not touch the transient-error wording', () => {
    const d = pauseDisplay(pause(PauseSource.TransientError, 'network down'), false, false)!
    expect(d.label).toBe('Paused — network down')
    expect(d.canRetryNow).toBe(true)
  })
})
