// Local-root migration verdict → UI decision (see docs/change-local-root-design.md).
// A pure function so it can be tested: the repository has no component-rendering infrastructure, and
// the dialog only draws what this returns.

export interface LocalRootPreview {
  verdict: string // 'Ok' | 'NeedsConfirm' | 'Rejected' | 'NoBaseline' | 'BaselineUnreadable'
  sampled: number
  matched: number
  missing: number
  sizeMismatch: number
  mtimeDiffers: number
  matchRate: number
  reason: string | null
  examples: string[]
}

export interface LocalRootDecision {
  /** Whether Apply can be pressed right now (when needsForce is true, the checkbox must also be ticked). */
  canApply: boolean
  /** Whether "change anyway" must be ticked manually before Apply. */
  needsForce: boolean
  tone: 'ok' | 'warn' | 'danger' | 'info'
  headline: string
  /**
   * The body shown next to the checkbox when needsForce is true. The three confirming verdicts have
   * different consequences (below), so it belongs in this pure function rather than leaving the
   * dialog to assemble wording from tone/verdict itself. Unset when needsForce is false.
   */
  confirmBody?: string
}

// NeedsConfirm and Rejected both really did run a file-by-file comparison and really did find
// mismatches — the next backup recording them as deleted and re-uploading them genuinely happens.
const CONFIRM_BODY_COMPARED =
  'I understand — change it anyway. The next backup will record every file that no longer matches as deleted and upload the new ones. Scope rules are kept as they are and may no longer match if this directory is laid out differently.'

// BaselineUnreadable compared nothing at all (Sampled/Matched/Missing are all 0) — it is not "files
// do not match" but "this backup's own index cannot be read". The real consequence is that
// TrackedInfoStore.LoadAsync deserialises the cached index bytes without a try/catch: the root change
// itself succeeds, the index is still unreadable, and the next backup will most likely fail in the
// same place. This operation does not fix it.
const CONFIRM_BODY_UNREADABLE =
  "I understand — change it anyway. No comparison actually ran, because this backup's own index could not be read — nothing is known about whether these files match. The move itself will go through, but the unreadable index is a separate problem this operation does not fix, and the next backup is likely to fail on it until that's dealt with."

export function localRootDecision(preview: LocalRootPreview | null): LocalRootDecision {
  if (!preview) {
    return { canApply: false, needsForce: false, tone: 'info', headline: 'Check the new path first.' }
  }

  switch (preview.verdict) {
    case 'Ok':
      return {
        canApply: true,
        needsForce: false,
        tone: 'ok',
        headline: `${preview.matched} of ${preview.sampled} sampled entries match.`,
      }
    case 'NoBaseline':
      return {
        canApply: true,
        needsForce: false,
        tone: 'info',
        headline: preview.reason ?? 'No previous version to compare against — only the path itself was checked.',
      }
    case 'NeedsConfirm':
      return {
        canApply: false,
        needsForce: true,
        tone: 'warn',
        headline: `Only ${preview.matched} of ${preview.sampled} sampled entries match.`,
        confirmBody: CONFIRM_BODY_COMPARED,
      }
    case 'Rejected':
      return {
        canApply: false,
        needsForce: true,
        tone: 'danger',
        headline: `${preview.matched} of ${preview.sampled} sampled entries match — this looks like the wrong directory.`,
        confirmBody: CONFIRM_BODY_COMPARED,
      }
    case 'BaselineUnreadable':
      // History that cannot be read is not the same as no history: the latter passes, the former has
      // to be questioned. `reason` carries the underlying exception text — with no command line on a
      // NAS, that is the user's only diagnostic.
      return {
        canApply: false,
        needsForce: true,
        tone: 'danger',
        headline: preview.reason ?? 'This backup has history, but its latest version index could not be read.',
        confirmBody: CONFIRM_BODY_UNREADABLE,
      }
    default:
      // If the backend adds a verdict the frontend does not know yet, block rather than allow.
      return { canApply: false, needsForce: false, tone: 'danger', headline: 'Unrecognised check result.' }
  }
}
