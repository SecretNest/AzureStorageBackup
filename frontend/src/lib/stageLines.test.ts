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
    stagedBytes: 0,
    waitingToUploadBytes: 0,
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
        stagedBytes: 500_000_000,
        waitingOnPeer: 2,
        waitingOnSlot: 3,
        waitingToUploadBytes: 2_800_000_000,
        awaitingUpload: 7,
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
      // Beside the in-flight count, because that is mostly what it is: the unsent tail of the volumes on the wire,
      // plus the archives of the peer waiters below. Everything here is already owned by an uploader, which is the
      // one thing that distinguishes it from the queue two entries down.
      "476.8 MB in the uploaders' hands",
      '2 objects waiting on the same content elsewhere',
      // Both upload-side waits in one entry, each keeping its own unit: volumes are queuing at the global gate,
      // objects have been claimed and compressed but no uploader has picked them up. The bytes in the parentheses
      // are measured on exactly those two waits, so the counts and the size finally describe the same set — the
      // old pairing put the whole pool next to them, tail of the in-flight transfers included.
      '3 volumes + 7 objects (2.608 GB) waiting for uploading',
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
        awaitingUpload: 24,
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
   * The merged entry has three shapes, and the two half-empty ones must not leave a dangling "+" or an
   * orphaned unit. Only the halves that are non-zero get named, and each keeps its own unit — that unit is
   * the only thing left saying which of the two stages a number belongs to.
   */
  test('the upload-side wait names only the halves that are non-zero', () => {
    expect(stageLines(progress({ waitingOnSlot: 5, awaitingUpload: 230 })).pipeline).toContain(
      '5 volumes + 230 objects waiting for uploading',
    )
    expect(stageLines(progress({ waitingOnSlot: 5 })).pipeline).toContain(
      '5 volumes waiting for uploading',
    )
    expect(stageLines(progress({ awaitingUpload: 1 })).pipeline).toContain(
      '1 object waiting for uploading',
    )
    expect(stageLines(progress({})).pipeline).not.toContain('waiting for uploading')
  })

  /**
   * The queue's bytes are its own, and they are allowed to disagree with its item count in either direction.
   * A dedup hit, a resume hit and a raw in-place item all queue owning no archive at all, so a store-only run
   * queues the whole dataset here against nothing on disk — and printing "(0 B)" beside five figures of objects
   * would read as a bug, or worse, as a full temp disk. The parentheses simply drop out.
   */
  test('the upload-side wait carries its own bytes, and none when it holds none', () => {
    expect(
      stageLines(progress({ waitingOnSlot: 2, awaitingUpload: 9, waitingToUploadBytes: 3_500_000_000 }))
        .pipeline,
    ).toContain('2 volumes + 9 objects (3.260 GB) waiting for uploading')

    const noArchives = stageLines(progress({ awaitingUpload: 12_000 })).pipeline
    expect(noArchives).toContain('12,000 objects waiting for uploading')
    expect(noArchives).not.toContain('(')
  })

  /**
   * The bytes an uploader is already holding are a different set from the bytes queued for one, and after the
   * merge they are also different entries: the queue's own size sits in its parentheses, and what is left of the
   * pool — the unsent tail of everything on the wire, and the archives of items stuck on a peer — moves up beside
   * the in-flight count. Pairing the pool total with the queue's counts, as the old "ready to upload" did,
   * overstated the queue by exactly the amount that was moving.
   */
  test('separates what is queued for an uploader from what one already holds', () => {
    const { pipeline } = stageLines(
      progress({
        activeItems: [{ label: 'a', sent: 400_000_000, total: 500_000_000, percent: 80 }],
        stagedBytes: 100_000_000,
        awaitingUpload: 3,
        waitingToUploadBytes: 900_000_000,
      }),
    )

    expect(pipeline).toBe(
      "1 volume uploading · 95.4 MB in the uploaders' hands · 3 objects (858.3 MB) waiting for uploading",
    )
    expect(pipeline).not.toContain('ready to upload')
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
   * entries that **do not overlap**: the backend already subtracts checkingBytes from stagedBytes,
   * so the frontend only has to stop adding them together. Together they read as "one object is
   * being checked and those 95.4 MB are its; another 2.608 GB is in an uploader's hands".
   */
  test('keeps bytes being checked out of the pool figure beside them', () => {
    const { pipeline } = stageLines(
      progress({ stagedBytes: 2_800_000_000, checking: 1, checkingBytes: 100_000_000 }),
    )
    expect(pipeline).toBe(
      "2.608 GB in the uploaders' hands · 1 object checking files · 95.4 MB being checked",
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
        stagedBytes: 2_800_000_000,
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
