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
    // 真的比对过，且有真的不匹配的文件——文案说「记成删除再重传」是准确的。
    expect(d.confirmBody).toContain('record every file that no longer matches as deleted')
  })

  it('Rejected — needs the checkbox, strongest tone', () => {
    const d = localRootDecision({ ...base, verdict: 'Rejected', matched: 0, matchRate: 0 })
    expect(d.canApply).toBe(false)
    expect(d.needsForce).toBe(true)
    expect(d.tone).toBe('danger')
    // 同样是真的比对过——和 NeedsConfirm 共用同一句話。
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
    // 「有历史但读不出来」绝不能被当成「没有历史」放行。
    expect(d.headline).toContain('bad decrypt')
    // 这里压根没有比对过（Sampled/Matched 都是 0），所以不能说「记成删除再重传」——
    // 那句话对 NeedsConfirm/Rejected 是真的，对这个 verdict 是编的。
    expect(d.confirmBody).not.toContain('record every file that no longer matches as deleted')
    // 真正的后果：换根本身会成功，但索引读不出来是另一个问题，这个操作不解决它，
    // 下次备份大概率会栽在同一处（TrackedInfoStore.LoadAsync 没有 try/catch）。
    expect(d.confirmBody).toContain('index')
    expect(d.confirmBody).toContain('next backup')
  })

  it('an unknown verdict never silently allows the change', () => {
    const d = localRootDecision({ ...base, verdict: 'SomethingNew' })
    expect(d.canApply).toBe(false)
  })
})
