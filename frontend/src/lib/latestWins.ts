// A small gate that lets only the most recently started call write state. Extracted as a pure
// function so it can be tested: the repository has no component-rendering infrastructure
// (see the same note in localRootVerdict.ts).
//
// Why not the `cancelled` flag used throughout this codebase: that flag is an effect's cleanup
// discarding its own request on unmount or re-run, and it answers "does the component still
// exist?". This answers something else — the same refresh function is started by two unrelated
// triggers (a user action calling load(), and the unattended 5-second poll), leaving two requests
// in flight. Which returns first is not decided by which started first: the later one can return
// first and the earlier one second, so **an old snapshot overwrites a new one** and the UI keeps
// showing a run that stopped midway until the next tick corrects it.
//
// The only workable predicate is "am I still the latest?", which needs a monotonically increasing
// number rather than a boolean.
export interface LatestWins {
  /**
   * Start a new request, invalidating every one started before it.
   * @returns a predicate: call it when the result arrives; only true permits writing state.
   */
  begin(): () => boolean
}

export function latestWins(): LatestWins {
  let issued = 0
  return {
    begin() {
      const mine = ++issued
      return () => mine === issued
    },
  }
}
