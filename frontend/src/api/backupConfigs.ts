import { api } from './client'
import type { LocalRootPreview } from '../lib/localRootVerdict'
import type { StopRequested } from '../lib/windDownControls'

// Mirrors the backend enum (System.Text.Json serialises it as a number by default)
export const StorageTier = { Hot: 0, Cool: 1, Cold: 2, Archive: 3 } as const
export const RetentionMode = {
  VersionOnly: 0,
  TimeOnly: 1,
  EitherTriggers: 2,
  BothRequired: 3,
} as const

export const tierLabels: Record<number, string> = {
  0: 'Hot',
  1: 'Cool',
  2: 'Cold',
  3: 'Archive',
}

export const retentionModeLabels: Record<number, string> = {
  0: 'By version count only',
  1: 'By age only',
  2: 'Either triggers',
  3: 'Both required',
}

// Stages of the backup pipeline
export const BackupStage = {
  Scanning: 0,
  Diffing: 1,
  Uploading: 2,
  WritingIndex: 3,
  Finalizing: 4,
  CleaningUp: 5,
  Completed: 6,
} as const

export const backupStageLabels: Record<number, string> = {
  [BackupStage.Scanning]: 'Scanning',
  [BackupStage.Diffing]: 'Diffing',
  [BackupStage.Uploading]: 'Uploading',
  [BackupStage.WritingIndex]: 'Writing index',
  [BackupStage.Finalizing]: 'Finalizing',
  [BackupStage.CleaningUp]: 'Cleaning up',
  [BackupStage.Completed]: 'Completed',
}

// Persistent status (§4.2, decision 2): Normal/Error only. Transient states are in BackupActivity (derived, never stored).
export const BackupStatus = { Normal: 0, Error: 1 } as const

// Restore conflict mode (§4.1c, decision 3; values match the backend enum)
export const RestoreConflictMode = { OverwriteIfChanged: 0, Skip: 1, RenameKeep: 2 } as const
export const restoreConflictModeLabels: Record<number, string> = {
  0: 'Overwrite if changed',
  1: 'Skip existing',
  2: 'Keep existing (rename)',
}

// Archive rehydrate priority (values match the backend enum)
export const RestoreRehydratePriority = { Standard: 0, High: 1 } as const
export type BackupActivity = 'Idle' | 'BackingUp' | 'Restoring' | 'Checking' | 'Repairing' | 'CleaningUp'

/** The readable form, used inside a sentence ("Currently backing up — …"). The counterpart of the backend's Humanize. */
export const activityLabels: Record<BackupActivity, string> = {
  Idle: 'idle',
  BackingUp: 'backing up',
  Restoring: 'restoring',
  Checking: 'checking',
  Repairing: 'repairing',
  CleaningUp: 'cleaning up',
}

/**
 * The standalone form used on a badge. activityLabels cannot be reused: that set is lowercase for
 * mid-sentence use and reads as half a sentence on a badge. Nor can the activity itself be printed —
 * that is the backend's enum name, and camel case like "BackingUp" / "CleaningUp" on screen is an
 * implementation detail leaking out, not copy.
 */
export const activityBadgeLabels: Record<BackupActivity, string> = {
  Idle: 'Idle',
  BackingUp: 'Backing Up',
  Restoring: 'Restoring',
  Checking: 'Checking',
  Repairing: 'Repairing',
  CleaningUp: 'Cleaning Up',
}

/** The effective values after backend resolution (null fields filled in from the global settings). Read-only, for display. */
export interface EffectiveBackupSettings {
  ignoreRules: string | null
  dontCompressRules: string | null
  dontGroupRules: string | null
  // The case-insensitive half of each list: `*.mp4` there also matches .MP4/.Mp4, while paths stay literal.
  ignoreRulesCaseInsensitive: string | null
  dontCompressRulesCaseInsensitive: string | null
  dontGroupRulesCaseInsensitive: string | null
  crossDirGroupRulesCaseInsensitive: string | null
  // Matching paths may be packed across directory boundaries. Empty = pack strictly by directory (the historical behaviour).
  crossDirGroupRules: string | null
  includeSymlinks: boolean
  maxVersions: number
  maxAgeDays: number
  retentionMode: number
  singleFileThresholdBytes: number
  groupCapBytes: number
  volumeBytes: number | null
  verboseLogging: boolean
}

