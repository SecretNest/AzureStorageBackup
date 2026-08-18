import { describe, expect, test } from 'vitest'

import type { StageProgress } from '../api/backupConfigs'
import { stageLines } from './stageLines'

function progress(over: Partial<StageProgress> = {}): StageProgress {
  return {
    stage: 'Uploading',
    processed: 0,
    total: 0,
    bytes: 0,
    currentItem: null,
    activeItems: [],
    bytesPerSecond: 0,
    preparing: 0,
    queued: 0,
    waitingOnArchive: 0,
    percent: null,
    etaSeconds: null,
    estimatedRemaining: null,
    workTotal: 0,
    workDone: 0,
    workRemaining: 0,
    transferredBytes: 0,
    unfinishedItemBytes: 0,
    stagedBytes: 0,
    checkingBytes: 0,
    transferTotal: 0,
    workPercent: null,
    spilledItems: 0,
    uploading: 0,
    waitingOnPeer: 0,
    waitingOnSlot: 0,
    checking: 0,
    awaitingCompression: 0,
    awaitingUpload: 0,
    ...over,
  }
}

describe('stageLines', () => {
  /**
   * This is the reason for the whole reordering. With counts on one line and bytes on another, each
   * ordered by its own logic, nothing on screen could say whether "+2.0 GB unfinished" came before
   * or after "100 MB ready to upload" — and that really was asked, at length: "if volumes never
   * stall between one another, why are both non-zero with nothing transferring?" The answer is that
   * they are not at the same point of the timeline at all: the first is already in the cloud, the
   * second has not left. Laying the whole pipeline out backwards stops the question arising.
   */
  test('orders the whole pipeline from nearly-done back to queued', () => {
    const { pipeline } = stageLines(
      progress({
        unfinishedItemBytes: 3_000_000_000,
        activeItems: [
          { label: 'a', sent: 1, total: 2, percent: 50 },
          { label: 'b', sent: 1, total: 2, percent: 50 },
        ],
        waitingOnPeer: 2,
        waitingOnSlot: 3,
        stagedBytes: 2_800_000_000,
        awaitingUpload: 9,
        checking: 1,
        checkingBytes: 100_000_000,
        preparing: 1,
        waitingOnArchive: 4,
        awaitingCompression: 128,
        queued: 4_365,
        spilledItems: 12,
      }),
    )

    expect(pipeline.split(' · ')).toEqual([
      '+2.8 GB on the cloud',
      '2 volumes uploading',
      '2 objects waiting on the same content elsewhere',
      '3 volumes waiting for an upload slot',
      // The two hand-off queues bracket the compression stretch, one on each side: waiting for an uploader sits
      // just past "ready to upload" (the bytes those very items are holding), waiting for the compressor sits
      // just before "queued" (probed, but the single compressor has not got to them).
      '9 objects waiting for an uploader',
      '2.6 GB ready to upload',
      '1 object checking files',
      '95.4 MB being checked',
      '1 object preparing',
      '4 objects waiting for the archive slot',
      '128 objects waiting for the compressor',
      '4,365 objects queued',
      '12 objects buffered to disk',
    ])
  })

  /**
   * The reported symptom, reproduced. An operator saw
   * `+66.8 MB on the cloud · nothing on the wire right now · 24 objects starting upload · 66.8 MB ready to
   * upload · 1 object preparing`, with that 24 climbing all run and never coming back down.
   *
   * Those items were parked in the hand-off channel waiting for an uploader — claimed, nothing being done to
   * them. The backend folded them into `uploading`, and everything in `uploading` that cannot say what it is
   * waiting on gets reported as "starting upload", so a queue depth was rendered as a stalled upload. The fix is
   * on the backend (they no longer count as uploading); what this test pins is that the line now names the queue
   * they are actually in, and that "starting upload" is gone once the tier is empty.
   */
  test('a hand-off queue does not read as a stalled upload', () => {
    const { pipeline } = stageLines(
      progress({
        unfinishedItemBytes: 66_800_000,
        awaitingUpload: 24,
        stagedBytes: 66_800_000,
        preparing: 1,
      }),
    )

    expect(pipeline).toBe(
      '+63.7 MB on the cloud · nothing on the wire right now · ' +
        '24 objects waiting for an uploader · 63.7 MB ready to upload · 1 object preparing',
    )
    expect(pipeline).not.toContain('starting upload')
  })

  /** Empty segments disappear entirely — no "0 objects queued" noise. */
  test('drops every empty segment', () => {
    const { pipeline } = stageLines(progress({ queued: 7 }))
    expect(pipeline).toBe('7 objects queued')
  })

  /**
   * For the stretch that is compressed but not yet checked, the count and the bytes are separate
   * entries that **do not overlap**: the backend already subtracts checkingBytes from stagedBytes,
   * so the frontend only has to stop adding them together. Together they read as "one object is
   * being checked and those 95.4 MB are its; another 2.6 GB is checked and waiting to go up".
   */
  test('keeps bytes being checked out of ready to upload', () => {
    const { pipeline } = stageLines(
      progress({ stagedBytes: 2_800_000_000, checking: 1, checkingBytes: 100_000_000 }),
    )
    expect(pipeline).toBe('2.6 GB ready to upload · 1 object checking files · 95.4 MB being checked')
  })

  /**
   * Nothing on the wire while something is compressing: speed is 0 and the in-flight list is empty,
   * which looks exactly like a hang. Something has to say why, and it occupies uploading's slot
   * (the same point on the timeline).
   */
  test('says why the wire is idle instead of just dropping the segment', () => {
    const { pipeline } = stageLines(progress({ preparing: 1, queued: 3 }))
    expect(pipeline).toBe('nothing on the wire right now · 1 object preparing · 3 objects queued')
  })

  /** The first line carries only what has settled: the completion fraction and what was really uploaded. Nothing in flight gets in. */
  test('the done line carries only settled bytes', () => {
    const { done } = stageLines(
      progress({
        workTotal: 3_000_000_000_000,
        workDone: 1_900_000_000_000,
        workPercent: 62,
        transferredBytes: 1_900_000_000_000,
        unfinishedItemBytes: 3_000_000_000,
        stagedBytes: 2_800_000_000,
      }),
    )
    expect(done).toBe('1.7 TB / 2.7 TB original (62%) · 1.7 TB uploaded (100% of original)')
  })

  /**
   * Downloading is the other direction (pull down, then write out), so the wording cannot be shared
   * with uploading; and its total is known up front (volume sizes are in the index). What has not
   * been restored yet belongs at the far end of the timeline, on the second line.
   */
  test('restoring reads in the download direction', () => {
    const { done, pipeline } = stageLines(
      progress({
        stage: 'Restoring',
        transferredBytes: 500_000_000,
        transferTotal: 2_000_000_000,
        workDone: 400_000_000,
        workRemaining: 1_600_000_000,
        activeItems: [{ label: 'a', sent: 1, total: 2, percent: 50 }],
      }),
    )
    expect(done).toBe('476.8 MB / 1.9 GB downloaded · 381.5 MB restored')
    expect(pipeline).toBe('1 object downloading · 1.5 GB to go')
  })
})
