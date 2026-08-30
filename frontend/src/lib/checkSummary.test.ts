import { describe, expect, test } from 'vitest'

import type { CheckReport, FileFinding } from '../api/backupConfigs'
import { CloudState, LocalState } from '../api/backupConfigs'
import { orphanSummary, repairabilitySummary, resolutionSummary } from './checkSummary'

function report(over: Partial<CheckReport> = {}): CheckReport {
  return {
    version: 5,
    findings: [],
    metadataIssue: null,
    ok: true,
    missingRefs: [],
    corruptedPaths: [],
    repairablePaths: [],
    orphanBlobs: [],
    orphansChecked: false,
    orphanScanIssue: null,
    ...over,
  }
}

describe('orphanSummary', () => {
  /**
   * The regression this exists for. Ticking "Detect unreferenced blobs" and getting a clean container
   * used to render nothing at all, so the finished row read exactly like a run that never scanned —
   * and the only way to learn the scan had happened was to reopen the dialog. The operation log had
   * been saying "; 0 unreferenced blob(s)" the whole time, which is the criterion copied here:
   * the scan **ran**, not the scan **found something**.
   */
  test('a clean scan says so rather than saying nothing', () => {
    expect(orphanSummary(report({ orphansChecked: true }))).toBe(' · no unreferenced blobs')
  })

  test('a scan that found blobs names the count and what to do about it', () => {
    expect(orphanSummary(report({ orphansChecked: true, orphanBlobs: ['data/a', 'data/b'] }))).toBe(
      ' · 2 unreferenced blob(s), reclaimable by repair',
    )
  })

  /** Nobody ticked the box: the axis was never asked about, so it must not be reported on either way. */
  test('an unasked scan stays silent', () => {
    expect(orphanSummary(report())).toBe('')
  })

  /**
   * Asked for and abandoned is its own third state, and it outranks both: reporting "no unreferenced
   * blobs" for a scan that never completed would be a false all-clear on the one axis a repair acts on.
   */
  test('an abandoned scan outranks the count', () => {
    expect(
      orphanSummary(
        report({ orphansChecked: false, orphanScanIssue: 'reference set incomplete', orphanBlobs: [] }),
      ),
    ).toBe(' · unreferenced-blob scan abandoned')
  })
})

describe('resolutionSummary', () => {
  /**
   * §39b: a settled check (clean, repaired, or dropped) leaves no findings table, so this one line is
   * the only trace that a check ran — "没地方看到这个没有错误的报告" is exactly what it prevents. Pending and
   * "no report at all" render nothing here: the dialog shows the live findings table or the start view.
   */
  test('a pending report shows no history line — the findings table does that job', () => {
    expect(resolutionSummary('Pending', 0, 'Aug 30, 2026, 1:00 PM')).toBeNull()
  })

  test('no report at all shows no history line', () => {
    expect(resolutionSummary(null, 0, null)).toBeNull()
  })

  test('a clean report says everything was OK, with the time it was checked', () => {
    expect(resolutionSummary('Clean', 0, 'Aug 30, 2026, 1:00 PM')).toBe(
      'The last check (Aug 30, 2026, 1:00 PM) found everything OK. You can start a new check to replace this.',
    )
  })

  /** A missing timestamp (older row) must not print "(null)" — the clause is simply dropped. */
  test('a clean report without a timestamp drops the time clause', () => {
    expect(resolutionSummary('Clean', 0, null)).toBe(
      'The last check found everything OK. You can start a new check to replace this.',
    )
  })

  test('a repaired report says the problems were all fixed', () => {
    expect(resolutionSummary('Repaired', 0, 'Aug 30, 2026, 1:00 PM')).toBe(
      'The last check found problems and they have all been repaired (checked Aug 30, 2026, 1:00 PM). ' +
        'You can start a new check to replace this.',
    )
  })

  /** Dropped keeps the count of what was left marked — that is the whole reason history is preserved. */
  test('a dropped report names how many files were left unrepaired', () => {
    expect(resolutionSummary('Dropped', 4, 'Aug 30, 2026, 1:00 PM')).toBe(
      'The last report was dropped — 4 file(s) left unrepaired and still marked; the next backup heals ' +
        'any whose content is unchanged. You can start a new check to replace this.',
    )
  })
})

describe('repairabilitySummary', () => {
  const finding = (over: Partial<FileFinding> = {}): FileFinding => ({
    path: 'a.bin',
    ref: 'data/x',
    cloud: CloudState.MissingOrBad,
    local: LocalState.NotChecked,
    repairable: false,
    length: 0,
    unreadableAt: null,
    ...over,
  })

  /**
   * The regression this exists for. A check run without a content-level local check reported
   * "0 repairable" — a verdict where there was only an unanswered question — and the user read it as
   * "repair cannot help", when repair is exactly what would have hashed the affected files and fixed
   * the recoverable ones.
   */
  test('problems whose local side was never checked read as not assessed, not as unrepairable', () => {
    expect(repairabilitySummary(report({ findings: [finding(), finding({ path: 'b.bin' })] }))).toBe(
      '2 problem(s), local repairability not assessed — repair will hash just the affected files',
    )
  })

  test('a content-checked run counts the repairable', () => {
    expect(
      repairabilitySummary(
        report({
          findings: [
            finding({ local: LocalState.Ok, repairable: true }),
            finding({ path: 'b.bin', local: LocalState.Changed }),
          ],
          repairablePaths: ['a.bin'],
        }),
      ),
    ).toBe('2 problem(s), 1 repairable from local')
  })

  /** A sentinel demotion or partial pass leaves a mix; the unassessed must not fold into "no". */
  test('a mixed run states the unassessed separately', () => {
    expect(
      repairabilitySummary(
        report({
          findings: [finding({ local: LocalState.Ok, repairable: true }), finding({ path: 'b.bin' })],
          repairablePaths: ['a.bin'],
        }),
      ),
    ).toBe('2 problem(s), 1 repairable from local, 1 not assessed')
  })
})