export interface BackupConfig {
  id: number
  accountId: number
  containerName: string
  name: string
  description: string | null
  localRoot: string
  hasPassword: boolean
  indexTier: number
  dataTier: number
  ignoreRules: string | null
  dontCompressRules: string | null
  dontGroupRules: string | null
  // The case-insensitive half of each list: `*.mp4` there also matches .MP4/.Mp4, while paths stay literal.
  ignoreRulesCaseInsensitive: string | null
  dontCompressRulesCaseInsensitive: string | null
  dontGroupRulesCaseInsensitive: string | null
  crossDirGroupRulesCaseInsensitive: string | null
  // Matching paths may be packed across directory boundaries. Empty = pack strictly by directory (the historical behaviour).
  crossDirGroupRules: string | null
  /** Backup scope. null = everything under the root. Not inheritable, so it does not appear in EffectiveBackupSettings. */
  scopeRules: string | null
  includeSymlinks: boolean | null
  maxVersions: number | null
  maxAgeDays: number | null
  retentionMode: number | null
  singleFileThresholdBytes: number | null
  groupCapBytes: number | null
  volumeBytes: number | null
  verboseLogging: boolean | null
  effective: EffectiveBackupSettings
  createdAt: string
  status: number // BackupStatus
  lastError: string | null
  lastErrorAt: string | null
  activity: BackupActivity
  secretsUnavailable: boolean
  /**
   * A path that must exist before this backup runs; null = no precondition. It exists because an unmounted
   * root is not an absent root: the mount point is still there with nothing under it, so the diff concludes
   * every file was deleted and records a version in which the whole backup has vanished. Point it at
   * something that only appears after the mount.
   */
  sentinelPath: string | null
}

export interface BackupConfigInput {
  accountId: number
  containerName: string
  name: string
  description: string | null
  localRoot: string
  password: string | null
  indexTier: number
  dataTier: number
  ignoreRules: string | null
  dontCompressRules: string | null
  dontGroupRules: string | null
  // The case-insensitive half of each list: `*.mp4` there also matches .MP4/.Mp4, while paths stay literal.
  ignoreRulesCaseInsensitive: string | null
  dontCompressRulesCaseInsensitive: string | null
  dontGroupRulesCaseInsensitive: string | null
  crossDirGroupRulesCaseInsensitive: string | null
  // Matching paths may be packed across directory boundaries. Empty = pack strictly by directory (the historical behaviour).
  crossDirGroupRules: string | null
  /** Backup scope. null = everything under the root. Not inheritable, so it does not appear in EffectiveBackupSettings. */
  scopeRules: string | null
  includeSymlinks: boolean | null
  maxVersions: number | null
  maxAgeDays: number | null
  retentionMode: number | null
  singleFileThresholdBytes: number | null
  groupCapBytes: number | null
  volumeBytes: number | null
  verboseLogging: boolean | null
  /** See BackupConfig.sentinelPath. A blank is normalised to null by the backend, which is how it is cleared. */
  sentinelPath: string | null
}

// The validation report for a local-root migration (the backend's LocalRootPreviewResponse).
// The shape is declared in lib/localRootVerdict.ts next to the decision logic; this only re-exports it.
export type { LocalRootPreview } from '../lib/localRootVerdict'

// What a stage is currently doing. Stages other than upload used to have no progress at all — scanning
// and diffing each reported once on entry, and a first backup's diff reads every file end to end to
// hash it, which can run for hours.
/** One transfer in flight. label is the **source file path** (upload) or a pack's description, never the content-addressed blob name. */
export interface ActiveTransfer {
  label: string
  sent: number
  total: number // 0 = unknown (a download does not know until the response headers arrive)
  percent: number | null
}

