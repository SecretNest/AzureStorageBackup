import { describe, expect, test } from 'vitest'

import type { CheckReport } from '../api/backupConfigs'
import { orphanSummary } from './checkSummary'

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
