import { describe, expect, it } from 'vitest'
import { latestWins } from './latestWins'

describe('latestWins', () => {
  it('lets the only in-flight call through', () => {
    const gate = latestWins()
    expect(gate.begin()()).toBe(true)
  })

  // 要害就在这一条：后发的先回、先发的后回时，先发的那次绝不能再写状态。
  it('rejects an earlier call that resolves after a later one', () => {
    const gate = latestWins()
    const first = gate.begin()
    const second = gate.begin()

    expect(second()).toBe(true)
    expect(first()).toBe(false)
  })

  // 到达顺序不改变结论：谁最后发起的，谁说了算。
  it('keeps rejecting the stale call no matter the arrival order', () => {
    const gate = latestWins()
    const first = gate.begin()
    const second = gate.begin()

    expect(first()).toBe(false)
    expect(second()).toBe(true)
    expect(first()).toBe(false)
  })

  it('tracks each gate independently', () => {
    const a = latestWins()
    const b = latestWins()
    const fromA = a.begin()
    b.begin()
    expect(fromA()).toBe(true)
  })
})