export interface StageProgress {
  stage: string
  processed: number
  total: number // 0 = total unknown (the scan has not finished)
  bytes: number // Accumulated as it transfers, **including in flight** — the one used for speed
  currentItem: string | null
  activeItems: ActiveTransfer[]
  bytesPerSecond: number
  preparing: number // Holding the global archive lock and producing volume files (can last tens of seconds) — by the lock's definition, only ever 0 or 1
  // What that one item is: a source path, or the directories a pack's members come from. Null when nothing
  // is preparing, and also when the caller had nothing to say — the count then stands alone, as it did before
  // there was a row for it. One string rather than a list because the archive lock is global.
  preparingItem: string | null
  // Source size of preparingItem; 0 when there is none worth stating (a pack, whose label carries a member
  // count instead). A number rather than text, so this row size-formats the way the transfer rows beside it do.
  preparingBytes: number
  queued: number // Not yet picked up — the queue only (those waiting on the archive lock are in waitingOnArchive)
  // Items picked up by a worker and queuing behind the **archive lock**. That lock is global
  // (StagingArea is a singleton, so production does not run concurrently across backups either), so one
  // backup's threads can sit entirely behind a lock held by **another backup** — and then this backup's
  // preparing is 0. Folded into queued, the screen would show ten thousand "queued" and nothing able to
  // say "it is blocked by someone else".
  // Split out, the diagnosis is free: preparing=1 with waiters means the lock is your own;
  // preparing=0 with waiters means someone else holds it.
  waitingOnArchive: number
  // Inside the staging area but not even at the lock yet: parked on the pool's byte ceiling, waiting for an **upload**
  // to free space. Split out of waitingOnArchive because the two point at opposite culprits — the lock means another
  // producer is packing (possibly another backup's, which you can go stop), room means the wire is the bottleneck and
  // compression is being throttled on purpose. Reported as one number, a full pool looked exactly like a foreign lock,
  // which is the state an upload-bound run spends most of its time in.
  waitingOnRoom: number
  percent: number | null
  etaSeconds: number | null // Seconds remaining, extrapolated by the backend from the whole-run average; estimatedRemaining derives from it
  estimatedRemaining: string | null // A .NET TimeSpan serialised as "hh:mm:ss"
  // The byte breakdown. The segments never overlap: workRemaining (source bytes not yet processed), the two
  // post-compression on-disk ones (waitingToUploadBytes with nothing on the wire, checkingBytes not yet cleared to
  // travel), the in-flight volumes in activeItems, transferredBytes (in the cloud with the item complete) and
  // unfinishedItemBytes (in the cloud with the item unfinished).
  // workDone/workTotal are pre-compression, and give the real completion figure.
  workTotal: number // Total source bytes (pre-compression). During upload it keeps growing until the diff finishes
  workDone: number // Of those, the ones fully finished (excluding in flight)
  workRemaining: number
  transferredBytes: number // Bytes that finished items actually pushed over the wire (post-compression, excluding in flight)
  // Bytes in the cloud whose item has not finished (post-compression). A large item is split into many
  // volumes, and when the first few complete those bytes really are in the cloud — but the item is not
  // done, so they cannot enter transferredBytes (which is kept per item, to line up with the per-item
  // workDone) and they are no longer on the staging disk either (the pool releases per volume). They fold
  // into the former and reset to zero when the item completes.
  unfinishedItemBytes: number
  // Everything on the staging disk with **not one byte on the wire** — this run's pool occupancy minus the whole size
  // of every in-flight volume and minus what checking still holds. Together with the two counts below it is one
  // population reported three ways, and it is the single "waiting for uploading" entry on screen.
  //
  // In-flight volumes come out whole rather than by the part already sent. Their files really do lie in the pool in
  // full until the transfer completes, but this figure claims nothing of it is moving, and the unsent tail of a volume
  // on the wire is already visible per stream as the sent/total of its activeItems entry.
  //
  // It replaced a two-entry split — queued for an uploader versus in an uploader's hands — divided by **ownership**,
  // which is an implementation detail no operator can act on, and which counted neither the volumes an uploader owns
  // but has not started: the per-item sliding window opens no task for those, so they queued nowhere at all.
  waitingToUploadBytes: number
  // How many volume files those bytes are spread across. Not derivable from either neighbour: an archive's volumes are
  // uniform except for its remainder, and one run mixes archives of wildly different sizes. It leads the rendered line,
  // and drops out of it when it equals waitingToUploadObjects — one volume per object means nothing was split.
  waitingToUploadVolumes: number
  // And how many **objects** own them: parked in the hand-off channel, or past staging and still holding a volume no
  // stream has opened. From the item ledger rather than by counting archives on disk, because the three entry kinds
  // that own no archive at all (a dedup hit, a resume hit, a raw in-place item) have to keep showing here — "many
  // objects, few bytes" is what says the wire is the constraint and the temp disk is not.
  // An object partway through its volumes counts here **and** shows in activeItems: it really is doing both, and
  // striking it out whole is what made a single big file read as "0 objects waiting (269 volumes on the staging disk)".
  // Peer waiters are excluded (they have their own entry naming the reason) while their archives still count in the two
  // figures above, whose basis is "on the disk, not on the wire" rather than "which object owns it".
  waitingToUploadObjects: number
  // Parked for an uploader owning no archive, but with the whole source file still to send from where it already sits
  // (the raw in-place route). Its own entry because the pool cannot describe it — never staged, never charged, so it
  // has no volumes and no bytes in the three figures above, and folded into their object count it read as an object
  // waiting on a staging disk it never touched.
  awaitingInPlace: number
  // Parked for an uploader with **nothing to send at all**: a dedup hit or a resume hit, whose content is already
  // stored by an earlier version or by this run's own earlier attempt. What remains is the index entry and the journal
  // record. This is the population that made the upload wait unreadable — on a store-only or mostly-unchanged run it
  // is five figures against a handful of volumes, and while it counted as "waiting for uploading" the entry claimed a
  // temp disk full of work that did not exist. It never appears in activeItems either: no bytes, so no stream.
  awaitingRecording: number
  // Bytes compressed onto disk but still in checking and not cleared to upload (the byte side of
  // checking, already subtracted from waitingToUploadBytes, as are its volumes).
  // Output is booked against backpressure the moment compression ends (that ledger needs "how much is on
  // the disk right now", and booking it a second later risks blowing the temp disk), but it still has to
  // pass the post-compression recheck — and if that finds a member changed during compression, the whole
  // archive is **discarded and recompressed**, sending not one byte. Calling that stretch "ready to
  // upload" over-promises, hence its own entry.
  checkingBytes: number
  transferTotal: number // How many bytes this stage will send in total; 0 = unknown (the upload side only learns it after compressing, so it is always 0)
  workPercent: number | null // Completion by source bytes; null until the total is settled
  // The diff decides orders of magnitude faster than compression and upload can consume, so it always
  // runs ahead; the surplus is buffered to disk (cumulative for the run, only ever growing).
  // This used to be a boolean "blocked by the queue" — the write side no longer blocks, the diff runs to
  // completion, and only then does the upload's remaining time have a denominator (the total is settled
  // only when the diff finishes). This number quantifies that.
  spilledItems: number
  // **Items** past the compression and staging stage: compression finished long ago, and from here they
  // either have volumes in flight or are stuck in one of the three tiers below.
  // A different basis from activeItems — that holds **volumes**, and one item can have several in flight
  // or none at all.
  // processed + preparing + queued + waitingOnRoom + waitingOnArchive + awaitingCompression + awaitingUpload +
  // uploading ≡ total is an identity, and the numbers on screen have to add up to it. They did not: an item stuck
  // in this stretch was counted by nothing, and could only be found by lining up several screenshots and
  // doing subtraction.
  uploading: number
  // Claimed but idle in one of the pipeline's two hand-off channels: nothing is being done to them, they
  // are waiting for the next stage to have room. The run is prober → compressor → uploaders, and each
  // arrow is a channel.
  //
  // They used to be folded into uploading, which is where "N objects starting upload" came from — a
  // number that climbed all run and never came back down while nothing was on the wire. Neither queue is
  // small: probedQueue holds 128, and stagedQueue is unbounded for the entry kinds that own no archive
  // (a dedup hit, a resume hit, a raw in-place item), so a store-only workload can queue the whole
  // dataset in it while the uploaders trickle.
  awaitingCompression: number // Probed, waiting for the single compressor
  awaitingUpload: number // Compressed (or needing no compression at all), waiting for an uploader
  waitingOnPeer: number // Of those, items waiting for the first uploader of identical content to finish
  waitingOnSlot: number // Of those, **volumes** queuing on the global upload gate (the gate queues per volume, a different unit from waitingOnPeer)
  // Of those, items doing disk checking: pushing no bytes and waiting on nothing. A single file's dedup
  // pre-check reads the whole file for three hashes; a pack stats every member before and after
  // compression (re-hashing the ones that changed); a multi-volume upload lists leftover cloud volumes
  // first. Each can run for tens of seconds on a NAS while emitting not one progress event — the screen
  // used to show a motionless "1 object starting upload", which is neither starting nor uploading.
  // It is a **subset** of uploading (all of it happens after leaving staging and before any volume is
  // registered in flight), so it must be subtracted when computing starting upload, or one item is
  // reported in two columns and the numbers no longer add up to that identity.
  checking: number
}

