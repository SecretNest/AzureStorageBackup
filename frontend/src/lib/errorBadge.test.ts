import { describe, expect, test } from 'vitest'
import { errorBadgeLabel } from './errorBadge'

const now = new Date('2026-08-17T12:00:00Z')

// `now` minus an offset, as an ISO string — the shape `LastErrorAt` actually arrives in over the wire.
const secondsAgo = (seconds: number) => new Date(now.getTime() - seconds * 1000).toISOString()

describe('errorBadgeLabel', () => {
  test('no stored error renders nothing', () => {
    expect(errorBadgeLabel(null, null, now)).toBeNull()
  })

  test('an error minutes old says so', () => {
    expect(errorBadgeLabel('SQLite database is locked', secondsAgo(5 * 60 + 30), now)).toBe('Error — 5m 30s ago')
  })

  test('an error days old says so', () => {
    expect(errorBadgeLabel('Azure blob conflict', secondsAgo(3 * 86400 + 5 * 3600), now)).toBe('Error — 3d 5h ago')
  })

  /**
   * The case that decides the whole design. A row written before `LastErrorAt` existed, or a config
   * synced from a backend that predates it, carries a message with no timestamp. Dropping the badge
   * because one of its two fields is missing would hide the fact that matters most — something
   * failed — to preserve a fact that matters less — when. So it still renders, just without the tense.
   */
  test('an error with no timestamp still renders, just without the tense', () => {
    expect(errorBadgeLabel('legacy failure, written before LastErrorAt existed', null, now)).toBe('Error')
  })
})
