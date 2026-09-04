import type { BackupActivity, BackupRun } from '../api/backupConfigs'

/**
 * Whether a configuration's row should offer to pick up an interrupted run.
 *
 * The rule used to be "there is no run in memory": a journal on disk with nothing running meant the round
 * before the restart never finished. That misses the case the operator hits most — a run that **failed**.
 * The backend keeps a failed run in memory as a terminal state, so `runs[id]` is still there, while
 * RunStatus renders that state as a single line of red text with no buttons at all. The notice carrying
 * Resume was gated off by the presence of the run, and the run's own row offered nothing: the way back was
 * gone, and only reloading the page (which clears the in-memory run states) brought it back.
 *
 * So the gate is not "is there a run" but "does this run's own row already offer a way forward":
 *
 * · Running   — not interrupted yet, nothing to offer.
 * · Suspended — RunStatus already renders Resume and Discard; a second Resume on the same row is noise.
 * · Completed — the round succeeded. Any journal still listed belongs to another configuration sharing
 *               this container (see InterruptedNotice on why the list is keyed by container, not config),
 *               and offering to resume it from this row would claim something untrue about this backup.
 * · Failed / Canceled — the journal outlives both, and neither renders a button. These are the ones that
 *               need the notice, and Failed is the case that was broken.
 *
 * "No run in memory" has one more reading than "the process restarted". On entering the page, `runs` is
 * empty until the first status poll lands, while the journal list — which includes the **live** run's own
 * journal, since the backend lists every volume on the container — can land first. In that gap the row
 * said "Interrupted run … Resume Discard" in amber and then switched to the progress line: a claim of
 * interrupted work about a backup that was running the whole time. The configuration list already knows
 * the difference — `activity` is BackingUp — so when the server says a backup is in progress and its
 * state simply has not arrived yet, wait for it rather than explaining the journal wrongly. Any other
 * activity leaves the original rule alone: nothing is about to account for that journal.
 */
export function showsInterruptedNotice(
  run: BackupRun | undefined,
  interruptedCount: number,
  activity: BackupActivity = 'Idle',
): boolean {
  if (interruptedCount <= 0)
    return false
  if (!run)
    return activity !== 'BackingUp'
  return run.status === 'Failed' || run.status === 'Canceled'
}
