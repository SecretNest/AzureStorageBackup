import { describe, expect, test } from 'vitest'

import type { StageProgress } from '../api/backupConfigs'
import { pipelineHold, preparingLabelOf, stageLines } from './stageLines'

function progress(over: Partial<StageProgress> = {}): StageProgress {
  return {
    stage: 'Uploading',
    processed: 0,
    total: 0,
    bytes: 0,
    currentItem: null,
    activeItems: [],
    preparingItem: null,
    preparingBytes: 0,
    preparingDone: 0,
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
    awaitingInPlace: 0,
    awaitingRecording: 0,
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
        awaitingInPlace: 3,
        awaitingRecording: 12_000,
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
      // No '2 volumes uploading' here any more: the in-flight heading above the stream rows says exactly
      // that sentence, and the pipeline starts at the first thing the heading does not say.
      '2 objects waiting on the same content elsewhere',
      // The whole upload-side wait as one entry: the volumes on the disk, who owns them, and what they weigh —
      // one population stated three ways. No volume on the wire is in the volumes or the bytes, so neither
      // overlaps the "2 volumes uploading" above.
      '33 volumes (7 objects, 3.073 GB) waiting for uploading',
      // The same point of the pipeline, kept adjacent and ordered by what is left to do: off the disk, from the
      // source, and — for content already stored — nothing at all but the index entry and the journal record.
      '3 objects waiting to upload in place',
      '12,000 objects waiting to be recorded',
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
   * The volume count leads the entry, and is dropped altogether when it equals the object count: one volume per
   * object means nothing was split, and the word would carry no information — worse, two identical numbers side by
   * side read as a bug. It leads the moment they diverge, because that difference is the entire reason it exists.
   *
   * Only that term moves. The size stays either way — it is the number the temp-disk question is asked of, and it
   * has nothing to do with whether anything was split.
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
    ).toContain('80 volumes (14 objects, 9.686 GB) waiting for uploading')

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
   * Where the volumes were measured has to be said in the one direction where the pair invites a division that
   * means nothing — **more objects than volumes** — because the two counts do not share a source: objects comes
   * from the item ledger, volumes off the staging pool. Left bare, "3 volumes (5 objects)" reads as several objects
   * merged into one volume, which never happens; one volume belongs to exactly one object.
   *
   * The gap used to be enormous, because every object owning no archive was counted here — on a store-only run the
   * entry sat at five figures of objects against a handful of volumes all run long. Those have their own entries
   * now, so what is left is the few an uploader has already picked up (a hit being settled, a raw file about to
   * open its stream), bounded by the uploader count. Small, but the reading it invites is the same, so the four
   * words stay.
   */
  test('says where the volume count was measured when the objects outnumber it', () => {
    const split = stageLines(
      progress({
        waitingToUploadObjects: 5,
        waitingToUploadVolumes: 3,
        waitingToUploadBytes: 6_943_888_875,
      }),
    ).pipeline

    expect(split).toContain('3 volumes on the staging disk (5 objects, 6.467 GB) waiting for uploading')
  })

  /**
   * The other direction gets no such qualifier, and that is the point of making it conditional: one object across
   * 269 volumes is a split file and nothing else, so naming the disk there is four words that answer a question
   * nobody asked. The reported line — a single big file, five of its volumes on the wire, the rest on disk.
   */
  test('leaves the disk unnamed when one object owns many volumes', () => {
    const { pipeline } = stageLines(
      progress({
        unfinishedItemBytes: 52_930_000_000,
        activeItems: Array.from({ length: 5 }, (_, i) => ({
          label: `v${i}`,
          sent: 1,
          total: 2,
          percent: 50,
        })),
        waitingToUploadObjects: 1,
        waitingToUploadVolumes: 269,
        waitingToUploadBytes: 28_196_000_000,
      }),
    )

    expect(pipeline).toContain('269 volumes (1 object, 26.260 GB) waiting for uploading')
    expect(pipeline).not.toContain('on the staging disk')
    // The number in front can no longer collapse while the disk is full — that was the whole defect.
    expect(pipeline).not.toContain('0 objects')
  })

  /**
   * An object count of 0 against waiting volumes is still reachable, and it is a real state rather than a
   * miscount: a peer waiter is named in its own entry ("waiting on the same content elsewhere") and so is kept
   * out of this one, while its archive is on the disk and counted here. The term drops out rather than printing
   * "(0 objects", which would put the contradiction back on screen in the aside instead of the subject.
   */
  test('drops the object term rather than printing a zero', () => {
    const { pipeline } = stageLines(
      progress({
        waitingOnPeer: 1,
        waitingToUploadObjects: 0,
        waitingToUploadVolumes: 9,
        waitingToUploadBytes: 900_000_000,
      }),
    )

    expect(pipeline).toBe(
      '1 object waiting on the same content elsewhere · 9 volumes (858.3 MB) waiting for uploading',
    )
  })

  /**
   * The bytes are allowed to disagree with the object count in either direction, so an object count standing alone
   * still renders bare rather than printing "(0 B)" — which would read as a bug, or worse, as a full temp disk.
   */
  test('drops the parentheses entirely when the queue owns no archives', () => {
    const noArchives = stageLines(progress({ waitingToUploadObjects: 4 })).pipeline
    expect(noArchives).toContain('4 objects waiting for uploading')
    expect(noArchives).not.toContain('(')
  })

  /**
   * The three populations that wait at the same point of the pipeline, told apart by what is left to do to them.
   * They were one entry, and counting them together is what made the upload wait unreadable: a mostly-unchanged
   * run queues its whole dataset in hits, so the entry stood at five figures of objects against a handful of
   * volumes — a ratio that looks like a merge and is really three populations printed as one, claiming a temp disk
   * full of work that did not exist.
   *
   * The wording is the point of the split as much as the arithmetic. A hit has **nothing to send** — its content
   * is already stored, by an earlier version or by this run's own earlier attempt — so the only honest verb is
   * about the index entry and the journal record it still needs. A raw in-place item is genuinely waiting to
   * upload, just not off the staging disk, and saying "in place" is what keeps it from looking like a fourth
   * temp-disk number.
   */
  test('separates what is waiting on the disk, in place, and for nothing but a record', () => {
    const { pipeline } = stageLines(
      progress({
        waitingToUploadObjects: 1,
        waitingToUploadVolumes: 269,
        waitingToUploadBytes: 28_196_000_000,
        awaitingInPlace: 3,
        awaitingRecording: 12_000,
      }),
    )

    expect(pipeline).toBe(
      '269 volumes (1 object, 26.260 GB) waiting for uploading · ' +
        '3 objects waiting to upload in place · ' +
        '12,000 objects waiting to be recorded',
    )
  })

  /** Each of the two appears on its own, and neither borrows the other's verb. */
  test('names each archiveless wait for what it is', () => {
    expect(stageLines(progress({ awaitingInPlace: 1 })).pipeline).toBe(
      '1 object waiting to upload in place',
    )
    expect(stageLines(progress({ awaitingRecording: 1 })).pipeline).toBe(
      '1 object waiting to be recorded',
    )
    // Nothing on the staging disk, so the entry that describes the staging disk stays away entirely.
    expect(stageLines(progress({ awaitingRecording: 9 })).pipeline).not.toContain('waiting for uploading')
  })

  /**
   * No volume on the wire is in the entry's volumes or bytes, which is the whole point of merging them into one
   * population: the backend excludes every in-flight volume from both, whole rather than by the part already sent.
   * What is on the wire is described by the in-flight list, per stream, as its sent/total.
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
      '9 volumes (3 objects, 858.3 MB) waiting for uploading',
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
      '28 volumes (4 objects, 2.608 GB) waiting for uploading · ' +
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
    expect(pipeline).toBe('1.490 GB to go')
  })
})

/**
 * The reported symptom: a backup winding down or held at the pause gate still read
 * `9 objects waiting for the compressor · 4,365 objects queued`, two verbs promising motion that had
 * already stopped — while the row directly above said "Suspending…" or "Paused".
 *
 * The two entries are the only ones in the list the hold provably freezes, and both freeze for the same
 * reason: the compressor's loop checks the stop intent (`control is { Stop: not StopKind.None }`) and the
 * pause gate the line after it, **before** anything is done to the item. Everything downstream of that
 * check is already past it — an item inside the staging area goes on to compress and upload, which is
 * precisely why a suspend takes minutes, so those entries stay as they are and go on moving.
 *
 * The wind-down case is worse than a stale number: `queued` is a subtraction
 * (`_enqueued - _processed - inWork`, see StageProgress.Tick), and draining the probed channel releases
 * each item's share without marking it processed — so abandoned work **migrates into** queued and the
 * number climbs while the run winds down.
 */
describe('a held pipeline', () => {
  const held = () =>
    progress({
      activeItems: [{ label: 'a', sent: 1, total: 2, percent: 50 }],
      waitingToUploadObjects: 7,
      waitingToUploadVolumes: 33,
      waitingToUploadBytes: 3_300_000_000,
      awaitingCompression: 9,
      queued: 4_365,
    })

  /**
   * The two counts become one because the hold makes them one population — neither has been started and
   * neither will be — and stating them apart would keep inviting the question the split exists to answer
   * ("is the compressor the bottleneck?"), which no longer has an answer once nothing is running.
   *
   * The journal outlives every stop kind (`SettleStopAsync` flushes before it returns, and
   * `showsInterruptedNotice` offers Resume for Canceled as well as Suspended), so "left for the next run"
   * is true of a Stop, not only of a Suspend.
   */
  test('a wind-down says the queue is not going to be started', () => {
    expect(stageLines(held(), 'winding-down').pipeline).toBe(
      '33 volumes (7 objects, 3.073 GB) waiting for uploading · ' +
        '4,374 objects left for the next run',
    )
  })

  /** A pause holds the same population rather than abandoning it, and the wording has to say which. */
  test('a pause says the queue is held', () => {
    expect(stageLines(held(), 'paused').pipeline).toBe(
      '33 volumes (7 objects, 3.073 GB) waiting for uploading · ' +
        '4,374 objects held by the pause',
    )
  })

  /**
   * The entries the hold does **not** freeze keep their own wording. A suspend waits out the file in hand
   * and every one of its volumes, so what is on the staging disk and on the wire is the one thing on this
   * line that answers "why is this still going?" — replacing it would take away the only number that does.
   */
  test('leaves everything already past the hold alone', () => {
    const { pipeline } = stageLines(
      progress({
        preparing: 1,
        waitingOnArchive: 4,
        waitingOnRoom: 1,
        checking: 1,
        checkingBytes: 100_000_000,
      }),
      'winding-down',
    )
    expect(pipeline).toBe(
      'nothing on the wire right now · 1 object checking files · 95.4 MB being checked · ' +
        '1 object preparing · 4 objects waiting for the archive slot · 1 object waiting for staging room',
    )
  })

  /** An empty queue stays empty: a hold is not a reason to print "0 objects left for the next run". */
  test('drops the entry when there is no queue to hold', () => {
    expect(stageLines(progress({ preparing: 1 }), 'paused').pipeline).toBe(
      'nothing on the wire right now · 1 object preparing',
    )
  })

  /** Nothing changes for a run that is neither winding down nor paused. */
  test('an unheld run reads exactly as before', () => {
    expect(stageLines(held()).pipeline).toBe(
      '33 volumes (7 objects, 3.073 GB) waiting for uploading · ' +
        '9 objects waiting for the compressor · 4,365 objects queued',
    )
  })
})

/**
 * Which of the two holds the row is actually under. Its own function because the answer is not the
 * disjunction it looks like: a run can be both at once, and only one of the two words is true then.
 */
describe('pipelineHold', () => {
  /**
   * Every rung of the stop ladder abandons the queue identically — the orchestrator's check is
   * `Stop: not StopKind.None`, never a comparison against one kind — so all three read the same here.
   */
  test('every stop kind is one wind-down', () => {
    expect(pipelineHold('suspend', false)).toBe('winding-down')
    expect(pipelineHold('finish', false)).toBe('winding-down')
    expect(pipelineHold('now', false)).toBe('winding-down')
  })

  test('a pause with no stop under way is a pause', () => {
    expect(pipelineHold(undefined, true)).toBe('paused')
  })

  /**
   * Both at once, and the stop wins. A stop **downgrades** the pause gate (see windDownControls), after
   * which the gate can never hold anyone again — so at that moment the queue is abandoned, not held, and
   * "held by the pause" would promise a resume that the run is no longer going to reach.
   */
  test('a stop outranks a pause it has already downgraded', () => {
    expect(pipelineHold('suspend', true)).toBe('winding-down')
  })

  test('an ordinary running backup is under no hold', () => {
    expect(pipelineHold(undefined, false)).toBeUndefined()
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

describe('repairing in-flight wording', () => {
  /**
   * The verb follows the item's own wire flag, not the label's shape. Two field incidents taught that:
   * first "1 object downloading" over a local hash read (an Archive-tier operator read it as an
   * unconsented rehydration and stopped a healthy repair), then — after repair uploads adopted
   * source-path labels — five parallel uploads read as "5 objects hashing" because the label-prefix
   * guess broke. The backend knows which streams cross the network; the display just repeats it.
   */
  test('a local read shows as hashing, not downloading', () => {
    const { inFlightPhrase } = stageLines(
      progress({
        stage: 'Repairing',
        activeItems: [{ label: '/nas/movies/big.mkv', sent: 80_000, total: 113_949_000_000, percent: 0, wire: false }],
      }),
    )
    expect(inFlightPhrase).toBe('1 object hashing')
  })

  test('wire streams read as uploading, counted in volumes, whatever the labels look like', () => {
    const { inFlightPhrase } = stageLines(
      progress({
        stage: 'Repairing',
        activeItems: [
          { label: '/nas/movies/big.mkv (50/1167)', sent: 1, total: 2, percent: 50, wire: true },
          { label: '/nas/movies/big.mkv (51/1167)', sent: 0, total: 2, percent: 0, wire: true },
        ],
      }),
    )
    expect(inFlightPhrase).toBe('2 volumes uploading')
  })

  /** The user's reading of "Repairing: 0 of 4 objects" over a run visibly at work: the count should say
   * which object is UNDER repair, not how many are finished — Repaired is the past tense, Repairing is
   * not. So the stage counts one ahead while work remains, and lands exactly on N of N at the end. */
  test('the count names the object under repair, not the finished ones', () => {
    expect(stageLines(progress({ stage: 'Repairing', processed: 0, total: 4 })).counts)
      .toBe('1 of 4 objects')
    expect(stageLines(progress({ stage: 'Repairing', processed: 3, total: 4 })).counts)
      .toBe('4 of 4 objects')
    expect(stageLines(progress({ stage: 'Repairing', processed: 4, total: 4 })).counts)
      .toBe('4 of 4 objects')
  })

  /** The pipeline's first segment used to repeat the in-flight heading word for word ("5 volumes
   * uploading" twice on one screen); the heading owns that sentence now, and the pipeline starts at
   * the first thing the heading does NOT say. */
  test('the pipeline does not repeat the in-flight heading', () => {
    const { pipeline } = stageLines(
      progress({
        stage: 'Repairing',
        activeItems: [{ label: 'x', sent: 1, total: 2, percent: 50, wire: true }],
        queued: 3,
      }),
    )
    expect(pipeline).toBe('3 objects queued')
  })
})

describe('diffing in-flight wording', () => {
  /**
   * The diff's content hashing gained the same per-file read registration as the repair's hash gate,
   * so its verb needs the same honesty: a file being read end to end locally is hashing, not
   * downloading. And hashing is serial by design (one file at a time, deliberately not concurrent),
   * so the in-flight heading must not say "in parallel" over it — the user read "1 object hashing in
   * parallel" as a claim that hashes run concurrently.
   */
  test('a diff-stage read is hashing, counted in files', () => {
    const { inFlightPhrase } = stageLines(
      progress({
        stage: 'Diffing',
        activeItems: [{ label: 'movies/big.mkv', sent: 5_000_000, total: 113_949_000_000, percent: 0 }],
      }),
    )
    expect(inFlightPhrase).toBe('1 file hashing')
  })

  test('the hashing heading does not claim parallelism', () => {
    const { inFlightHeading } = stageLines(
      progress({
        stage: 'Diffing',
        activeItems: [{ label: 'movies/big.mkv', sent: 5, total: 10, percent: 50 }],
      }),
    )
    expect(inFlightHeading).toBe('1 file hashing:')
  })

  test('the repair hash gate heading does not claim parallelism either', () => {
    const { inFlightHeading } = stageLines(
      progress({
        stage: 'Repairing',
        activeItems: [{ label: '/nas/movies/big.mkv', sent: 5, total: 10, percent: 50, wire: false }],
      }),
    )
    expect(inFlightHeading).toBe('1 object hashing:')
  })

  test('transfers keep the parallel heading', () => {
    const { inFlightHeading } = stageLines(
      progress({
        stage: 'Uploading',
        activeItems: [{ label: 'a', sent: 1, total: 2, percent: 50 }],
      }),
    )
    expect(inFlightHeading).toBe('1 volume uploading in parallel:')
  })
})

describe('preparingLabelOf', () => {
  /**
   * The count and the row naming the item are two halves of one statement, so they read the word from one
   * place. "1 object preparing" above a row that says "extracting" would read as two different things.
   */
  test('the upload side prepares and the download side extracts', () => {
    expect(preparingLabelOf('Uploading')).toBe('preparing')
    expect(preparingLabelOf('Restoring')).toBe('extracting')
    // The repair's preparing stretch is 7z producing the replacement volumes — the same work the
    // upload stage calls preparing. "extracting" would claim the opposite direction.
    expect(preparingLabelOf('Repairing')).toBe('preparing')
    expect(preparingLabelOf('Verifying')).toBe('extracting')
  })
})
