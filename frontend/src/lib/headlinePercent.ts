import { BackupStage, type StageProgress } from '../api/backupConfigs'

/**
 * The one percentage on a backup row's headline, from the running stage's own detail — and, for the upload
 * stage alone, from the run-level item count when the detail cannot give one.
 *
 * Extracted so the fallback can be asserted: it used to be an inline `stage >= Uploading ? run.percent`,
 * and everything past the upload took the fallback too. The run-level figure is the upload's item count,
 * which is N of N by then, so "Writing index" opened at 100%, dropped to 0% the moment the index tracker
 * planned its first volume, and "Finalizing" (now "Updating catalog") then stood at 100% for as long as the catalog import ran —
 * a bar going backwards and a stage that looked finished while minutes of work remained (field, 2026-09-12).
 *
 * A wrap-up stage with no percentage of its own shows none. That is what those stages' detail lines say
 * in words anyway; the number was never theirs.
 */
export function headlinePercent(stage: number, runPercent: number, details: StageProgress[]): number | null {
  const own = details[0]?.workPercent ?? details[0]?.percent
  if (own != null) return own
  return stage === BackupStage.Uploading ? runPercent : null
}
