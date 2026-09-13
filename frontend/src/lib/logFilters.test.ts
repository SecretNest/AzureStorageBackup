import { describe, expect, test } from 'vitest'

import { formatLocalDateTime } from '../constants/format'
import {
  datetimeLocalValue,
  incompleteTimeNotice,
  initialLogFilters,
  noLogFilters,
  timeFieldHint,
  toForRefresh,
} from './logFilters'

describe('datetimeLocalValue', () => {
  test('is the browser wall clock, minute precision, in the shape the control takes', () => {
    // Constructed from local parts, so the expectation holds in any timezone the suite runs in.
    const at = new Date(2026, 8, 13, 19, 30, 45)
    expect(datetimeLocalValue(at)).toBe('2026-09-13T19:30')
  })

  test('pads every field, so a one-digit month or hour still parses', () => {
    expect(datetimeLocalValue(new Date(2026, 0, 2, 3, 4))).toBe('2026-01-02T03:04')
  })

  test('round-trips: the value the control takes is read back as the same wall clock', () => {
    const at = new Date(2026, 8, 13, 19, 30)
    expect(new Date(datetimeLocalValue(at)).getTime()).toBe(at.getTime())
  })
})

describe('the filters a page starts and resets with', () => {
  test('a fresh page pins To to now and leaves the rest blank', () => {
    const now = new Date(2026, 8, 13, 19, 30)
    expect(initialLogFilters(now)).toEqual({ minLevel: '', source: '', from: '', to: '2026-09-13T19:30' })
  })

  test('clearing empties every control, To included — that is the way back to the whole list', () => {
    expect(noLogFilters).toEqual({ minLevel: '', source: '', from: '', to: '' })
  })
})

describe('toForRefresh', () => {
  test('follows the clock while the box still holds what the page put there', () => {
    const opened = '2026-09-13T09:00'
    const now = new Date(2026, 8, 13, 19, 30)
    expect(toForRefresh(opened, false, now)).toBe('2026-09-13T19:30')
  })

  test('leaves a time the operator typed alone', () => {
    const now = new Date(2026, 8, 13, 19, 30)
    expect(toForRefresh('2026-09-01T00:00', true, now)).toBe('2026-09-01T00:00')
  })

  test('leaves a cleared box cleared: refresh must not put a bound back after Clear filters', () => {
    expect(toForRefresh('', true, new Date(2026, 8, 13, 19, 30))).toBe('')
  })
})

describe('timeFieldHint', () => {
  test('echoes the value in the format the table below uses, which is where the 12/24-hour clash shows', () => {
    // The control renders this as "07:30 PM" for an en-US reader; the table writes 19:30.
    const hint = timeFieldHint('2026-09-13T19:30', false)!
    expect(hint.incomplete).toBe(false)
    expect(hint.text).toBe(formatLocalDateTime('2026-09-13T19:30'))
    expect(hint.text).toContain('19:30')
  })

  test('a half-filled box says so — its value is the empty string, which reads as no filter at all', () => {
    const hint = timeFieldHint('', true)!
    expect(hint.incomplete).toBe(true)
    expect(hint.text).toContain('incomplete')
  })

  test('badInput wins over a value, since the control reports the last complete one it held', () => {
    expect(timeFieldHint('2026-09-13T19:30', true)!.incomplete).toBe(true)
  })

  test('an empty box says nothing at all', () => {
    expect(timeFieldHint('', false)).toBeNull()
  })
})

describe('incompleteTimeNotice', () => {
  test('names the box that is half filled', () => {
    expect(incompleteTimeNotice(true, false)).toContain('From')
    expect(incompleteTimeNotice(true, false)).not.toContain('To time')
    expect(incompleteTimeNotice(false, true)).toContain('To')
  })

  test('names both when both are', () => {
    expect(incompleteTimeNotice(true, true)).toContain('From and To')
  })

  test('says nothing when both boxes are readable', () => {
    expect(incompleteTimeNotice(false, false)).toBeNull()
  })

  test('says how to get out of it, since clearing the box is the non-obvious half', () => {
    expect(incompleteTimeNotice(false, true)).toContain('clear')
  })
})
