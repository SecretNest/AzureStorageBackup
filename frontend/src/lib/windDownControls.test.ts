import { describe, expect, test } from 'vitest'
import { windDownControls, windDownFromServer } from './windDownControls'

describe('windDownFromServer', () => {
  /**
   * The bug this exists for: the wind-down was known only to the tab that had pressed the button. Suspend waits
   * out the file in hand, so the row sits in that state for minutes — switch away and back and it came up as an
   * ordinary running backup, every button live again, nothing said about the stop already under way.
   */
  test('each rung of the server ladder maps to its own', () => {
    expect(windDownFromServer('Suspend')).toBe('suspend')
    expect(windDownFromServer('FinishCurrentFiles')).toBe('finish')
    expect(windDownFromServer('StopNow')).toBe('now')
  })

  test('a run nobody has stopped is not winding down', () => {
    expect(windDownFromServer('None')).toBeUndefined()
    expect(windDownFromServer(null)).toBeUndefined()
    expect(windDownFromServer(undefined)).toBeUndefined()
  })

  // A newer server naming a rung this build has never heard of costs a row its label, not the page.
  test('an unknown rung reads as no wind-down', () =>
    expect(windDownFromServer('Annihilate')).toBeUndefined())
})

describe('windDownControls', () => {
  test('nothing winding down leaves every control live', () => {
    const c = windDownControls(undefined)
    expect(c.canStop).toBe(true)
    expect(c.canActOnGate).toBe(true)
    expect(c.stopLabel).toBe('Stop')
    expect(c.suspendLabel).toBe('Suspend')
  })

  /**
   * The bug this function was extracted for. Suspend waits for the file in hand and every volume of it,
   * with the run still reporting Running, so on a slow uplink it is a wait of minutes to tens of minutes.
   * Disabling Stop for that whole stretch removed the one correct move — escalating — and left restarting
   * the container as the only way out. The backend never refused it: StopNow (3) > Suspend (1), and
   * `RequestStop`'s CAS loop takes any strictly greater kind.
   */
  test('Stop stays live while a suspend winds down', () => {
    const c = windDownControls('suspend')
    expect(c.canStop).toBe(true)
    expect(c.stopLabel).toBe('Stop')
  })

  /**
   * The same defect one rung up, and the rung where it bites hardest: Finish current files is chosen
   * precisely by operators who then discover what "current files" was holding. StopNow (3) >
   * FinishCurrentFiles (2), so this escalation is as valid as the one above — the old marker collapsed
   * both stop modes into one value and could not tell them apart.
   */
  test('Stop stays live while finish-current-files winds down', () => {
    const c = windDownControls('finish')
    expect(c.canStop).toBe(true)
    expect(c.stopLabel).toBe('Stop')
  })

  test('Stop goes quiet only at the top of the ladder', () => {
    const c = windDownControls('now')
    expect(c.canStop).toBe(false)
    expect(c.stopLabel).toBe('Stopping…')
  })

  /**
   * The half of the old behaviour that was right and stays. All of Retry now / Resume / Pause reach into
   * the pause gate, which any stop has already downgraded past the point of holding anyone; Suspend is a
   * step down the ladder, which RequestStop ignores outright.
   */
  test.each(['suspend', 'finish', 'now'] as const)(
    'the gate controls stay disabled throughout a %s wind-down',
    (kind) => {
      expect(windDownControls(kind).canActOnGate).toBe(false)
    })

  test('the Suspend button says so only while it is the suspend that is running', () => {
    expect(windDownControls('suspend').suspendLabel).toBe('Suspending…')
    expect(windDownControls('finish').suspendLabel).toBe('Suspend')
    expect(windDownControls('now').suspendLabel).toBe('Suspend')
  })
})

describe('windDownControls while wrapping up', () => {
  /**
   * From the index write on, every upload is done and nothing left in the run consults the pause gate. Pause
   * used to answer success and show "Paused" over a run that went on to Completed; Suspend used to hang for
   * its cap and hand back a Completed run labelled "Suspending…". At a few million entries the index write is
   * minutes, so this is a whole stage, not a race window. Stop stays: it still skips the cleanup.
   */
  test('the gate controls go quiet, Stop stays live', () => {
    const c = windDownControls(undefined, true)
    expect(c.canActOnGate).toBe(false)
    expect(c.canStop).toBe(true)
    expect(c.stopLabel).toBe('Stop')
    expect(c.suspendLabel).toBe('Suspend')
  })

  test('the disabled buttons say why', () => {
    expect(windDownControls(undefined, true).gateHint).toMatch(/writing its index/)
    expect(windDownControls(undefined, false).gateHint).toBeUndefined()
  })

  // A wind-down already under way is not overwritten by the wrap-up: the labels keep telling the truth about it.
  test('a suspend already under way keeps its label', () => {
    expect(windDownControls('suspend', true).suspendLabel).toBe('Suspending…')
    expect(windDownControls('now', true).canStop).toBe(false)
  })
})
