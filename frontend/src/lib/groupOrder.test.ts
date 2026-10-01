import { describe, expect, it } from 'vitest'
import { moveMember, toggleMember } from './groupOrder'

describe('toggleMember', () => {
  it('appends a new key at the end, so a newly ticked backup runs last', () => {
    expect(toggleMember(['1/a', '1/b'], '2/c')).toEqual(['1/a', '1/b', '2/c'])
  })
  it('removes a present key and keeps the others in order', () => {
    expect(toggleMember(['1/a', '1/b', '2/c'], '1/b')).toEqual(['1/a', '2/c'])
  })
})

describe('moveMember', () => {
  const order = ['1/a', '1/b', '2/c']
  it('moves a key one place up or down', () => {
    expect(moveMember(order, '1/b', -1)).toEqual(['1/b', '1/a', '2/c'])
    expect(moveMember(order, '1/b', 1)).toEqual(['1/a', '2/c', '1/b'])
  })
  it('leaves the first alone going up and the last alone going down', () => {
    expect(moveMember(order, '1/a', -1)).toEqual(order)
    expect(moveMember(order, '2/c', 1)).toEqual(order)
  })
  it('ignores a key that is not in the list', () => {
    expect(moveMember(order, '9/x', 1)).toEqual(order)
  })
  it('does not mutate its input', () => {
    const input = [...order]
    moveMember(input, '1/b', 1)
    expect(input).toEqual(order)
  })
})
