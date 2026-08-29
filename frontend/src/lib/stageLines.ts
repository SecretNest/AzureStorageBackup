import type { StageProgress } from '../api/backupConfigs'
import { formatBytes, formatDuration } from '../constants/format'
import type { WindDownKind } from './windDownControls'

const STAGE_UNITS: Record<string, string> = {
  Scanning: 'entries',
  Diffing: 'files',
  Uploading: 'objects',
  Restoring: 'objects',
  // The check stages. Cloud counts volumes (one HEAD per volume — its unit of real work, so a
  // thousand-volume object does not freeze the bar as one tick), Verifying counts packs that get
  // downloaded, extracted and re-hashed (its bytes carry the real progress), and Local counts index
  // entries — the three differ by orders of magnitude and cannot share one word.
  Cloud: 'volumes',
  // The repair's own stages: the pre-check surfaces as Assessing (same HEAD probing, repair's name on it),
  // and Repairing counts damaged objects with byte-based completion (declared source bytes, like restore).
  Assessing: 'volumes',
  Repairing: 'objects',
  Verifying: 'objects',
  Local: 'files',
  // The orphan scan's listing pass, named for the work rather than the quarry: it counts every blob it lists,
  // not the orphans among them, and under the old name Orphans the container's own size read as an orphan count.
  Listing: 'blobs',
}

/**
 * What each stage is called **on screen**. The backend's stage token is an internal identifier, and
 * printing it raw only ever worked by luck: every other stage happens to be one English word, so
 * nobody noticed there was no mapping until the check's opening stage — `LoadingIndex` — reached a
 * user as "LoadingIndex: 0 entries so far".
 *
 * The numeric backup stage already has this map (`backupStageLabels`, where WritingIndex reads
 * "Writing index"); this is the same map on the string axis, which had none.
 *
 * Only tokens that do not read as English need an entry. The fallback is the token itself, so a
 * stage added on the backend and forgotten here degrades to a terse heading rather than a blank one.
 */
const STAGE_LABELS: Record<string, string> = {
  LoadingIndex: 'Loading index',
  Assessing: 'Assessing damage',
}

/** The on-screen name of a stage — exported so run headlines (the repair row) can say which phase is
 * actually underway, the way a backup's headline distinguishes Diffing from Uploading. A row that says
 * "Repairing" while the pre-check is still probing reads as the wrong operation entirely. */
export function stageLabelOf(stage: string): string {
  return STAGE_LABELS[stage] ?? stage
}

/**
 * Give a number its own unit word, with singular and plural following the number.
 *
 * The in-flight line carries two counting bases at once — upload registers per **volume** (VolumeBlobIO
 * one entry per volume, and the upload gate queues per volume) while everything else counts **items**.
 * Units used to be written only on the two volume entries, leaving the item ones bare, so a line read
 * like "1 volume uploading · 1 preparing" — intermittent enough to look like a typo rather than a
 * deliberate distinction. Now every number states its own unit: the reader need not remember which is
 * which, and will not try to add this line's numbers into a total.
 */
function withUnit(n: number, plural: string): string {
  const singular = plural === 'entries' ? 'entry' : plural.replace(/s$/, '')
  return `${n.toLocaleString()} ${n === 1 ? singular : plural}`
}

/**
 * What has stopped the front of the pipeline, when something has.
 *
 * Only two entries of the in-flight line are frozen by either of these, and both are frozen at the same
 * place: the compressor's loop checks the stop intent and then the pause gate **before** it does anything
 * to the item it has just taken (`BackupOrchestrator`, the probed-queue loop). Everything the line reports
 * downstream of that point — inside the staging area, checking, on the wire — is already past the check
 * and goes on moving, which is exactly why a suspend can take minutes.
 *
 * The distinction between the two words matters: a wind-down **abandons** that queue for this run, while a
 * pause merely holds it.
 */
export type PipelineHold = 'winding-down' | 'paused'

