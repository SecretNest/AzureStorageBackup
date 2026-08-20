import { describe, expect, test } from 'vitest'

import type { BackupRun } from '../api/backupConfigs'
import { runTotals } from './runSummary'

function run(over: Partial<BackupRun> = {}): BackupRun {
  return {
    status: 'Completed',
    progress: null,
    version: 42,
    unreadableFiles: null,
    error: null,
    startedAt: null,
    completedAt: null,
    runId: 'abc123',
    pause: null,
    pausedByUser: false,
    suspendReason: null,
    newFiles: 0,
    modifiedFiles: 0,
    deletedFiles: 0,
    deletedBytes: 0,
    changedBytes: 0,
    uploadedBytes: 0,
    ...over,
  }
}

describe('runTotals', () => {
  test('lists new, modified and deleted counts alongside the byte figures', () => {
    expect(
      runTotals(
        run({
          newFiles: 2481,
          modifiedFiles: 130,
          deletedFiles: 5,
          deletedBytes: 3221225472,
          changedBytes: 5046586572,
          uploadedBytes: 851443712,
        }),
      ),
    ).toBe(
      '2,481 new, 130 modified, 5 deleted (3.000 GB) · 4.700 GB changed at source → 812.0 MB uploaded',
    )
  })

  // Five deleted files can be five empty stubs or five disk images. The size says which, and it belongs
  // next to the count rather than in the data segment — that segment tracks what went over the wire, and
  // deleted bytes went nowhere.
  test('sizes the deleted files where the count is', () => {
    expect(runTotals(run({ deletedFiles: 12, deletedBytes: 5046586572 }))).toBe(
      '12 deleted (4.700 GB)',
    )
  })

  test('leaves the size off when the deleted files weighed nothing', () => {
    expect(runTotals(run({ deletedFiles: 12, deletedBytes: 0 }))).toBe('12 deleted')
  })

  // An older backend sends the counts but not this size. Rendering "(0 B)" there would state something
  // about those files that nothing here knows.
  test('shows the count alone when the backend sent no size', () => {
    expect(runTotals(run({ deletedFiles: 12, deletedBytes: undefined }))).toBe('12 deleted')
  })

  test('drops the file items that are zero', () => {
    expect(runTotals(run({ newFiles: 3, changedBytes: 1024, uploadedBytes: 512 }))).toBe(
      '3 new · 1.0 KB changed at source → 512 B uploaded',
    )
  })

  // The round where dedup hit on everything: a great deal changed at source and not one byte was sent.
  // Dropping the data segment because uploaded is zero would hide exactly the figure worth reading.
  test('keeps the data segment when everything hit dedup', () => {
    expect(runTotals(run({ modifiedFiles: 12, changedBytes: 5046586572, uploadedBytes: 0 }))).toBe(
      '12 modified · 4.700 GB changed at source → 0 B uploaded',
    )
  })

  test('drops the data segment when nothing changed and nothing was uploaded', () => {
    expect(runTotals(run({ newFiles: 3 }))).toBe('3 new')
  })

  test('says so plainly when the round found nothing to do', () => {
    expect(runTotals(run())).toBe('no changes')
  })

  // Unreadable files are already spelled out in their own red line next to this one; repeating them
  // here would report the same files twice.
  test('leaves unreadable files to the line that already reports them', () => {
    expect(runTotals(run({ unreadableFiles: 7 }))).toBe('no changes')
  })

  // An older backend does not send these fields at all. "no changes" would be a lie there — nothing is
  // known about this run, which is not the same as knowing it changed nothing.
  test('reports nothing when the backend did not send the figures', () => {
    expect(
      runTotals(
        run({
          newFiles: undefined,
          modifiedFiles: undefined,
          deletedFiles: undefined,
          changedBytes: undefined,
          uploadedBytes: undefined,
        }),
      ),
    ).toBeNull()
  })
})
