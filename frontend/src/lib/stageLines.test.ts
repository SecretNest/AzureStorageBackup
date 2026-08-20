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
    waitingOnRoom: 0,
    percent: null,
    etaSeconds: null,
    estimatedRemaining: null,
    workTotal: 0,
    workDone: 0,
    workRemaining: 0,
    transferredBytes: 0,
    unfinishedItemBytes: 0,
    waitingToUploadBytes: 0,
    waitingToUploadVolumes: 0,
    waitingToUploadObjects: 0,
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
        waitingToUploadObjects: 7,
        waitingToUploadVolumes: 33,
        waitingToUploadBytes: 3_300_000_000,
        checking: 1,
        checkingBytes: 100_000_000,
        preparing: 1,
        waitingOnArchive: 4,
        waitingOnRoom: 1,
        // The probedQueue bound, so this is the deepest this entry can ever read (see BackupOrchestrator).
        awaitingCompression: 9,
        queued: 4_365,
        spilledItems: 12,
      }),
    )

    expect(pipeline.split(' · ')).toEqual([
      '+2.794 GB on the cloud',
      '2 volumes uploading',
      '2 objects waiting on the same content elsewhere',
      // The whole upload-side wait as one entry: objects, the volumes they are split across, and their size on
      // disk — one population stated three ways. Nothing on the wire is in any of the three, so this entry and
      // the "2 volumes uploading" above it never overlap.
      '7 objects (33 volumes, 3.073 GB) waiting for uploading',
      '1 object checking files',
      '95.4 MB being checked',
      '1 object preparing',
      // The two staging-area waits, split: the lock points at a producer (possibly another backup's), the pool's
      // byte ceiling points at the wire. Reported as one number, the second looked exactly like the first.
      '4 objects waiting for the archive slot',
      '1 object waiting for staging room',
      '9 objects waiting for the compressor',
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
        waitingToUploadObjects: 24,
        waitingToUploadVolumes: 24,
        waitingToUploadBytes: 66_800_000,
        preparing: 1,
      }),
    )

    expect(pipeline).toBe(
      '+63.7 MB on the cloud · nothing on the wire right now · ' +
        '24 objects (63.7 MB) waiting for uploading · 1 object preparing',
    )
    expect(pipeline).not.toContain('starting upload')
  })

  /**
   * The volume count is dropped when it equals the object count: one volume per object means nothing was split,
   * and the word would carry no information — worse, two identical numbers side by side read as a bug. It stays
   * the moment they diverge, because that difference is the entire reason the count exists.
   */
  test('names the volumes only when something is actually split', () => {
    expect(
      stageLines(
        progress({
          waitingToUploadObjects: 14,
          waitingToUploadVolumes: 80,
          waitingToUploadBytes: 10_400_000_000,
        }),
      ).pipeline,
    ).toContain('14 objects (80 volumes, 9.686 GB) waiting for uploading')

    expect(
      stageLines(
        progress({
          waitingToUploadObjects: 14,
          waitingToUploadVolumes: 14,
          waitingToUploadBytes: 10_400_000_000,
        }),
      ).pipeline,
    ).toContain('14 objects (9.686 GB) waiting for uploading')

    expect(stageLines(progress({})).pipeline).not.toContain('waiting for uploading')
  })

  /**
   * The bytes are allowed to disagree with the object count in either direction. A dedup hit, a resume hit and a
   * raw in-place item all queue owning no archive at all, so a store-only run queues the whole dataset here
   * against nothing on disk — and printing "(0 B)" beside five figures of objects would read as a bug, or worse,
   * as a full temp disk. The parentheses simply drop out.
   */
  test('drops the parentheses entirely when the queue owns no archives', () => {
    const noArchives = stageLines(progress({ waitingToUploadObjects: 12_000 })).pipeline
    expect(noArchives).toContain('12,000 objects waiting for uploading')
    expect(noArchives).not.toContain('(')
  })

  /**
   * The entry and the in-flight count never overlap, which is the whole point of merging them into one
   * population: the backend excludes every in-flight volume from all three numbers, whole rather than by the
   * part already sent. What is on the wire is described by the in-flight list, per stream, as its sent/total.
   *
   * The lump this replaced ("in the uploaders' hands") did include that tail, so the same bytes were named
   * twice on one line — and the volumes an uploader owned but had not started appeared in no number at all,
   * which is how 8.268 GB came to sit against 19 reported volumes.
   */
  test('nothing on the wire appears in the waiting entry', () => {
    const { pipeline } = stageLines(
      progress({
        activeItems: [{ label: 'a', sent: 400_000_000, total: 500_000_000, percent: 80 }],
        waitingToUploadObjects: 3,
        waitingToUploadVolumes: 9,
        waitingToUploadBytes: 900_000_000,
      }),
    )

    expect(pipeline).toBe(
      '1 volume uploading · 3 objects (9 volumes, 858.3 MB) waiting for uploading',
    )
    expect(pipeline).not.toContain("uploaders' hands")
  })

  /**
   * The staging area's two waits point at opposite culprits: the archive lock means a producer is holding it —
   * possibly another backup's, which you can go and stop — while a full pool means only an upload can help and
   * stopping anything would be the wrong move. Reported as one number, the second wore the first's diagnosis,
   * and the second is the one an upload-bound run sits in for most of its life.
   */
  test('tells a full staging pool apart from a lock held elsewhere', () => {
    expect(stageLines(progress({ waitingOnRoom: 1 })).pipeline).toBe(
      '1 object waiting for staging room',
    )
    expect(stageLines(progress({ waitingOnArchive: 1 })).pipeline).toBe(
      '1 object waiting for the archive slot',
    )
  })

  /** Empty segments disappear entirely — no "0 objects queued" noise. */
  test('drops every empty segment', () => {
    const { pipeline } = stageLines(progress({ queued: 7 }))
    expect(pipeline).toBe('7 objects queued')
  })

  /**
   * For the stretch that is compressed but not yet checked, the count and the bytes are separate
   * entries that **do not overlap** with the waiting one: the backend subtracts checking's bytes *and* its
   * volumes from it, so the frontend only has to stop adding them together. Together they read as "one object
   * is being checked and those 95.4 MB are its; another 2.608 GB across 28 volumes is waiting to go".
   */
  test('keeps what is being checked out of the waiting entry beside it', () => {
    const { pipeline } = stageLines(
      progress({
        waitingToUploadObjects: 4,
        waitingToUploadVolumes: 28,
        waitingToUploadBytes: 2_800_000_000,
        checking: 1,
        checkingBytes: 100_000_000,
      }),
    )
    expect(pipeline).toBe(
      '4 objects (28 volumes, 2.608 GB) waiting for uploading · ' +
        '1 object checking files · 95.4 MB being checked',
    )
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
        waitingToUploadBytes: 2_800_000_000,
      }),
    )
    expect(done).toBe('1.728 TB / 2.728 TB original (62%) · 1.728 TB uploaded (100% of original)')
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
    expect(done).toBe('476.8 MB / 1.863 GB downloaded · 381.5 MB restored')
    expect(pipeline).toBe('1 object downloading · 1.490 GB to go')
  })
})

/**
 * The stage name is printed on screen, so it cannot be the backend's own token. Every other stage
 * happens to be a single English word and reads fine untranslated, which is exactly why the one
 * compound token — the index-loading stage a check opens with — shipped raw: "LoadingIndex:".
 * The same trap `backupStageLabels` already solved for the numeric enum (WritingIndex → "Writing
 * index"), only on the string axis, where nothing was mapping at all.
 */
describe('stage labels', () => {
  test('a compound stage token is spelled out for the screen', () => {
    expect(stageLines(progress({ stage: 'LoadingIndex' })).label).toBe('Loading index')
  })

  test('single-word stages keep their own name', () => {
    expect(stageLines(progress({ stage: 'Uploading' })).label).toBe('Uploading')
    expect(stageLines(progress({ stage: 'Verifying' })).label).toBe('Verifying')
  })

  /**
   * An unmapped token must still print something rather than nothing — a stage added on the backend
   * and not here should read a little terse, not vanish and leave a bare "  : 12 of 40 files".
   */
  test('an unknown stage falls back to its own token', () => {
    expect(stageLines(progress({ stage: 'Something' })).label).toBe('Something')
  })
})
