import { describe, expect, test } from 'vitest'
import { showsInterruptedNotice } from './interruptedNotice'
import type { BackupRun } from '../api/backupConfigs'

const run = (status: BackupRun['status']) => ({ status }) as BackupRun

describe('showsInterruptedNotice', () => {
  test('no journal on disk means nothing to offer', () => {
    expect(showsInterruptedNotice(undefined, 0)).toBe(false)
    expect(showsInterruptedNotice(run('Failed'), 0)).toBe(false)
  })

  test('no run in memory and a journal on disk: the original case', () => {
    expect(showsInterruptedNotice(undefined, 1)).toBe(true)
  })

  /**
   * The bug this function exists for. A failed run stays in memory as a terminal state, and RunStatus
   * renders it as a single line of red text with no buttons at all — while the notice that carries Resume
   * was gated on there being no run at all. Between the two, the operator lost every way to pick the run
   * back up and had to reload the page to discover the journal was still there.
   */
  test('a failed run still offers to resume', () => {
    expect(showsInterruptedNotice(run('Failed'), 1)).toBe(true)
  })

  test('a stopped run does too — the journal outlives the stop', () => {
    expect(showsInterruptedNotice(run('Canceled'), 1)).toBe(true)
  })

  /**
   * Suspended is deliberately excluded: RunStatus already renders Resume and Discard for it, and showing
   * the notice as well would put two Resume buttons on one row saying the same thing.
   */
  test('a suspended run does not, because its own row already carries Resume', () => {
    expect(showsInterruptedNotice(run('Suspended'), 1)).toBe(false)
  })

  test('a live run does not — it has not been interrupted yet', () => {
    expect(showsInterruptedNotice(run('Running'), 1)).toBe(false)
  })

  /**
   * A run that just succeeded must not be told it was interrupted. Any journal still listed here belongs
   * to another configuration sharing the container, and offering to resume it from this row would be a lie.
   */
  test('a completed run does not', () => {
    expect(showsInterruptedNotice(run('Completed'), 1)).toBe(false)
  })
})
