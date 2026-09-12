import type { LatestWins } from './latestWins'

/**
 * Run one request under a latest-wins gate and deliver its outcome — result or error — only if no
 * later request has been started since. A superseded request delivers nothing at all: not its data,
 * not its error, and not "done loading".
 *
 * That last part is why this exists. The list pages used to write `.finally(() => setLoaded(true))`
 * next to a gated `.then`, so a superseded request — the mount load on a slow server, overtaken by
 * the 5-second poll or by the next keystroke in a filter — kept its data out of state but still
 * flipped "loading" off. The table then rendered its empty state ("No backups yet.", "No log
 * entries.") over a list that had simply not arrived yet, with no error line, until whichever
 * request was newest landed — and on a stalled server that could be never. "Loaded" means an answer
 * arrived, and only the latest request's answer counts, so the callbacks own that flag and a
 * superseded request never reaches them.
 *
 * @returns the result when it was delivered; null when the request was superseded, and null after a
 * delivered error too — the caller has been told through `onError` and nothing is left to do.
 */
export function gatedLoad<T>(
  gate: LatestWins,
  request: () => Promise<T>,
  onResult: (result: T) => void,
  onError: (error: unknown) => void,
): Promise<T | null> {
  const isLatest = gate.begin()
  return request().then(
    (result) => {
      if (!isLatest()) return null
      onResult(result)
      return result
    },
    (error: unknown) => {
      if (isLatest()) onError(error)
      return null
    },
  )
}
