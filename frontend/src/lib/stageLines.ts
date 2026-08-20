import type { StageProgress } from '../api/backupConfigs'
import { formatBytes, formatDuration } from '../constants/format'

const STAGE_UNITS: Record<string, string> = {
  Scanning: 'entries',
  Diffing: 'files',
  Uploading: 'objects',
  Restoring: 'objects',
  // The check stages. Cloud counts stored objects (one HEAD per pack), Verifying counts packs that get
  // downloaded, extracted and re-hashed, and Local counts index entries — the three differ by orders of
  // magnitude and cannot share one word.
  Cloud: 'objects',
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
 * Split one stage snapshot into the two lines on screen.
 *
 * Extracted so it can be tested: the entire difficulty of these two lines is **order** and **wording**,
 * and both can otherwise only be confirmed by reading the code — once a string is inside JSX there is
 * nowhere left to assert it. A wrong order raises no error, it just regrows the endless "which of these
 * two numbers comes first?" question on screen.
 */
export function stageLines(detail: StageProgress) {
  const unit = STAGE_UNITS[detail.stage] ?? 'items'
  const label = STAGE_LABELS[detail.stage] ?? detail.stage
  // "of" rather than "/": a slash is fraction notation, and putting one there invites a percentage —
  // while an item-count percentage means very little during upload (one item may be a 6.8 GB single file
  // or a pack of several hundred 5 KB files). Real completion is by bytes, in the "60.618 GB / 191.000 GB
  // original (31%)" on the next line, which does use a slash because its percentage follows it and the
  // fraction is one you can legitimately form.
  const counts =
    detail.total > 0
      ? `${detail.processed.toLocaleString()} of ${detail.total.toLocaleString()} ${unit}`
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
  const preparingLabel = detail.stage === 'Uploading' ? 'preparing' : 'extracting'
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
  // · objects — how many items are stuck here. Can exceed the volume count: a dedup hit, a resume hit and a raw
  //   in-place item own no archive at all, so a store-only run shows five figures of objects against few bytes, and
  //   that pairing is the answer to "should I worry about my temp disk" — no, the wire is the bottleneck.
  // · volumes — omitted when equal to the objects, since one volume per object means nothing was split and the word
  //   would carry no information.
  // · bytes — omitted when zero, which is a real state (see the objects note), not a rounding artefact.
  //
  // In-flight volumes are excluded from all three, so this entry and the "N volumes uploading" above it never overlap.
  // The unsent tail of a volume on the wire is already on screen, per stream, as that stream's sent/total.
  const waitingInner = [
    detail.waitingToUploadVolumes > 0 &&
      detail.waitingToUploadVolumes !== detail.waitingToUploadObjects &&
      withUnit(detail.waitingToUploadVolumes, 'volumes'),
    detail.waitingToUploadBytes > 0 && formatBytes(detail.waitingToUploadBytes),
  ]
    .filter(Boolean)
    .join(', ')
  const waitingToUpload =
    detail.waitingToUploadObjects > 0 || detail.waitingToUploadBytes > 0
      ? `${withUnit(detail.waitingToUploadObjects, unit)}${waitingInner ? ` (${waitingInner})` : ''}`
      : ''
  const idleOnStaging = detail.activeItems.length === 0 && detail.preparing > 0
  const inFlightVerb = detail.stage === 'Uploading' ? 'uploading' : 'downloading'
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
    detail.stage === 'Uploading' ? 'volumes' : unit,
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
    detail.activeItems.length > 0 && inFlightPhrase,
    // "right now" rather than "yet": this says **at this instant** nothing is in flight (the item in hand
    // holds the compression lock), not "it has not started". Mid-run the line above already shows
    // terabytes accumulated, so "yet" would be false.
    idleOnStaging && 'nothing on the wire right now',
    detail.waitingOnPeer > 0 &&
      `${withUnit(detail.waitingOnPeer, unit)} waiting on the same content elsewhere`,
    // The whole upload-side wait, assembled above where the reasoning is set out. Nothing here is "starting upload",
    // and that distinction is the entry's whole point: not one thing is being done to any of it. Folded into the tier
    // below, this was a number that climbed all run and never came back down, describing items that were neither
    // starting nor uploading.
    //
    // It is unbounded by construction and can reach five figures. The hand-off channel behind it is deliberately not
    // depth-limited — whatever owns an archive is already bounded in bytes by the staging pool, which is the limit the
    // operator set — but a dedup hit, a resume hit and a raw in-place item own no archive, so on a store-only workload
    // the compressor can queue the whole dataset here while the uploaders trickle. A big number is the pipeline working
    // as designed; what it tells the operator is that the bottleneck is the wire, not the CPU.
    waitingToUpload && `${waitingToUpload} waiting for uploading`,
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
    // Earlier than either of the two above, and not the same wait as either. These have been probed — their content
    // identity is settled — but they have not reached the staging area at all, because the compressor is a single
    // worker and takes one item at a time. Capped at the channel's depth (9), so unlike its counterpart above this
    // one plateaus — and the cap is deliberately small enough that plateauing says little: nine is a display
    // decision, because a deep queue here reads as "compression is the bottleneck" when usually it is the
    // opposite (the compressor is held by staging backpressure, waiting on the uploaders).
    detail.awaitingCompression > 0 &&
      `${withUnit(detail.awaitingCompression, unit)} waiting for the compressor`,
    detail.queued > 0 && `${withUnit(detail.queued, unit)} queued`,
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
          // Restored: source bytes written back out after extraction. What has not been restored (to go) sits at the end of the timeline below.
          detail.workDone > 0 && `${formatBytes(detail.workDone)} restored`,
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
  // Hand back inFlightPhrase rather than unit + inFlightVerb: the in-flight list's heading
  // ("5 volumes uploading in parallel:") assembles exactly the same sentence, and letting the caller
  // build it again would copy the "upload counts volumes, download counts items" rule into a second
  // place — change one, miss the other.
  return { counts, done, pipeline, speed, eta, inFlightPhrase, label }
}
