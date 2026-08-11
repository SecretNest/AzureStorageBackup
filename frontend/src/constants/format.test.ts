import { describe, expect, it } from 'vitest'
import { formatDuration, formatLocalDateTime, formatUtcOffset } from './format'

describe('formatLocalDateTime', () => {
  it('pads every field to a fixed width so a log column lines up', () => {
    // Built from local parts, so the expectation holds in whatever zone the suite runs in.
    const local = new Date(2026, 0, 5, 3, 7, 9)
    expect(formatLocalDateTime(local.toISOString())).toBe('2026-01-05 03:07:09')
  })

  it('shows the reader wall clock, not the UTC one the backend stores', () => {
    const iso = '2026-08-11T12:00:00.000Z'
    // Reading the printed text back *as if it were UTC* must land exactly one local offset away from
    // the instant — which is only true when the text was written in local time. True in any zone.
    const printedAsUtc = Date.parse(`${formatLocalDateTime(iso).replace(' ', 'T')}Z`)
    expect(printedAsUtc - Date.parse(iso)).toBe(-new Date(iso).getTimezoneOffset() * 60_000)
  })

  it('hands back anything it cannot parse instead of printing "Invalid Date"', () => {
    expect(formatLocalDateTime('not a time')).toBe('not a time')
  })
})

describe('formatUtcOffset', () => {
  // getTimezoneOffset counts minutes behind UTC, so the sign on screen is the opposite of its own.
  const at = (offsetMinutes: number) => ({ getTimezoneOffset: () => offsetMinutes }) as Date

  it('flips the sign and pads both halves', () => {
    expect(formatUtcOffset(at(-480))).toBe('UTC+08:00') // Asia/Shanghai
    expect(formatUtcOffset(at(300))).toBe('UTC-05:00') // America/New_York
    expect(formatUtcOffset(at(-330))).toBe('UTC+05:30') // Asia/Kolkata, half-hour zone
  })

  it('says UTC+00:00 outright when the browser really is on UTC', () => {
    expect(formatUtcOffset(at(0))).toBe('UTC+00:00')
  })
})

describe('formatDuration', () => {
  it('gives every segment its unit', () => {
    expect(formatDuration(45)).toBe('45s')
    expect(formatDuration(90)).toBe('1m 30s')
    expect(formatDuration(3600)).toBe('1h')
    expect(formatDuration(3900)).toBe('1h 5m')
  })

  it('says days when it is more than a day', () => {
    // This is the exact shape of that bug: .NET serialises it as "3.05:20:00", and splitting on '.'
    // leaves "3", so the screen read "~3 left" with no unit — days or hours, unknowable.
    expect(formatDuration(3 * 86400 + 5 * 3600 + 20 * 60)).toBe('3d 5h')
    expect(formatDuration(3 * 86400)).toBe('3d')
  })

  it('shows two units only — nobody cares about the seconds when three days are left', () => {
    expect(formatDuration(3 * 86400 + 5 * 3600 + 20 * 60 + 11)).toBe('3d 5h')
    expect(formatDuration(5 * 3600 + 20 * 60 + 11)).toBe('5h 20m')
  })

  it('folds zero and negatives to 0s rather than printing something strange', () => {
    expect(formatDuration(0)).toBe('0s')
    expect(formatDuration(-5)).toBe('0s')
  })
})
