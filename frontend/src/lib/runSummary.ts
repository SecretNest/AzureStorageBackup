import type { BackupRun } from '../api/backupConfigs'
import { formatBytes } from '../constants/format'

/**
 * The one-line totals for a finished backup, shown next to "Completed — version N".
 *
 * These numbers already existed, but only in the operation log and the webhook notification — the place
 * the operator actually watches a backup finish said nothing beyond the version number, so answering
 * "what did that round do?" meant leaving the page. This is the same content as the log's summary,
 * rearranged for a line rather than a paragraph.
 *
 * The rules are deliberately the same as the backend's BackupSummary, and for the same reason: a zero
 * makes its item disappear. Drag a string of zeros along every round and the round that carries
 * information drowns in the noise. Two exceptions to that rule are load-bearing and each has a test:
 *
 * · Zero **files** does not empty the line, it says "no changes" — an empty line reads as a rendering
 *   bug, not as a finding.
 * · Zero **uploaded** does not drop the data segment, as long as something changed at source. That is
 *   precisely the round where dedup hit on everything, and "4.7 GB changed yet not a byte uploaded" is
 *   the whole reason these two figures are reported separately rather than as one number.
 *
 * Unreadable files are left out on purpose: they get their own red line right after this one, and
 * counting them here too would report the same files twice.
 *
 * Extracted from the JSX so the wording and the disappearing rules can be asserted — once a string is
 * inside a component there is nowhere left to test it, and this project has no component tests.
 */
export function runTotals(run: BackupRun): string | null {
  // An older backend sends none of these fields. "no changes" would be a lie there: nothing is known
  // about the round, which is not the same as knowing it changed nothing. So say nothing at all.
  const figures = [run.newFiles, run.modifiedFiles, run.deletedFiles, run.changedBytes, run.uploadedBytes]
  if (figures.every((v) => v == null)) return null

  const changedBytes = run.changedBytes ?? 0
  const uploadedBytes = run.uploadedBytes ?? 0

  const files = [
    run.newFiles && `${run.newFiles.toLocaleString()} new`,
    run.modifiedFiles && `${run.modifiedFiles.toLocaleString()} modified`,
    // The size sits with the count, not in the data segment: that segment tracks what changed at source
    // against what went over the wire, and deleted bytes went nowhere. A zero (or an older backend that
    // sends no size at all) drops the parenthesis rather than printing "(0 B)".
    run.deletedFiles &&
      `${run.deletedFiles.toLocaleString()} deleted${run.deletedBytes ? ` (${formatBytes(run.deletedBytes)})` : ''}`,
  ]
    .filter(Boolean)
    .join(', ')

  return [
    files || 'no changes',
    (changedBytes > 0 || uploadedBytes > 0) &&
      `${formatBytes(changedBytes)} changed at source → ${formatBytes(uploadedBytes)} uploaded`,
  ]
    .filter(Boolean)
    .join(' · ')
}
