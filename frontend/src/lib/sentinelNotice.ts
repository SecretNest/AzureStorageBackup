import type { BackupRun, CheckReport } from '../api/backupConfigs'

/**
 * The two lines the sentinel puts on screen: one when a backup did not run, one when half a check did not.
 *
 * They live together because they are the same claim seen from two angles — "the source was not there, so
 * this did not happen" — and the day they drift apart is the day one of them tells the operator something
 * the other contradicts. The backend keeps the same pair in one place (SentinelGate) for the same reason.
 *
 * Extracted from the JSX so the wording can be asserted — once a string is inside a component there is
 * nowhere left to test it, and this project has no component tests. Same reasoning as runTotals and
 * orphanSummary beside it.
 */

/**
 * What a skipped backup says on the row the operator is watching.
 *
 * Deliberately not styled or worded as a failure: nothing went wrong, and a red badge every morning for a
 * NAS that simply is not mounted overnight is an alarm nobody will still be reading in a week. But it must
 * not read as a success either — "nothing was recorded" is the load-bearing half, because the whole reason
 * this feature exists is that a round which looks like it worked, and quietly deleted everything, is worse
 * than a round that visibly did not run.
 */
export function runSkipNotice(run: BackupRun): string {
  // An older backend sends the status without the reason. A bare "Skipped" with no explanation reads as a
  // bug in the app, so the sentence stands on its own and only gains the path when there is one.
  const why = run.skipReason ?? 'the configured sentinel path was not found'
  return `Skipped — ${why} Nothing was backed up and nothing was recorded for this round.`
}

/**
 * The banner on a finished check whose local half was demoted.
 *
 * Two things have to be said and neither is optional. That the local comparison did not happen — otherwise a
 * column of "not checked" reads as a clean bill of health, which is the same false reassurance in a different
 * costume. And that the cloud result still counts — otherwise the operator throws away a verdict that is
 * perfectly good and reruns a check that cannot tell them any more than this one already did.
 */
export function checkLocalSkipNotice(report: CheckReport): string {
  if (!report.localSkippedSentinel) return ''
  return (
    `Local check skipped: sentinel '${report.localSkippedSentinel}' does not exist, so the source was not ` +
    `compared. The cloud-side result below is unaffected.`
  )
}