export interface BackupProgress {
  stage: number
  changedFiles: number
  changedBytes: number
  uploadedItems: number
  totalItems: number
  percent: number
  // Once pipelined, Diffing and Uploading run at the same time, so the detail is a list.
  details: StageProgress[]
  // The headline detail (= details[0]). A serial stage has exactly one, and this is it.
  detail: StageProgress | null
}

// Terminal states of a background run. Canceled = the user pressed stop: neither success nor failure,
// so the backend does not write it as the backup's Error status (otherwise every stop would need a
// manual Reset).
// Suspended = stopped after saving the scene safely, semantically closer to Canceled than to Failed:
// the next run picks up exactly where it left off.
// Skipped = the run never started, because this backup's sentinel path was not there (the source is most
// likely not mounted). Its own state rather than a flavour of Failed or Canceled: nothing was attempted,
// so the persisted status is left exactly as it was — in both directions, which matters, because writing
// "Normal" would wipe a genuine earlier failure off a backup that has not run since.
export type RunStatus = 'Running' | 'Completed' | 'Failed' | 'Canceled' | 'Suspended' | 'Skipped'

// Stuck on a transient error, waiting for the self-healing retry. **This is not a status**: status is
// still Running, because the background task is alive and the staging lease is still held. Making it a
// status would hide the stop button — and being stuck is exactly when stopping is most wanted.
// Why the gate is closed (serialises as a number, matching the backend enum PauseSource — pinned there,
// mirrored here). TransientError = the self-healing retry above. User = the operator pressed Pause: no
// countdown, nothing to retry now, and it never degrades to suspended on its own.
export const PauseSource = { TransientError: 0, User: 1 } as const

