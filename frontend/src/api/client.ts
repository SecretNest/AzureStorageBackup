// The base API client. Every request goes through /api (via the Vite proxy in development, via the
// reverse proxy in production).
//
// App remounts the login page when the session expires (design §6).
const BASE = '/api'
let onUnauthorized: (() => void) | null = null

export function setUnauthorizedHandler(handler: () => void) {
  onUnauthorized = handler
}

export class ApiError extends Error {
  status: number
  /** A machine-readable code the backend attaches in some cases, e.g. keyring_lost. */
  code?: string

  constructor(status: number, message: string, code?: string) {
    super(message)
    this.status = status
    this.code = code
    this.name = 'ApiError'
  }
}

/**
 * The request's deadline passed with nothing back — not a refusal, not an HTTP status (hence 0): the
 * server never answered. Worth its own type because "the server said no" and "nothing came back at
 * all" call for completely different things from the person reading the message.
 */
export class ApiTimeoutError extends ApiError {
  timeoutMs: number

  constructor(timeoutMs: number) {
    super(0, `No response from the server after ${timeoutMs / 1000} seconds.`)
    this.name = 'ApiTimeoutError'
    this.timeoutMs = timeoutMs
  }
}

/**
 * How long any request may go unanswered before it fails with ApiTimeoutError. Before this, no request
 * had a deadline: a GET queued behind the page's own polling on a server whose disk was saturated simply
 * never settled, and the page sat on "Loading…" for the rest of the session with nothing to say. Every
 * request this app makes is either a handful of local database rows or a bounded cloud listing, and the
 * long jobs — backup, check, restore, repair — are started and then polled, so a minute is far longer
 * than any of them can legitimately take and short enough that the user has not yet walked away.
 */
export const DEFAULT_TIMEOUT_MS = 60_000

export interface RequestOptions extends RequestInit {
  /** This request's deadline; 0 disables it. Defaults to DEFAULT_TIMEOUT_MS. */
  timeoutMs?: number
}

async function request<T>(path: string, init?: RequestOptions): Promise<T> {
  const { timeoutMs = DEFAULT_TIMEOUT_MS, signal, ...rest } = init ?? {}
  // The deadline rides alongside a caller's own signal (a typeahead cancelling its previous request),
  // not instead of it: whichever aborts first ends the request, and only the deadline's own abort is
  // reported as a timeout — a cancellation the caller asked for stays the AbortError it always was.
  const deadline = timeoutMs > 0 ? AbortSignal.timeout(timeoutMs) : undefined
  const combined = deadline && signal ? AbortSignal.any([deadline, signal]) : (deadline ?? signal)
  const timedOut = (e: unknown) => (deadline?.aborted ? new ApiTimeoutError(timeoutMs) : e)

  let res: Response
  try {
    res = await fetch(`${BASE}${path}`, {
      headers: { 'Content-Type': 'application/json' },
      // fetch defaults to same-origin, so the session cookie is not sent at all under a cross-origin
      // deployment (the SPA hosted separately), which would make the backend's AllowCredentials()
      // pointless. include is a superset of same-origin, so same-origin deployments are unaffected.
      credentials: 'include',
      ...rest,
      signal: combined,
    })
  } catch (e) {
    throw timedOut(e)
  }

  if (!res.ok) {
    if (res.status === 401) onUnauthorized?.()
    const text = await res.text().catch(() => '')

    // The backend reports errors uniformly as { error, code? }. Without parsing it, the user sees
    // the raw JSON — or, when the body is empty, a fallback to something as uninformative as
    // "Internal Server Error".
    let message = text || res.statusText
    let code: string | undefined
    try {
      const body = JSON.parse(text) as { error?: unknown; code?: unknown }
      if (typeof body.error === 'string' && body.error) message = body.error
      if (typeof body.code === 'string') code = body.code
    } catch {
      // Not JSON (an HTML error page from a reverse proxy, say): keep the raw text.
    }

    throw new ApiError(res.status, message, code)
  }

  // 204, no content — and any other success with an empty body (202 Accepted without a payload, say):
  // res.json() on an empty body throws "Unexpected end of JSON input", which surfaced as a red banner the
  // instant a suspend was accepted. An empty success simply has nothing to say.
  if (res.status === 204) return undefined as T
  // The body read is under the same deadline: the headers arriving does not mean the rest will.
  let body: string
  try {
    body = await res.text()
  } catch (e) {
    throw timedOut(e)
  }
  return (body ? JSON.parse(body) : undefined) as T
}

export const api = {
  get: <T>(path: string, init?: RequestOptions) => request<T>(path, init),
  post: <T>(path: string, body: unknown, init?: RequestOptions) =>
    request<T>(path, { ...init, method: 'POST', body: JSON.stringify(body) }),
  put: <T>(path: string, body: unknown, init?: RequestOptions) =>
    request<T>(path, { ...init, method: 'PUT', body: JSON.stringify(body) }),
  del: (path: string, init?: RequestOptions) => request<void>(path, { ...init, method: 'DELETE' }),
}
