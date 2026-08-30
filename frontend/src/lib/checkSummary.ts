import type { CheckReport, CheckResolution } from '../api/backupConfigs'
import { CloudState, LocalState } from '../api/backupConfigs'

/**
 * The one-line history of a **settled** check (§39b) — the only trace left once the findings table is
 * gone. A clean, repaired, or dropped report persists but no longer gates: the dialog drops back to the
 * start view, and without this line "the last check found nothing" would be visible nowhere at all
 * ("没地方看到这个没有错误的报告"). Returns null for the two states that have their own on-screen home — a
 * Pending report (the live findings table) and no report (the start view) — so the caller renders
 * nothing in those cases.
 *
 * Extracted from the JSX for the same reason as the summaries below: this project has no component
 * tests, so the wording of the three settled states can only be pinned here. `when` is the already
 * locale-formatted timestamp (or null on an older row) — kept out of this function so the assertions
 * stay free of the machine's timezone.
 */
export function resolutionSummary(
  resolution: CheckResolution | null,
  unrepairedCount: number,
  when: string | null,
): string | null {
  const tail = ' You can start a new check to replace this.'
  switch (resolution) {
    case 'Clean':
      return `The last check${when ? ` (${when})` : ''} found everything OK.${tail}`
    case 'Repaired':
      return `The last check found problems and they have all been repaired${when ? ` (checked ${when})` : ''}.${tail}`
    case 'Dropped':
      return (
        `The last report was dropped — ${unrepairedCount} file(s) left unrepaired and still marked; ` +
        `the next backup heals any whose content is unchanged.${tail}`
      )
    default:
      return null // Pending (findings table shows it) or null (start view) — no history line here
  }
}

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
