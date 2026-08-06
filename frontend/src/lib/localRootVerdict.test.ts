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
    // 用户看不到命令行，数字必须直接摆在标题上。
    expect(d.headline).toContain('137')
    expect(d.headline).toContain('200')
  })

  it('Rejected — needs the checkbox, strongest tone', () => {
    const d = localRootDecision({ ...base, verdict: 'Rejected', matched: 0, matchRate: 0 })
    expect(d.canApply).toBe(false)
    expect(d.needsForce).toBe(true)
    expect(d.tone).toBe('danger')
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
    // 「有历史但读不出来」绝不能被当成「没有历史」放行。
    expect(d.headline).toContain('bad decrypt')
  })

  it('an unknown verdict never silently allows the change', () => {
    const d = localRootDecision({ ...base, verdict: 'SomethingNew' })
    expect(d.canApply).toBe(false)
  })
})
