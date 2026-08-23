/**
 * Which run controls stay live once a wind-down has been asked for, and what Stop reads as.
 *
 * The kinds are the frontend's mirror of the backend's `StopKind` ladder (BackupRunControl.cs), minus the
 * `None` at the bottom — which is `undefined` here:
 *
 *     Suspend (1)  <  FinishCurrentFiles (2)  <  StopNow (3)
 *
 * That ladder is the whole reason this is a function with its own tests. `BackupRunControl.RequestStop`
 * is a CAS loop that only ever moves the stop kind **up** — `if ((int)kind <= current) return` — so a
 * second, stronger request is not a race with an unpredictable winner, it is the escalation the ladder
 * exists for, and it is exactly what a stronger kind additionally does that the operator needs: only
 * StopNow fires `_abort`, and only that interrupts the upload already on the wire.
 *
 * Why that matters enough to model: Suspend and Finish current files both wait for the file in hand,
 * **including every one of its volumes**, and the run keeps reporting itself as Running the whole time.
 * On a slow uplink that is minutes to tens of minutes. An operator who presses Suspend and then realises
 * what it is waiting on has one correct move — escalate — and disabling Stop takes it away, leaving
 * restarting the container as the only way out of a wait they did not mean to start.
 *
 * The other three controls (Retry now, Resume, Pause) are a different case and stay disabled throughout.
 * All three reach into the run's pause gate, and any stop **downgrades** that gate — after which it can
 * never hold anyone again, so those buttons cannot do what their labels say however long the wind-down
 * lasts. The backend answers them with a conflict rather than a silent 204; disabling them here is the
 * half that keeps the operator from meeting that error at all. Suspend goes with them: from anywhere on
 * the ladder it is a step **down**, which `RequestStop` ignores, so it would be a button that does
 * nothing.
 */
export type WindDownKind = 'suspend' | 'finish' | 'now'

/** The backend's own name for the same ladder, as it arrives on `BackupRun.stopRequested`. */
export type StopRequested = 'None' | 'Suspend' | 'FinishCurrentFiles' | 'StopNow'

/**
 * The wind-down a running backup is actually in, as reported by the server.
 *
 * Until this existed the answer lived only in the browser tab that had pressed the button — local state, lost on
 * unmount. Switching away and back during a wind-down brought the row up as an ordinary running backup, with every
 * button live again and nothing on screen about the stop already in progress; and since Suspend waits out the file
 * in hand, that window is minutes long. The server knows the whole time, so this is what the row should key off,
 * with the local mark kept only to cover the gap before the next poll.
 *
 * An unrecognised value reads as no wind-down rather than throwing — a newer server naming a rung this build has
 * never heard of should cost a row its label, not the page.
 */
export function windDownFromServer(stop: StopRequested | string | null | undefined): WindDownKind | undefined {
  switch (stop) {
    case 'Suspend': return 'suspend'
    case 'FinishCurrentFiles': return 'finish'
    case 'StopNow': return 'now'
    default: return undefined
  }
}

export function windDownControls(kind: WindDownKind | undefined): {
  /** Stop stays pressable while anything weaker than StopNow is winding down. */
  canStop: boolean
  /** Retry now / Resume / Pause / Suspend — none of them can act once a wind-down is under way. */
  canActOnGate: boolean
  stopLabel: string
  suspendLabel: string
} {
  return {
    // Only the top of the ladder has nothing left to escalate to. Note that this is deliberately not
    // "no stop requested yet": 'finish' is a stop, and going from it to StopNow is the escalation most
    // likely to be wanted, since Finish current files is the choice whose wait surprises people.
    canStop: kind !== 'now',
    canActOnGate: kind === undefined,
    // 'Stopping…' is claimed only where it is the whole truth. Under 'finish' a stop is indeed running,
    // but the button is still live and pressing it still does something, and a disabled-looking label on
    // a live button is the same lie in the other direction.
    stopLabel: kind === 'now' ? 'Stopping…' : 'Stop',
    suspendLabel: kind === 'suspend' ? 'Suspending…' : 'Suspend',
  }
}
