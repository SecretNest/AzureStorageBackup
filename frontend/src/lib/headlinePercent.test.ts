import { describe, expect, test } from 'vitest'

import { BackupStage, type StageProgress } from '../api/backupConfigs'
import { headlinePercent } from './headlinePercent'

function detail(over: Partial<StageProgress>): StageProgress {
  return { stage: 'Uploading', percent: null, workPercent: null, ...over } as StageProgress
}

describe('headlinePercent', () => {
  test('a stage with its own percentage uses it, bytes before items', () => {
    expect(headlinePercent(BackupStage.Uploading, 100, [detail({ workPercent: 31, percent: 75 })])).toBe(31)
    expect(headlinePercent(BackupStage.Diffing, 0, [detail({ stage: 'Diffing', percent: 12 })])).toBe(12)
    expect(headlinePercent(BackupStage.UpdatingCatalog, 100, [detail({ stage: 'UpdatingCatalog', workPercent: 46 })])).toBe(46)
  })

  test('the upload stage falls back to the run-level item count when its detail has none yet', () => {
    expect(headlinePercent(BackupStage.Uploading, 37, [])).toBe(37)
    expect(headlinePercent(BackupStage.Uploading, 37, [detail({})])).toBe(37)
  })

  test('the wrap-up stages never borrow the upload count: it is N of N by then and reads as done', () => {
    // Writing index opened at 100% off this fallback and fell to 0% when its own tracker planned the first
    // volume; the catalog update (then "Finalizing") stood at 100% through a whole import.
    expect(headlinePercent(BackupStage.WritingIndex, 100, [])).toBeNull()
    expect(headlinePercent(BackupStage.WritingIndex, 100, [detail({ stage: 'WritingIndex' })])).toBeNull()
    expect(headlinePercent(BackupStage.UpdatingCatalog, 100, [])).toBeNull()
    expect(headlinePercent(BackupStage.CleaningUp, 100, [])).toBeNull()
    // And the index tracker's own 0% is its own, shown as such.
    expect(headlinePercent(BackupStage.WritingIndex, 100, [detail({ stage: 'WritingIndex', percent: 0 })])).toBe(0)
  })

  test('the stages before the upload show nothing without a detail: the run count is 0 there, not 0%', () => {
    expect(headlinePercent(BackupStage.Scanning, 0, [])).toBeNull()
    expect(headlinePercent(BackupStage.Diffing, 0, [])).toBeNull()
  })
})
