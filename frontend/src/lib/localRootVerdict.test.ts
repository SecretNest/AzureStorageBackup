import { describe, expect, it } from 'vitest'
import { localRootDecision, type LocalRootPreview } from './localRootVerdict'

const base: LocalRootPreview = {
  verdict: 'Ok',
  sampled: 200,
  matched: 200,
  missing: 0,
  sizeMismatch: 0,
  mtimeDiffers: 0,
  matchRate: 1,
  reason: null,
  examples: [],
}

describe('localRootDecision', () => {
  it('nothing checked yet — cannot apply', () => {
    const d = localRootDecision(null)
    expect(d.canApply).toBe(false)
    expect(d.needsForce).toBe(false)
  })

  it('Ok — applies straight away, no checkbox', () => {
    const d = localRootDecision(base)
    expect(d.canApply).toBe(true)
    expect(d.needsForce).toBe(false)
    expect(d.tone).toBe('ok')
  })

  it('NoBaseline — applies straight away, no checkbox', () => {
    const d = localRootDecision({ ...base, verdict: 'NoBaseline', sampled: 0, matched: 0, reason: 'no versions' })
    expect(d.canApply).toBe(true)
    expect(d.needsForce).toBe(false)
    expect(d.tone).toBe('info')
  })

  it('NeedsConfirm — needs the checkbox before applying', () => {
    const d = localRootDecision({ ...base, verdict: 'NeedsConfirm', matched: 137, matchRate: 0.685 })
    expect(d.canApply).toBe(false)
    expect(d.needsForce).toBe(true)
    expect(d.tone).toBe('warn')
    // The user has no command line; the numbers have to be on the heading itself.
    expect(d.headline).toContain('137')
    expect(d.headline).toContain('200')
    // A real comparison with real mismatches, so "recorded as deleted and re-uploaded" is accurate.
    expect(d.confirmBody).toContain('record every file that no longer matches as deleted')
  })

  it('Rejected — needs the checkbox, strongest tone', () => {
    const d = localRootDecision({ ...base, verdict: 'Rejected', matched: 0, matchRate: 0 })
    expect(d.canApply).toBe(false)
    expect(d.needsForce).toBe(true)
    expect(d.tone).toBe('danger')
    // Also a real comparison — it shares the sentence with NeedsConfirm.
    expect(d.confirmBody).toContain('record every file that no longer matches as deleted')
  })

  it('BaselineUnreadable — needs the checkbox, and surfaces the underlying reason', () => {
    const d = localRootDecision({
      ...base,
      verdict: 'BaselineUnreadable',
      sampled: 0,
      matched: 0,
      matchRate: 0,
      reason: 'The latest version index could not be read: bad decrypt',
    })
    expect(d.canApply).toBe(false)
    expect(d.needsForce).toBe(true)
    // "There is history but it cannot be read" must never be waved through as "there is no history".

    // Nothing was actually compared here (Sampled and Matched are both 0), so it must not claim
    // "recorded as deleted and re-uploaded" — true for NeedsConfirm/Rejected, invented for this one.
    expect(d.confirmBody).not.toContain('record every file that no longer matches as deleted')
    // The real consequence: the root change itself succeeds, but an unreadable index is a separate
    // problem this operation does not solve, and the next backup will most likely hit the same wall.
    expect(d.confirmBody).toContain('index')
    expect(d.confirmBody).toContain('next backup')
  })

  it('an unknown verdict never silently allows the change', () => {
    const d = localRootDecision({ ...base, verdict: 'SomethingNew' })
    expect(d.canApply).toBe(false)
  })
})
