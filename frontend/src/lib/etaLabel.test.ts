import { describe, expect, test } from 'vitest'

import { etaLabel } from './etaLabel'

describe('etaLabel', () => {
  test('states the outstanding size alongside the time', () => {
    expect(etaLabel(45_780, 214_748_364_800)).toBe('200.000 GB (~12h 43m) left')
  })

  test('falls back to the bare time rather than empty parentheses when nothing is outstanding', () => {
    expect(etaLabel(5, 0)).toBe('~5s left')
  })

  test('an older backend that sends no remaining figure still gets a time', () => {
    expect(etaLabel(90, undefined)).toBe('~1m 30s left')
    expect(etaLabel(90, null)).toBe('~1m 30s left')
  })

  // No estimate is a state the backend reports on purpose while the diff and the upload run together:
  // the upload's denominator is still growing, so any number here would later go backwards.
  test('no estimate produces no label', () => {
    expect(etaLabel(null, 214_748_364_800)).toBeNull()
    expect(etaLabel(undefined, 214_748_364_800)).toBeNull()
  })

  // Zero seconds is a real reading at the tail of a run, distinct from "no estimate" — it must not be
  // swallowed by a truthiness check on the seconds.
  test('zero seconds is an estimate, not a missing one', () => {
    expect(etaLabel(0, 1024)).toBe('1.0 KB (~0s) left')
  })
})
