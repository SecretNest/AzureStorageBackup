import { describe, expect, it } from 'vitest'
import { latestWins } from './latestWins'

describe('latestWins', () => {
  it('lets the only in-flight call through', () => {
    const gate = latestWins()
    expect(gate.begin()()).toBe(true)
  })

  // This is the crux: when the later call returns first and the earlier one second, the earlier one must never write state again.
  it('rejects an earlier call that resolves after a later one', () => {
    const gate = latestWins()
    const first = gate.begin()
    const second = gate.begin()

    expect(second()).toBe(true)
    expect(first()).toBe(false)
  })

  // Arrival order does not change the conclusion: whoever started last wins.
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
