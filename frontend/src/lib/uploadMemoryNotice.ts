import { formatBytes } from '../constants/format'

/**
 * The settings page's reading of the upload memory limit, mirroring the backend's UploadMemoryBudget so the
 * operator sees the number the engine will actually use instead of the one they typed.
 *
 * A backup runs one upload stream more than the concurrency (the extra one keeps a split archive's volumes
 * from stalling at the hand-off), and it is the biggest consumer of the limit, so the notice reasons about a
 * backup. Repair and compaction get the same limit each, split across fewer streams.
 */

/** The 80 KB floor a stream is granted once a limit is set at all — one read chunk (UploadMemoryBudget.FloorBytes). */
export const UPLOAD_MEMORY_FLOOR_BYTES = 80 * 1024

/** A backup's uploader count for a given concurrency: UploadConcurrency + 1, never fewer than two. */
export function backupUploaderCount(uploadConcurrency: number): number {
  return Math.max(2, Math.max(1, uploadConcurrency) + 1)
}

/** The per-stream share of the limit for a backup — 0 when the limit is 0, otherwise never below the floor. */
export function uploadMemoryShare(limitBytes: number, uploadConcurrency: number): number {
  if (limitBytes <= 0) return 0
  return Math.max(UPLOAD_MEMORY_FLOOR_BYTES, Math.floor(limitBytes / backupUploaderCount(uploadConcurrency)))
}

const TWO_PASS =
  'hashed from disk first and read a second time for the send — still labelled, just one extra read.'

export function uploadMemoryNotice(
  limitBytes: number,
  uploadConcurrency: number,
  defaultVolumeBytes: number | null,
): string {
  if (limitBytes <= 0) return `Set to 0: no volume is ever held in memory. Every volume is ${TWO_PASS}`

  const streams = backupUploaderCount(uploadConcurrency)
  const share = uploadMemoryShare(limitBytes, uploadConcurrency)
  const head = `Each of a backup’s ${streams} upload streams may hold up to ${formatBytes(share)}.`

  if (defaultVolumeBytes === null || defaultVolumeBytes <= 0) {
    return (
      `${head} Volume splitting is off, so an archive is one volume of any size: archives up to ` +
      `${formatBytes(share)} are sent from memory in one disk read, larger ones are hashed first and read a ` +
      'second time for the send.'
    )
  }
  if (defaultVolumeBytes <= share) {
    return `${head} A ${formatBytes(defaultVolumeBytes)} volume fits, so volumes are hashed and sent from memory in one disk read.`
  }
  return `${head} A ${formatBytes(defaultVolumeBytes)} volume does not fit, so every volume is ${TWO_PASS}`
}
