import type { CheckReport } from '../api/backupConfigs'

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