export interface PauseInfo {
  reason: string
  since: string
  // When the next automatic retry is due (UTC). null while a retry is already under way.
  nextRetryAt: string | null
  // Consecutive failures. Reaching the threshold degrades automatically to suspended — unless the user's own
  // hold is standing, which stops that clock entirely.
  failures: number
  source: number // PauseSource
}

// A run left half-finished on disk. After a restart nothing is in memory, so this is the only way to know.
export interface InterruptedRun {
  runId: string
  startedAt: string
  // Blocks the journal confirms are in the cloud. Roughly what continuing would save.
  blocks: number
  journalBytes: number
  // A preview of the cheap preconditions, **not a promise**: the baseline version and the encryption
  // identity need the index and the password to verify, which only happens when a run actually opens the
  // journal. True here and voided there is possible.
  resumable: boolean
}

export interface BackupRun {
  status: RunStatus
  progress: BackupProgress | null
  version: number | null
  // Files this round could not read, whose old index entries were carried forward. A "successful" backup may have stored nothing at all.
  unreadableFiles: number | null
  // What the round actually did, the same figures the operation log's summary reports. Optional rather
  // than nullable: an older backend omits them entirely, and that has to stay distinguishable from a
  // round that genuinely changed nothing (see runTotals).
  newFiles?: number | null
  modifiedFiles?: number | null
  deletedFiles?: number | null
  // Source-side raw size those deleted files had, off the previous version's index. Not the space the
  // cloud gave back — older versions still reference the content until retention retires them.
  deletedBytes?: number | null
  // Source-side raw bytes of the changed files, before compression and dedup.
  changedBytes?: number | null
  // Bytes actually pushed to the cloud. Content that hit dedup counts zero — read the two together to
  // see what compression and dedup each saved.
  uploadedBytes?: number | null
  error: string | null
  // This backup's start and end (UTC), taken from the version record — the same pair /versions gives the
  // restore dialog. null while running, and against an older backend that does not send them.
  startedAt: string | null
  completedAt: string | null
  // This run's identifier, matching the journal filename on disk.
  runId: string
  // Non-null = stuck on a transient error awaiting retry. status is still 'Running'.
  pause: PauseInfo | null
  // Whether the operator's own hold is standing right now. A sibling of `pause`, not a field on it, and
  // deliberately not inferable from `pause.source`: pressing Pause while a transient-error backoff already
  // holds the gate leaves `pause.source` reporting 'TransientError' — countdown and Retry-now affordance
  // included — until that backoff's own timer fires, up to one steady interval (five minutes by default).
  // Rendering paused-ness from `pause` alone would show such a run as merely stuck and retrying, as though
  // the operator's Pause had done nothing. This flag stays true for as long as the hold does, regardless of
  // what `pause.source` says at the moment.
  pausedByUser: boolean
  // The strongest stop asked of this run so far, 'None' when nobody has. Reported because winding down takes
  // minutes with the status still reading 'Running', and without it the fact that a stop had been asked for
  // lived only in the tab that asked — see windDownFromServer.
  stopRequested: StopRequested
  // Why the run was skipped; absent when it was not, and absent from an older backend. Kept apart from
  // `error` on purpose: that one is painted red, and a backup waiting on a mount is not in an error state.
  skipReason?: string | null
  // Why, when status === 'Suspended': UserRequested / AutoSuspended.
  suspendReason: string | null
}

export interface RestoreRun {
  status: RunStatus
  version: number | null
  restoredFiles: number | null
  skippedFiles: number | null
  failedFiles: number | null
  detail: StageProgress | null
  // A record per skipped or failed file. There used to be a single phase field, each entry overwriting
  // the last, so only the final one survived the run and the rest showed up only as the failedFiles count.
  events: string[] | null
  error: string | null
  phase: string | null
}

