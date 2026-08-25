import { describe, expect, it } from 'vitest'
import type { BackupRun, CheckReport } from '../api/backupConfigs'
import { checkLocalSkipNotice, runSkipNotice } from './sentinelNotice'

const run = (over: Partial<BackupRun> = {}): BackupRun =>
  ({ status: 'Skipped', progress: null, version: null, error: null, ...over }) as BackupRun

const report = (over: Partial<CheckReport> = {}): CheckReport =>
  ({
    version: 3,
    findings: [],
    metadataIssue: null,
    ok: true,
    missingRefs: [],
    corruptedPaths: [],
    repairablePaths: [],
    orphanBlobs: [],
    orphansChecked: false,
    orphanScanIssue: null,
    localSkippedSentinel: null,
    ...over,
  }) as CheckReport

describe('runSkipNotice', () => {
  it('names the sentinel, because that is the path the operator has to go and look at', () => {
    expect(runSkipNotice(run({ skipReason: "Sentinel path '/mnt/nas/.mounted' does not exist." }))).toContain(
      '/mnt/nas/.mounted',
    )
  })

  it('still explains itself when an older backend sends no reason', () => {
    // The status can arrive without the reason (an older backend, a response trimmed somewhere in between).
    // A bare "Skipped" with no explanation reads as a bug in the app, so there has to be a fallback sentence.
    const notice = runSkipNotice(run({ skipReason: null }))
    expect(notice).toBeTruthy()
    expect(notice.toLowerCase()).toContain('skipped')
  })

  it('says nothing changed, so the round is not mistaken for a backup that ran', () => {
    // The whole risk this feature addresses is a round that looks like it worked. The line has to state
    // that nothing was recorded, the same way the Canceled line does.
    expect(runSkipNotice(run({ skipReason: "Sentinel path '/mnt/x' does not exist." })).toLowerCase()).toContain(
      'nothing was',
    )
  })
})

describe('checkLocalSkipNotice', () => {
  it('is silent when the local axis ran', () => {
    expect(checkLocalSkipNotice(report())).toBe('')
  })

  it('is silent for an older backend that does not send the field', () => {
    expect(checkLocalSkipNotice(report({ localSkippedSentinel: undefined }))).toBe('')
  })

  it('names the sentinel and says the cloud half still counts', () => {
    // Without the second half the banner reads as "this check is worthless", and the operator reruns a
    // check that already told them everything it could about the cloud copy.
    const notice = checkLocalSkipNotice(report({ localSkippedSentinel: '/mnt/nas/.mounted' }))
    expect(notice).toContain('/mnt/nas/.mounted')
    expect(notice.toLowerCase()).toContain('cloud')
  })
})
