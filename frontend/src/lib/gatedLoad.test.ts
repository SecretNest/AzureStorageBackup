import { describe, expect, it } from 'vitest'
import { gatedLoad } from './gatedLoad'
import { latestWins } from './latestWins'

function deferred<T>() {
  let resolve!: (v: T) => void
  let reject!: (e: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

describe('gatedLoad', () => {
  it('delivers and returns the result of the only request in flight', async () => {
    const gate = latestWins()
    const seen: string[][] = []
    const result = await gatedLoad(gate, () => Promise.resolve(['a']), (r) => seen.push(r), () => { throw new Error('no') })
    expect(result).toEqual(['a'])
    expect(seen).toEqual([['a']])
  })

  // The bug this guards: the mount load on a slow server, overtaken by the poll. The overtaken request
  // must deliver nothing — in particular it must not count as "loaded", or the table shows its empty
  // state over a list that has not arrived.
  it('a superseded request delivers neither its result nor "loaded"', async () => {
    const gate = latestWins()
    const first = deferred<string[]>()
    const second = deferred<string[]>()
    const delivered: string[][] = []
    const errors: unknown[] = []

    const p1 = gatedLoad(gate, () => first.promise, (r) => delivered.push(r), (e) => errors.push(e))
    const p2 = gatedLoad(gate, () => second.promise, (r) => delivered.push(r), (e) => errors.push(e))

    first.resolve(['stale'])
    expect(await p1).toBeNull()
    expect(delivered).toEqual([])

    second.resolve(['fresh'])
    expect(await p2).toEqual(['fresh'])
    expect(delivered).toEqual([['fresh']])
    expect(errors).toEqual([])
  })

  it('a superseded request keeps its error to itself as well', async () => {
    const gate = latestWins()
    const first = deferred<string[]>()
    const errors: unknown[] = []
    const p1 = gatedLoad(gate, () => first.promise, () => { throw new Error('no') }, (e) => errors.push(e))
    void gatedLoad(gate, () => new Promise<string[]>(() => {}), () => {}, (e) => errors.push(e))

    first.reject(new Error('stale failure'))
    expect(await p1).toBeNull()
    expect(errors).toEqual([])
  })

  it('the latest request delivers its error and resolves to null', async () => {
    const gate = latestWins()
    const errors: unknown[] = []
    const result = await gatedLoad(gate, () => Promise.reject(new Error('boom')), () => { throw new Error('no') }, (e) => errors.push(e))
    expect(result).toBeNull()
    expect(errors).toHaveLength(1)
    expect((errors[0] as Error).message).toBe('boom')
  })
})