export interface BackupVersionInfo {
  version: number
  /** When the version was committed (the backup's end). UTC, rendered in local time. */
  createdAt: string
  /** When the backup started running. UTC. null for versions written before this field existed. */
  startedAt: string | null
  files: number
  bytes: number
  changedFiles: number
}

// Graduated check (enums serialise as numbers, matching the backend). CloudCheckLevel/LocalCheckLevel
// share one definition with api/tasks.ts; see constants/labels.ts.
export { CloudCheckLevel, LocalCheckLevel } from '../constants/labels'
export const CloudState = { NotChecked: 0, Ok: 1, MissingOrBad: 2 } as const
export const LocalState = { NotChecked: 0, Ok: 1, Missing: 2, Changed: 3 } as const

export interface FileFinding {
  path: string
  ref: string | null
  cloud: number // CloudState
  local: number // LocalState
  repairable: boolean
  // Filled in by the per-file hash: the local file is longer than the recorded content, so it may hold it
  // as a prefix — what repair's opt-in prefix recovery acts on. Absent until hashed.
  grown?: boolean
  // Non-null = the cloud copy was carried over from an earlier version (the backup could never read the
  // source). Without it, local=Changed reads as "the local file was modified", when the real cause is
  // that the backup never managed to update the cloud copy.
  unreadableAt: string | null
}

export interface RepairPlanRow {
  path: string
  ref: string | null
  action: 'reupload' | 'grown' | 'unrecoverable'
  grown: boolean
  uploadBytes: number
}

export interface RepairPlan {
  version: number
  rows: RepairPlanRow[]
  reuploadObjects: number
  reuploadBytes: number
  unrecoverableCount: number
  grownCount: number
}

export interface CheckReport {
  version: number
  findings: FileFinding[]
  metadataIssue: string | null
  ok: boolean
  missingRefs: string[]
  corruptedPaths: string[]
  repairablePaths: string[]
  orphanBlobs: string[]
  // Whether the scan ran at all. An empty orphanBlobs cannot say: "nobody asked" and "asked, container clean" are
  // the same empty list. This used to be inferred from the dialog's own checkbox — which the dialog loses as soon
  // as it closes, and it closes the instant the check starts, so a finished scan reported nothing whatever it found.
  orphansChecked: boolean
  // Set when the scan was asked for but abandoned (the full reference set could not be built). Shown rather than
  // swallowed: silence is indistinguishable from "never ticked", which is the confusion being fixed.
  orphanScanIssue: string | null
  // The sentinel that was missing, which is why every finding's local state reads "not checked"; null when
  // the local axis ran. Carried on the report for the same reason orphansChecked is — the check dialog is
  // long closed by the time anyone reads this, and a column of "not checked" cannot tell "nobody asked" from
  // "asked, and the source was not mounted". Optional: an older backend does not send it.
  localSkippedSentinel?: string | null
}

export interface RepairReport {
  repaired: string[]
  unrecoverable: string[]
  deletedOrphans: string[]
}

export interface RepairRun {
  status: RunStatus
  repaired: string[] | null
  unrecoverable: string[] | null
  deletedOrphans: string[] | null
  error: string | null
  // The same stage detail the backup rows render — a repair is a run, and a 100 GB file's repair has an
  // honest floor of one full read plus one compression that must look like work, not a hang.
  detail: StageProgress | null
}

// Check is now a background job (202 plus polling): a content-level check downloads and re-hashes the
// entire backup, and as a synchronous endpoint the request was cut off by browser or reverse-proxy
// timeouts first. The finished report stays on the server, so closing and reopening the dialog gets it back.
export interface CheckRun {
  status: RunStatus
  report: CheckReport | null
  error: string | null
  detail: StageProgress | null
}

export interface FileVersionOption {
  version: number
  createdAt: string
  length: number
}

// A node in the restore browse tree (§4.1a): a directory's direct children, for lazy expansion.
export interface TreeNode {
  name: string
  path: string
  isDir: boolean
  hasChildren: boolean
  length: number | null
  mtime: string | null
  storageKind: string | null
  storageRef: string | null
  // Non-null = this entry was carried over from an earlier version, and the value is since when it could
  // not be updated. It has to be visible while choosing what to restore: restoring this version does not
  // give the content as of this version's timestamp.
  unreadableAt: string | null
}

