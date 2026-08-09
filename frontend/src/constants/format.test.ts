import { describe, expect, it } from 'vitest'
import { formatDuration } from './format'

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
