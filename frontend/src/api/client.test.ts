import { afterEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError, ApiTimeoutError } from './client'

/** A fetch that never answers on its own and only ends when its signal aborts — a stalled server. */
function stalledFetch() {
  return vi.fn((_input: RequestInfo | URL, init?: RequestInit) =>
    new Promise<Response>((_resolve, reject) => {
      init?.signal?.addEventListener('abort', () => reject(init.signal!.reason as unknown))
    }),
  )
}

describe('request deadline', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('a request the server never answers fails as a timeout, not as a hang', async () => {
    vi.stubGlobal('fetch', stalledFetch())
    const failure = await api.get('/backup-configs', { timeoutMs: 20 }).catch((e: unknown) => e)
    expect(failure).toBeInstanceOf(ApiTimeoutError)
    expect((failure as ApiTimeoutError).timeoutMs).toBe(20)
    expect((failure as ApiError).status).toBe(0)
    expect((failure as Error).message).toBe('No response from the server after 0.02 seconds.')
  })

  // A typeahead cancelling its previous request must still see the AbortError it asked for; only the
  // deadline's own abort is a timeout.
  it("a caller's own abort stays an abort", async () => {
    vi.stubGlobal('fetch', stalledFetch())
    const controller = new AbortController()
    const pending = api.get('/system/browse', { signal: controller.signal, timeoutMs: 10_000 }).catch((e: unknown) => e)
    controller.abort()
    const failure = await pending
    expect(failure).not.toBeInstanceOf(ApiTimeoutError)
    expect((failure as DOMException).name).toBe('AbortError')
  })

  it('a deadline of 0 means none', async () => {
    const fetched = stalledFetch()
    vi.stubGlobal('fetch', fetched)
    void api.get('/logs', { timeoutMs: 0 }).catch(() => {})
    await Promise.resolve()
    expect(fetched.mock.calls[0]![1]?.signal).toBeUndefined()
  })

  it('an answered request is unaffected by the deadline', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response('[1]', { status: 200 }))))
    expect(await api.get<number[]>('/groups', { timeoutMs: 20 })).toEqual([1])
  })
})