// Files in a version whose content was carried over (the backup could not read the source in those rounds). Unlike unrecoverable: the content is valid, just old.
export interface UnreadableEntry {
  path: string
  unreadableAt: string
}

// The restore estimate (§4.1b): download size, extracted size and file count computed locally, then one HEAD per deduplicated stored object to check rehydration state.
export interface RestoreEstimate {
  downloadBytes: number
  uncompressedBytes: number
  fileCount: number
  archivedObjects: number
  rehydratePending: number
}

/** The result of an import: the configuration created, plus two things the import itself discovered. */
export interface ImportResult {
  config: BackupConfig
  /** The cloud verification is already running in the background — open the check panel directly rather than making the user find that button. */
  checkStarted: boolean
  /** Version numbers whose file list could not be read. Those versions can neither be restored nor checked; the rest are unaffected. */
  unreadableVersions: number[]
}

export const backupConfigsApi = {
  list: () => api.get<BackupConfig[]>('/backup-configs'),
  get: (id: number) => api.get<BackupConfig>(`/backup-configs/${id}`),
  create: (input: BackupConfigInput) => api.post<BackupConfig>('/backup-configs', input),
  import: (
    accountId: number,
    containerName: string,
    password: string | null,
    checkAfterImport: boolean,
  ) =>
    api.post<ImportResult>('/backup-configs/import', {
      accountId,
      containerName,
      password,
      checkAfterImport,
    }),
  update: (id: number, input: BackupConfigInput) =>
    api.put<BackupConfig>(`/backup-configs/${id}`, input),
  remove: (id: number, deleteContainer = false) =>
    api.del(`/backup-configs/${id}${deleteContainer ? '?deleteContainer=true' : ''}`),
  resetStatus: (id: number) => api.post<void>(`/backup-configs/${id}/reset-status`, {}),
  // Migrating the local root. preview is a pure query and can be retried freely; only changeLocalRoot mutates.
  previewLocalRoot: (id: number, newRoot: string) =>
    api.post<LocalRootPreview>(`/backup-configs/${id}/local-root/preview`, { newRoot }),
  changeLocalRoot: (id: number, newRoot: string, force: boolean) =>
    api.post<BackupConfig>(`/backup-configs/${id}/local-root`, { newRoot, force }),
  run: (id: number) => api.post<BackupRun>(`/backup-configs/${id}/run`, {}),
  runStatus: (id: number) => api.get<BackupRun>(`/backup-configs/${id}/run`),
  versions: (id: number) => api.get<BackupVersionInfo[]>(`/backup-configs/${id}/versions`),
  tree: (id: number, version: number | null, path: string | null) =>
    api.get<TreeNode[]>(`/backup-configs/${id}/tree?${new URLSearchParams({
      ...(version != null ? { version: String(version) } : {}),
      ...(path ? { path } : {}),
    })}`),
  restoreEstimate: (id: number, version: number | null, paths: string[]) =>
    api.post<RestoreEstimate>(`/backup-configs/${id}/restore-estimate`, { version, paths }),
  restore: (
    id: number,
    targetRoot: string | null,
    version: number | null,
    substitutions?: Record<string, number>,
    // Selective restore (requirement B): empty restores the whole version; non-empty restores exactly these paths (a pack is downloaded once and only the selected members are written).
    selectedPaths?: string[] | null,
    conflict: number = RestoreConflictMode.OverwriteIfChanged, // Conflict mode (decision 3)
    rehydratePriority: number = RestoreRehydratePriority.Standard, // Archive rehydrate priority
  ) =>
    api.post<RestoreRun>(`/backup-configs/${id}/restore`, {
      targetRoot,
      version,
      substitutions,
      selectedPaths,
      conflict,
      rehydratePriority,
    }),
  restoreStatus: (id: number) => api.get<RestoreRun>(`/backup-configs/${id}/restore`),
  fileVersions: (id: number, path: string) =>
    api.get<FileVersionOption[]>(`/backup-configs/${id}/file-versions?path=${encodeURIComponent(path)}`),
  // The targeted follow-up to a cloud-only check: hash ONE path against the version's recorded
  // content. The backend compares the length first, so an appended huge file answers instantly.
  hashFile: (id: number, path: string, version: number | null) =>
    api.get<{ path: string; local: number; repairable: boolean; grown: boolean }>(
      `/backup-configs/${id}/hash-file?path=${encodeURIComponent(path)}${version != null ? `&version=${version}` : ''}`,
    ),
  unrecoverablePaths: (id: number, version: number | null) =>
    api.get<string[]>(`/backup-configs/${id}/unrecoverable${version != null ? `?version=${version}` : ''}`),
  unreadableEntries: (id: number, version: number | null) =>
    api.get<UnreadableEntry[]>(`/backup-configs/${id}/unreadable${version != null ? `?version=${version}` : ''}`),
  check: (id: number, cloud: number, local: number, version: number | null = null, rehydrate: number | null = null, listOrphans = false) => {
    const p = new URLSearchParams()
    p.set('cloud', String(cloud))
    p.set('local', String(local))
    if (version != null) p.set('version', String(version))
    if (rehydrate != null) p.set('rehydrate', String(rehydrate))
    if (listOrphans) p.set('listOrphans', 'true')
    // 202: this only starts the check; the result comes from polling checkStatus.
    return api.post<CheckRun>(`/backup-configs/${id}/check?${p.toString()}`, {})
  },
  // When this backup has never been checked the backend answers 204 (not 404, which would leave a red error in the browser console), so this comes back empty.
  checkStatus: (id: number) => api.get<CheckRun | null>(`/backup-configs/${id}/check`),
  repair: (id: number, cloud: number, version: number | null = null, rehydrate: number | null = null, cleanupOrphans = false, paths: string[] | null = null) => {
    const p = new URLSearchParams()
    p.set('cloud', String(cloud))
    if (version != null) p.set('version', String(version))
    if (rehydrate != null) p.set('rehydrate', String(rehydrate))
    if (cleanupOrphans) p.set('cleanupOrphans', 'true')
    return api.post<RepairRun>(`/backup-configs/${id}/repair?${p.toString()}`, { paths })
  },
  // The repair's price tag before any consent is spent: stat-and-index only, so it answers instantly.
  repairPlan: (id: number, version: number | null = null) =>
    api.get<RepairPlan>(`/backup-configs/${id}/repair-plan${version != null ? `?version=${version}` : ''}`),
  repairStatus: (id: number) => api.get<RepairRun>(`/backup-configs/${id}/repair`),
  // Suspend persists only the plan's selection; resume replays it against a fresh pre-check — healed files
  // fall out on their own, half-replaced families are salvaged volume by volume by the verified skip.
  repairSuspend: (id: number) => api.post<void>(`/backup-configs/${id}/repair/suspend`, {}),
  repairResume: (id: number) => api.post<RepairRun>(`/backup-configs/${id}/repair/resume`, {}),
  // Stop whatever is running. Omitting `what` stops every running operation on this configuration.
  //
  // On the backup path the backend **waits for the flush before answering**, so this promise resolving
  // means the scene is already safe — unless stopping is true, which means the backend waited 20 seconds
  // without the run winding down (a large file still uploading). The run still reaches a terminal state;
  // the UI just keeps polling.
  //
  // finishCurrentFiles=true: let the file currently uploading finish (all of its volumes) and count it;
  // false: stop immediately, deleting half-written volumes and in-flight blocks so no unusable remains are left.
  cancel: (id: number, what?: 'backup' | 'restore' | 'repair' | 'check', finishCurrentFiles = false) => {
    const p = new URLSearchParams()
    if (what) p.set('what', what)
    if (finishCurrentFiles) p.set('finishCurrentFiles', 'true')
    const q = p.toString()
    return api.post<{ canceled: string[]; stopping?: boolean }>(
      `/backup-configs/${id}/cancel${q ? `?${q}` : ''}`, {})
  },
  // Suspend: stop after flushing safely. **There is no matching resume** — resuming is not a mode, since
  // every run recognises any still-valid journal when it opens one, so "continue" is just calling run() again.
  suspend: (id: number) => api.post<void>(`/backup-configs/${id}/suspend`, {}),
  // Release one retry immediately, without waiting for the self-healing timer.
  retryNow: (id: number) => api.post<void>(`/backup-configs/${id}/retry-now`, {}),
  // Hold the run in place: every stage finishes the item in hand and parks, keeping its staging quota until
  // resume() lifts the hold. Not the same "resume" the suspend comment above disclaims — that one means
  // continuing a torn-down run via run(); this one lifts a pause that never tore anything down.
  pause: (id: number) => api.post<void>(`/backup-configs/${id}/pause`, {}),
  resume: (id: number) => api.post<void>(`/backup-configs/${id}/resume`, {}),
  interrupted: (id: number) => api.get<InterruptedRun[]>(`/backup-configs/${id}/interrupted`),
  discardInterrupted: (id: number) => api.del(`/backup-configs/${id}/interrupted`),
  resetPassword: (id: number, password: string) =>
    api.post<void>(`/backup-configs/${id}/reset-password`, { password }),
}
