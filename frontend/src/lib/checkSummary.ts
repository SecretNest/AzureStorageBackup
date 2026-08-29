import type { CheckReport } from '../api/backupConfigs'
import { CloudState, LocalState } from '../api/backupConfigs'

/**
 * The unreferenced-blob half of a finished check's one-line result.
 *
 * The orphan scan is a **separate axis** from ok: orphans are not corruption and never fail a check,
 * so if this line does not state the outcome, nothing on the page does. That matters more here than
 * anywhere else — the check dialog closes the instant a check starts, so for the whole of a check's
 * life and after it, this row is the only thing on screen.
 *
 * The criterion is `orphansChecked`, i.e. **the scan ran**, not `orphanBlobs.length`, i.e. **the scan
 * found something**. That distinction is the entire reason the flag exists (see its comment on
 * CheckReport): an empty list cannot tell "nobody ticked the box" from "ticked, container clean", and
 * keying on the length collapses the two — a user who explicitly asked the question got the same blank
 * row as a user who never asked, and had to reopen the dialog to find out the scan had happened at all.
 * BackupChecker's own log line has always used this criterion; this is it, on screen.
 *
 * Extracted from the JSX so the three states and their wording can be asserted — once a string is
 * inside a component there is nowhere left to test it, and this project has no component tests.
 * Same reasoning as runTotals beside it.
 */
export function orphanSummary(report: CheckReport): string {
  // Asked for and abandoned outranks the count, because the count is then not a finding: printing
  // "no unreferenced blobs" for a scan that never finished is a false all-clear on the one axis a
  // repair acts on. Silence would be no better — it is indistinguishable from never having asked.
  if (report.orphanScanIssue) return ' · unreferenced-blob scan abandoned'
  if (!report.orphansChecked) return ''
  return report.orphanBlobs.length > 0
    ? ` · ${report.orphanBlobs.length.toLocaleString()} unreferenced blob(s), reclaimable by repair`
    : ' · no unreferenced blobs'
}

/**
 * The repairability half of a failing check's one-line result, shared by the config row and the
 * dialog summary so the two cannot drift.
 *
 * Repairability is only a **verdict** where the local content was actually hashed. A check run
 * without a content-level local check knows nothing about the local side, and printing
 * "0 repairable" there is a verdict where there was only an unanswered question — it sends the user
 * away from the repair that would have hashed exactly the affected files (repair re-checks the
 * cloud and hashes locally per bad object on its own; it never trusted this report's flag).
 * Mirrors BackupChecker.ProblemsSummary on the notification side.
 */
export function repairabilitySummary(report: CheckReport): string {
  const problems = report.findings.filter((f) => f.cloud === CloudState.MissingOrBad)
  const unassessed = problems.filter((f) => f.local === LocalState.NotChecked).length
  const repairability =
    unassessed === problems.length && problems.length > 0
      ? 'local repairability not assessed — repair will hash just the affected files'
      : `${report.repairablePaths.length} repairable from local` +
        (unassessed > 0 ? `, ${unassessed} not assessed` : '')
  return `${problems.length} problem(s), ${repairability}`
}