/**
 * Which hold a row is under, given the two the backend reports separately.
 *
 * A stop **outranks** a pause rather than being an alternative to it, and the run really can be in both
 * states at once: `RequestStop` downgrades the pause gate on its way past, so from that moment the gate can
 * never hold anyone again (see windDownControls, which disables Resume for the same reason) and the run is
 * winding down whatever `pause` still says. Reading the pause first would put "held by the pause" on a
 * queue that is being abandoned, promising a resume the run is not going to reach.
 */
export function pipelineHold(
  windDown: WindDownKind | undefined,
  paused: boolean,
): PipelineHold | undefined {
  if (windDown) return 'winding-down'
  return paused ? 'paused' : undefined
}

/**
 * Split one stage snapshot into the two lines on screen.
 *
 * Extracted so it can be tested: the entire difficulty of these two lines is **order** and **wording**,
 * and both can otherwise only be confirmed by reading the code — once a string is inside JSX there is
 * nowhere left to assert it. A wrong order raises no error, it just regrows the endless "which of these
 * two numbers comes first?" question on screen.
 *
 * `hold` is the run's own state, which the snapshot cannot carry: the backend goes on counting the queue
 * truthfully while nothing is consuming it, and only the row knows that. See the entry it governs.
 */
export function stageLines(detail: StageProgress, hold?: PipelineHold) {
  const unit = STAGE_UNITS[detail.stage] ?? 'items'
  const label = STAGE_LABELS[detail.stage] ?? detail.stage
  // "of" rather than "/": a slash is fraction notation, and putting one there invites a percentage —
  // while an item-count percentage means very little during upload (one item may be a 6.8 GB single file
  // or a pack of several hundred 5 KB files). Real completion is by bytes, in the "60.618 GB / 191.000 GB
  // original (31%)" on the next line, which does use a slash because its percentage follows it and the
  // fraction is one you can legitimately form.
  // Repairing counts one ahead: the stage's name is the present tense, and "0 of 4" over a run visibly at
  // work read as a contradiction — the count names the object UNDER repair, landing exactly on N of N as
  // the last one finishes (field wording: "既然是Repairing而不是Repaired,为什么不显示1 of 4").
  const shown =
    detail.stage === 'Repairing' && detail.total > 0
      ? Math.min(detail.processed + 1, detail.total)
      : detail.processed
  const counts =
    detail.total > 0
      ? `${shown.toLocaleString()} of ${detail.total.toLocaleString()} ${unit}`
      : `${detail.processed.toLocaleString()} ${unit} so far` // Scanning does not know the total — computing it is what scanning is for
  // The in-flight breakdown. "N items processed" alone cannot distinguish work from a hang: during the
  // upload stage an item goes through 7z first (a 100 MB pack can take tens of seconds) before a single
  // byte is pushed, and during that window uploading is 0 while preparing is not. Restore and verify are
  // the same: the in-flight window closes as soon as the download ends, and the local CPU work of
  // extracting and hashing that follows still has to occupy preparing to stay visible. Each figure
  // appears only when non-zero — Scanning and Diffing have no queue, so all are 0 and this section
  // disappears entirely.
  //
  // preparing is now **stage-dependent**, not merely different in range but in what it counts: during
  // upload it counts items holding the global compression lock and producing volume files — there is one
  // lock, so it is always 0 or 1, and anything queuing behind it counts as queued; during restore and
  // verify it counts groups that finished downloading and are extracting or hashing, where there is no
  // global lock and up to DownloadConcurrency groups can run at once. The label follows that meaning.
  //
  // Nothing on the wire while work is being prepared must **not** just make "N uploading" quietly
  // disappear: at that moment speed is 0, the bar is still, and not one in-flight path is listed — it
  // looks exactly like a hang. State the reason.
  // The wording avoids "compressing": a backup configured not to compress still goes through 7z
  // (encryption, packing, volume splitting), so compressing would be wrong; restore and verify really
  // are extracting and hashing, so those say it plainly.
  const preparingLabel = preparingLabelOf(detail.stage)
  // Where an item is stuck between "compressed" and "bytes on the wire". The three are handled
  // completely differently, so they are stated separately:
  // · peer  — identical content is being uploaded by someone else; it can only wait for that whole item (minutes)
  // · slot  — the global upload gate is saturated (this one counts **volumes**; the gate queues per volume)
  // · cloud — waiting on a cloud response (existence / metadata HEAD); a slow network makes it tens of seconds
  // Without separating them, the screen says only "nothing is transferring", which is where investigation stops.
  //
  // Counting basis: peer and cloud count **items**, slot counts **volumes**. So only the first two take
  // part in item arithmetic; slot is stated separately with its unit spelled out.
  //
  // What is left (uploading minus peer and checking) are items past the staging stage whose bytes have
  // not left and which cannot say what they are waiting for: between registering "entered upload" and
  // the first volume taking off, the gaps between volumes, and the dedup-map lookup when nothing queued.
  // The disk-checking stretches (pre-check full read, per-member stat, clearing leftover cloud volumes)
  // are split into checking's own entry and must be subtracted here — without that, one item is reported
  // twice and the numbers on screen no longer add up to
  // processed + preparing + queued + uploading.
  // Reported only when **nothing is in flight**: with transfers running there is already an "N uploading"
  // above, and another tier would only blur which number is which. That reasoning does not apply to
  // checking (which says plainly what it is doing), so checking has no such gate.
  const stalled = Math.max(0, detail.uploading - detail.waitingOnPeer - detail.checking)
  // The front of the pipeline as one number, for the held case below. The two are summed rather than
  // stated apart because a hold makes them one population: neither has been started, and the only thing
  // that told them apart — whether the prober had read the item yet — decides nothing while nothing is
  // consuming either queue. Kept beside the other derived count rather than inline, so the entry that
  // prints it reads as the one sentence it is.
  const notStarted = detail.awaitingCompression + detail.queued
  // The whole upload-side wait as **one** entry, three numbers over one population: everything on the staging disk
  // with not one byte on the wire.
  //
  // It replaced a two-entry split — "N volumes + N objects waiting for uploading" next to "X in the uploaders' hands"
  // — whose dividing line was **ownership**: had an uploader thread picked this archive up yet? That is an
  // implementation detail nobody at the screen can act on, and reading the two side by side they looked like the same
  // thing counted twice. Worse, between them they counted neither of the volumes an uploader owns but has not started:
  // the per-item sliding window opens no task for those, so they sit in no queue at all and appeared in no number,
  // while being the bulk of the bytes (measured: 8.268 GB in that lump against 19 volumes reported as queued).
  //
  // The three numbers cannot be derived from one another, which is why all three are stated:
  // · volumes — the subject whenever it says anything the object count does not. One object split across hundreds of
  //   volumes is the ordinary shape of a big-file backup, and the volumes are what the temp disk is actually holding.
  // · objects — how many own those volumes, plus the ones owning no archive at all (a dedup hit, a resume hit, a raw
  //   in-place item), which is why it can exceed the volume count. A store-only run shows five figures of objects
  //   against few bytes, and that pairing is the answer to "should I worry about my temp disk" — no, the wire is.
  // · bytes — omitted when zero, which is a real state (see the objects note), not a rounding artefact.
  //
  // **Which of them leads** is the whole design of this entry, and it changed. The objects used to lead
  // unconditionally, with the volumes in the aside, and that put a 0 at the head of a sentence whose own parenthesis
  // said 269: "0 objects waiting for uploading (269 volumes on the staging disk, 26.260 GB)" — because an object with
  // any volume on the wire was struck from the count whole while its remaining volumes stayed in the aside. The
  // backend now keeps counting an object here for as long as it still owns a volume that is not moving (see
  // StageProgress.WaitingToUploadObjects), and the volumes lead, so the number in front is the one that cannot fall to
  // nothing while the disk is full. One consequence is deliberate: an object partway through its volumes appears here
  // *and* in the "N volumes uploading" above. Different units, both true — and the alternative was a sentence
  // contradicting its own parenthesis.
  //
  // The objects take the sentence back when the two counts are equal, since one volume per object means nothing was
  // split and the word would carry no information — and when there are no volumes at all, where they are all there is.
  //
  // "on the staging disk" now appears only when the objects **outnumber** the volumes, the one direction that invites
  // a division that means nothing: the two counts have different sources (objects from the item ledger, volumes and
  // bytes measured off the pool — StagingArea's per-lease file count), so fewer volumes than objects reads as "several
  // objects were merged into one volume", which never happens; one volume belongs to exactly one object. What it
  // really says is that the objects owning no archive never reached the disk to be counted, and naming where the
  // number was measured answers that in four words. The other direction needs no such defence — many volumes to one
  // object is just a split file, and saying so was only ever noise.
  //
  // In-flight volumes are excluded from the volumes and the bytes, so those two never overlap the "N volumes
  // uploading" above. The unsent tail of a volume on the wire is already on screen, per stream, as its sent/total.
  const byVolume =
    detail.waitingToUploadVolumes > 0 &&
    detail.waitingToUploadVolumes !== detail.waitingToUploadObjects
  const waitingSubject = byVolume
    ? `${withUnit(detail.waitingToUploadVolumes, 'volumes')}${
        detail.waitingToUploadObjects > detail.waitingToUploadVolumes ? ' on the staging disk' : ''
      }`
    : withUnit(detail.waitingToUploadObjects, unit)
  const waitingInner = [
    // The object term drops out at 0 rather than printing "(0 objects": that is the peer-waiter state — their volumes
    // are on the disk and counted, while the objects themselves are named in their own entry above — and writing the
    // zero would restate the very contradiction this entry was reshaped to remove.
    byVolume && detail.waitingToUploadObjects > 0 && withUnit(detail.waitingToUploadObjects, unit),
    detail.waitingToUploadBytes > 0 && formatBytes(detail.waitingToUploadBytes),
  ]
    .filter(Boolean)
    .join(', ')
  // The parenthesis sits between subject and verb rather than after it. It is the subject's own breakdown — who owns
  // these volumes, and how much they weigh — so it belongs with the noun; placed after the verb it trails a sentence
  // that has already ended, and reads as a fourth thing waiting.
  const waitingToUpload =
    detail.waitingToUploadObjects > 0 || detail.waitingToUploadBytes > 0
      ? `${waitingSubject}${waitingInner ? ` (${waitingInner})` : ''} waiting for uploading`
      : ''
  const idleOnStaging = detail.activeItems.length === 0 && detail.preparing > 0
  // The verb follows each stream's own wire flag (does it cross the network?), stated by the backend at
  // registration. It replaced two generations of guessing: a per-stage verb called the repair's local hash
  // read "downloading" (an Archive-tier operator read that as an unconsented rehydration and stopped a
  // healthy repair), and a label-prefix guess broke the day repair uploads adopted source-path labels
  // ("5 objects hashing" over five parallel uploads). Absent flag counts as wire — every transfer-side
  // registration sends true, and only the two local-read sites say false.
  const repairUploading =
    detail.stage === 'Repairing' &&
    detail.activeItems.length > 0 &&
    detail.activeItems.every((a) => a.wire !== false)
  // Diffing's content reads register per file exactly like the repair's hash gate, and the fallback verb
  // ('downloading') is just as false for them.
  const inFlightVerb =
    detail.stage === 'Uploading' || repairUploading
      ? 'uploading'
      : detail.stage === 'Repairing' || detail.stage === 'Diffing'
        ? 'hashing'
        : 'downloading'
  // The in-flight number's unit **differs by direction**:
  // · upload: VolumeBlobIO registers one entry per volume, so a single large item can occupy the whole
  //   concurrency allowance (5 by default);
  // · download: RestoreOrchestrator / BackupChecker register one per object (or group), shared by its volumes.
  // Spelling out the unit has a concrete consequence: processed and queued on the same line count
  // **items**, so adding a volume count to them exceeds the total — measured 5,346 + 5 + 1,031 = 6,382
  // > 6,378, the extra 4 being "5 volumes − 1 item".
  // "5 volumes uploading" / "1 volume uploading" / "3 objects downloading"
  const inFlightPhrase = `${withUnit(
    detail.activeItems.length,
    detail.stage === 'Uploading' || repairUploading ? 'volumes' : unit,
  )} ${inFlightVerb}`
  const downloading = detail.stage === 'Restoring' || detail.stage === 'Verifying'
  // Counts and bytes go on **one timeline** — that is the entire point of this reordering. With counts
  // on one line and bytes on another, each ordered by its own logic, nothing on screen could say whether
  // "+2.0 GB unfinished" and "100 MB on disk" were two halves of one item (they are not: the former's
  // bytes are already in the cloud, the latter has not left, and a whole upload separates them).
  // Those two numbers really were questioned at length, when the answer only needed them laid out in order.
  //
  // Ordered **backwards along the timeline**: closest to "this item is done" first, earliest last,
  // ending at queued. An item's forward order is queued → waiting for the compressor → waiting for staging
  // room (the pool's byte ceiling) → waiting for the archive slot (the global production lock) → preparing
  // (holding it) → the archive lands on disk → checking (recheck / clearing leftover cloud volumes) →
  // waiting for uploading → starting upload → waiting on peer → uploading → on the cloud → settled into
  // the line above. Read the array below in reverse and that is it.
  //
  // Three of those entries sit at **one** point of that order — the wait for an uploader — and are separate only
  // because what they are waiting to have done to them differs: an archive on the staging disk waits to be uploaded,
  // a raw in-place item waits to be uploaded from the source, and a dedup or resume hit waits only to be recorded,
  // having nothing to send. They are kept adjacent and in that order, decreasing by what is left to do.
  //
  // The bytes ride with the stage that owns them rather than forming a stage of their own: the upload wait's
  // are in its parentheses, checking's are the entry below it, and the bytes of what is actually on the wire
  // are per stream in the in-flight list, as each stream's sent/total.
  //
  // to go and buffered to disk land at the end: the former is source bytes not yet restored on the
  // download side, the latter is how far the diff has run ahead (queued, not yet picked up). Both sit at
  // the earliest end of the timeline.
  const pipeline = [
    // Bytes in the cloud whose item has not settled. No longer worded "uploaded in unfinished objects" —
    // the line already starts with "In flight", so repeating unfinished is redundant; "on the cloud"
    // states where they are.
    detail.unfinishedItemBytes > 0 && `+${formatBytes(detail.unfinishedItemBytes)} on the cloud`,
    // No "N volumes uploading" here: the in-flight heading above the per-stream rows says exactly that
    // sentence, and repeating it word for word on the next line read as two different numbers to check
    // against each other. The pipeline starts at the first thing the heading does NOT say.
    // "right now" rather than "yet": this says **at this instant** nothing is in flight (the item in hand
    // holds the compression lock), not "it has not started". Mid-run the line above already shows
    // terabytes accumulated, so "yet" would be false.
    idleOnStaging && 'nothing on the wire right now',
    detail.waitingOnPeer > 0 &&
      `${withUnit(detail.waitingOnPeer, unit)} waiting on the same content elsewhere`,
    // The upload-side wait, assembled above where the reasoning is set out. Nothing here is "starting upload", and that
    // distinction is the entry's whole point: not one thing is being done to any of it. Folded into the tier below,
    // this was a number that climbed all run and never came back down, describing items that were neither starting nor
    // uploading.
    //
    // It describes **the staging disk** and nothing else — every figure in it is measured off the pool, which is what
    // makes "should I worry about my temp disk" answerable from it alone. The two entries after it are the items that
    // wait at this same point owning nothing there, and they used to be counted in here: that is what made this entry
    // read as five figures of objects against a handful of volumes, a ratio that looks like a merge and was really
    // three populations printed as one.
    waitingToUpload,
    // Waiting to upload, but not off the staging disk: the raw in-place route sends the user's own file from where it
    // already sits, so it is bounded by nothing the pool knows and has no volume to be counted as. On a store-only,
    // unencrypted run (a media library, which is what that route exists for) this is the ordinary path, not a corner.
    detail.awaitingInPlace > 0 &&
      `${withUnit(detail.awaitingInPlace, unit)} waiting to upload in place`,
    // And the ones with nothing to send at all: a dedup hit or a resume hit, whose content is already stored — by an
    // earlier version, or by this run's own earlier attempt. Waiting for an uploader whose entire job for them is the
    // index entry and the journal record, which is why the verb cannot be "uploading".
    //
    // Unbounded by construction and the likeliest five-figure number on this line: the hand-off channel behind it is
    // deliberately not depth-limited (whatever owns an archive is bounded in bytes by the staging pool, the limit the
    // operator set), and these own no archive at all, so a mostly-unchanged run can queue the whole dataset here.
    // A big number is the pipeline working as designed — and now it says so, instead of claiming a full temp disk.
    detail.awaitingRecording > 0 &&
      `${withUnit(detail.awaitingRecording, unit)} waiting to be recorded`,
    detail.activeItems.length === 0 && stalled > 0 && `${withUnit(stalled, unit)} starting upload`,
    // The disk-checking stretches (dedup pre-check full read, per-member stat before and after packing,
    // clearing leftover cloud volumes). They emit not one progress event, and the heartbeat only runs
    // while something is in flight — unreported, the screen shows a motionless "1 object starting
    // upload" for minutes, which is neither starting nor uploading.
    detail.checking > 0 && `${withUnit(detail.checking, unit)} checking files`,
    // Of those, the bytes already compressed onto disk but not yet cleared to upload. The stretches
    // where no archive exists yet (the dedup pre-check reading the source, the pre-compression stat
    // sweep) report 0 here — nothing is in the pool at that point.
    detail.checkingBytes > 0 && `${formatBytes(detail.checkingBytes)} being checked`,
    detail.preparing > 0 && `${withUnit(detail.preparing, unit)} ${preparingLabel}`,
    // Queuing behind that global archive lock. The wording deliberately avoids compressing/compressor:
    // the lock protects "producing this item's volume files", and store-only only packs while a raw
    // passthrough never runs 7z at all — all three take the same lock.
    // The diagnosis comes free, and it only holds because the pool's byte ceiling was split into the entry
    // below rather than reported here: preparing=1 with this non-zero means the lock is your own and the
    // queue is moving; preparing=0 with this non-zero means another run holds it, and you can go stop that one.
    detail.waitingOnArchive > 0 &&
      `${withUnit(detail.waitingOnArchive, unit)} waiting for the archive slot`,
    // One step before the lock, and the far commoner of the two: the staging pool is at its byte ceiling and no
    // compression may start until an **upload** frees space. Both used to be reported as the archive slot, and that
    // made the diagnosis above actively wrong — a run whose pool is full shows preparing=0 with someone waiting, which
    // the archive-slot entry says means another run holds the lock, and it sends the operator off to stop a backup
    // that is not in the way. Split out, the pair reads straight: the archive slot points at a producer, this one
    // points at the wire, and this one is where an upload-bound run spends most of its life.
    detail.waitingOnRoom > 0 &&
      `${withUnit(detail.waitingOnRoom, unit)} waiting for staging room`,
    // The front of the pipeline, and the only part of this line a hold freezes. Three entries, of which
    // at most two ever render: without a hold the original pair, with one a single entry replacing both.
    //
    // They collapse because the hold makes them one population — not started, and not going to be while it
    // stands — so the distinction the split exists for ("is compression the bottleneck?") stops having an
    // answer. And their own wording promises motion that the row directly above has just denied: an
    // operator watching "Suspending…" was still being told 4,365 objects were queued, as if a queue that
    // nothing was consuming were merely deep.
    //
    // Under a wind-down it is worse than stale. `queued` is a subtraction — enqueued minus processed minus
    // in-hand (StageProgress.Tick) — and draining the probed channel releases each item's share **without**
    // marking it processed, so abandoned work migrates into this number and it climbs while the run winds
    // down. The count is the one honest thing left in it; every verb around it was wrong.
    //
    // The two holds do different things to the same queue and cannot share a word: a wind-down abandons it
    // for this run, a pause merely holds it. "left for the next run" is true of every stop kind and not
    // only Suspend — SettleStopAsync flushes the journal before it returns whatever the kind, and a
    // Canceled run's row offers Resume off that journal (see showsInterruptedNotice). What a stop discards
    // is the index version, not the work already done.
    hold &&
      notStarted > 0 &&
      `${withUnit(notStarted, unit)} ${
        hold === 'winding-down' ? 'left for the next run' : 'held by the pause'
      }`,
    // Earlier than either of the two above, and not the same wait as either. These have been probed — their content
    // identity is settled — but they have not reached the staging area at all, because the compressor is a single
    // worker and takes one item at a time. Capped at the channel's depth (9), so unlike its counterpart above this
    // one plateaus — and the cap is deliberately small enough that plateauing says little: nine is a display
    // decision, because a deep queue here reads as "compression is the bottleneck" when usually it is the
    // opposite (the compressor is held by staging backpressure, waiting on the uploaders).
    !hold &&
      detail.awaitingCompression > 0 &&
      `${withUnit(detail.awaitingCompression, unit)} waiting for the compressor`,
    !hold && detail.queued > 0 && `${withUnit(detail.queued, unit)} queued`,
    downloading && detail.workRemaining > 0 && `${formatBytes(detail.workRemaining)} to go`,
    // The diff decides orders of magnitude faster than compression and upload can consume, so running
    // ahead is normal; the surplus is buffered to disk until the downstream catches up. This replaced
    // the old "waiting for upload to catch up": the write side no longer blocks, so what needs saying
    // is not "it is stuck" but "it is this far ahead". The word buffered is deliberate — this must not
    // read as a failed retry.
    detail.spilledItems > 0 && `${withUnit(detail.spilledItems, unit)} buffered to disk`,
  ]
    .filter(Boolean)
    .join(' · ')
  // The gate is "is anything in flight", not a numeric threshold: a stalled transfer is driven to 0 by
  // the heartbeat (see StageTracker.Tick), and that is precisely the signal to show — a real stall should
  // read "0 B/s" rather than making this segment vanish, leaving no way to tell "not transferring" from
  // "hung". All three stages that register in-flight items (Uploading, Restoring, Verifying) report bytes
  // as they go — downloads carry per-volume progress too (see VolumeBlobIO.DownloadAsync) — so they are
  // symmetric and Uploading no longer needs singling out. The other seven never call BeginItem, so
  // activeItems is always empty and the condition degrades to the original bytesPerSecond > 0, unchanged.
  const speed =
    detail.bytesPerSecond > 0 || detail.activeItems.length > 0
      ? ` · ${formatBytes(detail.bytesPerSecond)}/s`
      : ''
  // Computed from the seconds rather than slicing the estimatedRemaining string — see formatDuration for why.
  const eta = detail.etaSeconds !== null ? ` · ~${formatDuration(detail.etaSeconds)} left` : ''

  // The first line carries only bytes that have **settled**: the completion fraction and what was
  // really uploaded. Everything in motion belongs to the timeline below. The dividing line is "can
  // this still change?" — above it cannot, below it all still can.
  //
  // Uploading and downloading are opposite directions and cannot share wording: uploading is
  // "compress, then send", downloading is "pull down, then write out". The download side also knows
  // its total up front (volume sizes are in the index), while the upload side only learns it after
  // compressing and can report only what is complete.
  const done = (
    downloading
      ? [
          // Downloaded / total: the denominator comes from the index; older indexes without volume sizes report 0, so only the numerator is shown.
          detail.transferredBytes > 0 &&
            (detail.transferTotal > 0
              ? `${formatBytes(detail.transferredBytes)} / ${formatBytes(detail.transferTotal)} downloaded`
              : `${formatBytes(detail.transferredBytes)} downloaded`),
          // Source bytes fully settled (restored to disk / verified against the index), as a fraction of the
          // declared workload with its byte-based percentage — the honest progress for a stage whose groups
          // range from one 100 GB file to a box of hundreds of small ones. The verb follows the stage: these
          // two lines share every mechanism but do opposite things with the bytes.
          detail.workTotal > 0
            ? `${formatBytes(detail.workDone)} / ${formatBytes(detail.workTotal)} ${
                detail.stage === 'Verifying' ? 'verified' : 'restored'
              }${detail.workPercent != null ? ` (${detail.workPercent}%)` : ''}`
            : detail.workDone > 0 &&
              `${formatBytes(detail.workDone)} ${detail.stage === 'Verifying' ? 'verified' : 'restored'}`,
        ]
      : [
          // Completed and total **source** bytes, pre-compression. A fraction only means something when
          // both sides share a basis — using transferred bytes as the numerator does not work: the
          // denominator (the compressed total) does not exist until compression has run, and the ratio
          // swings wildly with file type, so a cross-basis proportion says nothing at all.
          //
          // The original / compressed pair matches the Original Size / Compressed Size convention of
          // compression tools. Calling this one "uploaded" was wrong: it suggested the amount sent, when
          // these two numbers state **how large the original files are** — and for incompressible content
          // the two are nearly equal, which hid the basis completely.
          //
          // The completion percentage follows this fraction, since it is computed from exactly these two
          // numbers; side by side, nobody can misread it.
          detail.workTotal > 0 &&
            `${formatBytes(detail.workDone)} / ${formatBytes(detail.workTotal)} original${
              detail.workPercent != null ? ` (${detail.workPercent}%)` : ''
            }`,
          // The bytes this run actually pushed, followed by their ratio to the original size.
          //
          // The wording went through four rounds, each pointing at the same thing — this number's
          // **basis** is invisible:
          // · "stored" read as "how much is in the cloud in total" (it is per-run, like the rest of the line);
          // · how it differed from the fraction before it was equally invisible — both end in GB while one
          //   is original size and the other is what went over the wire, and for incompressible content
          //   (media, already-compressed files) they are nearly equal, so it looked like a repetition;
          // · "on the wire" was worse — the line below already uses that phrase in "nothing on the wire
          //   right now", one saying none and the other saying 2 GB;
          // · "compressed" contradicts itself: with store-only plus encryption, 7z wrapping and AES make
          //   the result **larger** than the input, as do archive headers for small files, producing
          //   "compressed (105%)".
          //
          // Back to "uploaded" — renaming the fraction to original freed the word, and it was always the
          // most accurate: what went out is what went out, presuming neither growth nor shrinkage. Over
          // 100% still reads correctly (105% of the original was uploaded), and that is exactly what the
          // user needs to know: configured this way, the cloud costs more than the source.
          detail.transferredBytes > 0 &&
            `${formatBytes(detail.transferredBytes)} uploaded${
              detail.workDone > 0
                // The parenthesis must carry "of original". A bare (95%) reads as "upload progress
                // 95%" — and the same line **already has** a real progress percentage next to it.
                ? ` (${Math.round((100 * detail.transferredBytes) / detail.workDone)}% of original)`
                : ''
            }`,
        ]
  )
    .filter(Boolean)
    .join(' · ')
  // The in-flight list's heading, owned here so the caller cannot rebuild the sentence and drift. The
  // "in parallel" suffix is a claim about transfers (they really do run concurrently) and must not be
  // made over hashing: reading files for their hash is serial by design — one file at a time — and
  // "1 object hashing in parallel" was read as a statement that hashes run concurrently.
  const inFlightHeading = `${inFlightPhrase}${inFlightVerb === 'hashing' ? '' : ' in parallel'}:`
  return { counts, done, pipeline, speed, eta, inFlightPhrase, inFlightHeading, label }
}

/**
 * What the local-CPU phase is called for a given stage. Exported so the count and the row naming the item
 * cannot drift apart — they are two halves of one statement, and "1 object preparing" above a row that says
 * "extracting" reads as two different things happening.
 *
 * The wording avoids "compressing": a backup configured not to compress still goes through 7z (encryption,
 * packing, volume splitting), so compressing would be wrong. Restore and verify really are extracting.
 */
export function preparingLabelOf(stage: string): string {
  // Repairing joins Uploading: its preparing stretch is 7z producing the replacement volumes, the same
  // work the upload stage calls preparing — "extracting" would claim the opposite direction.
  return stage === 'Uploading' || stage === 'Repairing' ? 'preparing' : 'extracting'
}
