import { formatBytes, formatDuration } from '../constants/format'

/**
 * The "how much longer" segment of a running operation's headline — `200.000 GB (~12h 43m) left`.
 *
 * The time alone was what this line used to say, and it answers only half the question an operator
 * asks at a glance: "~12h 43m left" of *what*? The headline's other figure is a running total
 * ("313.600 GB / 1.400 TB"), so working out the remainder means subtracting two numbers that both
 * move. Stating the outstanding bytes next to the time it will take to move them puts the estimate's
 * own denominator on screen, which also makes an implausible ETA visibly implausible.
 *
 * The size is **source** bytes (pre-compression), the same basis as the fraction beside it and as the
 * percentage before that — mixing in the wire volume here would produce two figures that disagree for
 * a reason nothing on the row explains.
 *
 * Zero (or unknown) remaining bytes falls back to the bare time rather than printing empty
 * parentheses: an operation reporting an ETA with nothing left to process is a state the backend can
 * reach briefly at the tail of a run, and "(~5s) left" reads as a rendering fault.
 *
 * Returns null when there is no estimate at all — during the pipelined phase the backend deliberately
 * returns no ETA (the upload's denominator is still growing), and a guess there would go backwards.
 *
 * Extracted from the JSX so the wording can be asserted; this project has no component tests.
 */
export function etaLabel(etaSeconds: number | null | undefined, workRemaining: number | null | undefined): string | null {
  if (etaSeconds == null) return null
  const time = `~${formatDuration(etaSeconds)}`
  return workRemaining != null && workRemaining > 0
    ? `${formatBytes(workRemaining)} (${time}) left`
    : `${time} left`
}
