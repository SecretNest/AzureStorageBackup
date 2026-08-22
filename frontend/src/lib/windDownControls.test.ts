import { describe, expect, test } from 'vitest'
import { windDownControls } from './windDownControls'

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
